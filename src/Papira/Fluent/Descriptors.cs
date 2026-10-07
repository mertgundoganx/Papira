using System.Globalization;
using Papira.Elements;

namespace Papira;

/// <summary>A column being filled: its items are stacked, and it breaks over pages between them.</summary>
public sealed class ColumnDescriptor
{
    internal ColumnElement Element { get; } = new();

    /// <summary>Vertical space between items, in points.</summary>
    public ColumnDescriptor Spacing(float value)
    {
        Element.Spacing = value;
        return this;
    }

    public IContainer Item()
    {
        var slot = new Slot();
        Element.Items.Add(slot);
        return slot;
    }
}

/// <summary>
/// A row being filled: its items stand side by side, each as wide as it is told to be — a width of its
/// own, a share of what is left over, or as wide as what it holds.
/// </summary>
public sealed class RowDescriptor
{
    internal RowElement Element { get; } = new();

    /// <summary>Horizontal space between items, in points.</summary>
    public RowDescriptor Spacing(float value)
    {
        Element.Spacing = value;
        return this;
    }

    /// <summary>Takes a share of the remaining width proportional to <paramref name="size"/>.</summary>
    public IContainer RelativeItem(float size = 1) => Add(RowItemKind.Relative, size);

    /// <summary>Fixed width in points.</summary>
    public IContainer ConstantItem(float width) => Add(RowItemKind.Constant, width);

    /// <summary>As wide as its content.</summary>
    public IContainer AutoItem() => Add(RowItemKind.Auto, 0);

    private RowItem Add(RowItemKind kind, float value)
    {
        var item = new RowItem(kind, value);
        Element.Items.Add(item);
        return item;
    }
}

/// <summary>
/// A table cell container; use <see cref="TableExtensions.ColumnSpan"/> and <see cref="TableExtensions.RowSpan"/>
/// to make it cover several columns or rows.
/// </summary>
public interface ITableCellContainer : IContainer;

/// <summary>What a cell of a table may say about how many columns and rows it covers.</summary>
public static class TableExtensions
{
    /// <summary>Makes the cell cover <paramref name="columns"/> columns (clamped to the column count).</summary>
    public static ITableCellContainer ColumnSpan(this ITableCellContainer cell, int columns)
    {
        AsCell(cell).ColumnSpan = Math.Max(1, columns);
        return cell;
    }

    /// <summary>
    /// Makes the cell cover <paramref name="rows"/> rows; cells of the following rows skip the columns it occupies.
    /// Rows connected by row spans are kept together on one page.
    /// </summary>
    public static ITableCellContainer RowSpan(this ITableCellContainer cell, int rows)
    {
        AsCell(cell).RowSpan = Math.Max(1, rows);
        return cell;
    }

    private static TableCell AsCell(ITableCellContainer cell) =>
        cell as TableCell ?? throw new ArgumentException("Table cells must be created with table.Cell() or header.Cell().", nameof(cell));
}

/// <summary>Items of a bulleted or numbered list.</summary>
public sealed class ListDescriptor
{
    private readonly List<Slot> _items = [];
    private readonly bool _numbered;
    private float _spacing = 4;
    private float? _markerWidth;
    private int _start = 1;
    private Func<int, string> _marker;

    internal ListDescriptor(bool numbered)
    {
        _numbered = numbered;
        _marker = numbered ? n => n.ToString(CultureInfo.InvariantCulture) + "." : _ => "\u2022";
    }

    /// <summary>Vertical space between items, in points. Default: 4.</summary>
    public ListDescriptor Spacing(float value)
    {
        _spacing = value;
        return this;
    }

    /// <summary>Width reserved for the markers, in points. Default: 12 for bullets, 22 for numbers.</summary>
    public ListDescriptor MarkerWidth(float value)
    {
        _markerWidth = value;
        return this;
    }

    /// <summary>Number of the first item of a numbered list. Default: 1.</summary>
    public ListDescriptor StartAt(int number)
    {
        _start = number;
        return this;
    }

    /// <summary>Marker text for the item with the given number, e.g. <c>n => $"{n})"</c> or <c>_ => "–"</c>.</summary>
    public ListDescriptor Marker(Func<int, string> marker)
    {
        _marker = marker ?? throw new ArgumentNullException(nameof(marker));
        return this;
    }

    public IContainer Item()
    {
        var slot = new Slot();
        _items.Add(slot);
        return slot;
    }

    /// <summary>A column of rows: a right-aligned marker column next to the item content.</summary>
    internal ColumnElement Build()
    {
        var column = new ColumnElement { Spacing = _spacing };
        var markerWidth = _markerWidth ?? (_numbered ? 22 : 12);

        for (var i = 0; i < _items.Count; i++)
        {
            var marker = new TextElement { Alignment = TextAlignment.Right, Role = "Lbl" };
            marker.Spans.Add(new TextSpan { Text = _marker(_start + i) });

            var markerItem = new RowItem(RowItemKind.Constant, markerWidth) { Child = marker };
            var contentItem = new RowItem(RowItemKind.Relative, 1) { Child = new TaggedElement("LBody") { Child = _items[i] } };

            var row = new RowElement { Spacing = 6 };
            row.Items.Add(markerItem);
            row.Items.Add(contentItem);

            // Every item of a tagged list is an element of its own, holding its marker and its text.
            column.Items.Add(new TaggedElement("LI") { Child = row });
        }

        return new ColumnElement { Spacing = _spacing, Items = { new TaggedElement("L") { Child = column } } };
    }
}

/// <summary>
/// A table being filled: its columns are defined once, its cells are added in order, and the rows of its
/// header are repeated at the top of every page the table runs onto.
/// </summary>
public sealed class TableDescriptor
{
    internal TableElement Element { get; } = new();

    public void ColumnsDefinition(Action<TableColumnsDescriptor> columns) => columns(new TableColumnsDescriptor(Element));

    /// <summary>Header cells, repeated at the top of every page the table spans.</summary>
    public void Header(Action<TableHeaderDescriptor> header) => header(new TableHeaderDescriptor(Element));

    /// <summary>Makes columns that are as wide as what they hold fill the whole width of the table.</summary>
    internal void Stretch(bool stretch) => Element.Stretch = stretch;

    public ITableCellContainer Cell()
    {
        var cell = new TableCell();
        Element.Cells.Add(cell);
        return cell;
    }
}

/// <summary>The columns of a table: a width of its own, a share of what is left, or as wide as its content.</summary>
public sealed class TableColumnsDescriptor
{
    private readonly TableElement _table;

    internal TableColumnsDescriptor(TableElement table) => _table = table;

    public void ConstantColumn(float width) => _table.Columns.Add(new TableColumn(TableColumnKind.Constant, width));

    /// <summary>
    /// A column as wide as what it holds. Where such columns need more room than there is, they share
    /// what there is between them in proportion to what each of them wanted.
    /// </summary>
    public void AutoColumn() => _table.Columns.Add(new TableColumn(TableColumnKind.Content, 0));

    public void RelativeColumn(float size = 1) => _table.Columns.Add(new TableColumn(TableColumnKind.Relative, size));
}

/// <summary>The cells of the header of a table, which are drawn again at the top of every page.</summary>
public sealed class TableHeaderDescriptor
{
    private readonly TableElement _table;

    internal TableHeaderDescriptor(TableElement table) => _table = table;

    public ITableCellContainer Cell()
    {
        var cell = new TableCell();
        _table.HeaderCells.Add(cell);
        return cell;
    }
}

/// <summary>
/// A piece of text being written: spans that may each have a style of their own, line breaks, links,
/// and the number of the page the text ends up on.
/// </summary>
public sealed class TextDescriptor
{
    private readonly TextElement _element;

    internal TextDescriptor(TextElement element) => _element = element;

    /// <summary>Keeps the space the text ends with, for text that something is drawn right after.</summary>
    internal void KeepTrailingSpace() => _element.KeepTrailingSpace = true;

    public TextSpanDescriptor Span(string? text) => Add(new TextSpan { Text = text ?? string.Empty });

    /// <summary>Adds text followed by a line break.</summary>
    public TextSpanDescriptor Line(string? text) => Span((text ?? string.Empty) + "\n");

    public TextSpanDescriptor EmptyLine() => Span("\n");

    public TextSpanDescriptor CurrentPageNumber() =>
        Add(new TextSpan { DynamicText = c => c.PageNumber.ToString(CultureInfo.InvariantCulture) });

    public TextSpanDescriptor TotalPages() =>
        Add(new TextSpan
        {
            DynamicText = c =>
            {
                c.TotalPagesRequested = true;
                return c.TotalPages.ToString(CultureInfo.InvariantCulture);
            },
        });

    /// <summary>Style applied to all spans of this text block (spans can still override it).</summary>
    public TextDescriptor DefaultTextStyle(TextStyle style)
    {
        _element.ParagraphStyle = style;
        return this;
    }

    public TextDescriptor DefaultTextStyle(Func<TextStyle, TextStyle> style) => DefaultTextStyle(style(TextStyle.Default));

    public TextDescriptor AlignLeft() => SetAlignment(TextAlignment.Left);
    public TextDescriptor AlignCenter() => SetAlignment(TextAlignment.Center);
    public TextDescriptor AlignRight() => SetAlignment(TextAlignment.Right);
    public TextDescriptor Justify() => SetAlignment(TextAlignment.Justify);

    /// <summary>
    /// Marks the text as a heading of the given level, from 1 to 6. In a tagged document the headings
    /// are what a reader for the blind navigates by.
    /// </summary>
    public TextDescriptor Heading(int level)
    {
        _element.Role = "H" + Math.Clamp(level, 1, 6).ToString(CultureInfo.InvariantCulture);
        return this;
    }

    /// <summary>
    /// The direction the paragraph reads in. The default takes it from the first strongly directional
    /// character, so Arabic and Hebrew text is laid out right to left without any setting.
    /// </summary>
    public TextDescriptor Direction(TextDirection direction)
    {
        _element.Direction = direction;
        return this;
    }

    /// <summary>Lays the paragraph out right to left, whatever it starts with.</summary>
    public TextDescriptor RightToLeft() => Direction(TextDirection.RightToLeft);

    private TextDescriptor SetAlignment(TextAlignment alignment)
    {
        _element.Alignment = alignment;
        return this;
    }

    private TextSpanDescriptor Add(TextSpan span)
    {
        _element.Spans.Add(span);
        return new TextSpanDescriptor(_element, span);
    }
}

/// <summary>Styles one span of text. Alignment methods apply to the whole text block.</summary>
public sealed class TextSpanDescriptor
{
    private readonly TextElement _element;
    private readonly TextSpan _span;

    internal TextSpanDescriptor(TextElement element, TextSpan span)
    {
        _element = element;
        _span = span;
    }

    private TextSpanDescriptor Update(Func<TextStyle, TextStyle> change)
    {
        _span.Style = change(_span.Style);
        return this;
    }

    public TextSpanDescriptor Style(TextStyle style) => Update(s => style.InheritFrom(s));

    /// <summary>Sets the font family, with optional fallback families for characters it lacks.</summary>
    public TextSpanDescriptor FontFamily(string family, params string[] fallbacks) => Update(s => s.FontFamily(family, fallbacks));
    public TextSpanDescriptor FontSize(float size) => Update(s => s.FontSize(size));
    public TextSpanDescriptor FontColor(Color color) => Update(s => s.FontColor(color));
    public TextSpanDescriptor FontWeight(FontWeight weight) => Update(s => s.FontWeight(weight));
    public TextSpanDescriptor Thin() => FontWeight(Papira.FontWeight.Thin);
    public TextSpanDescriptor Light() => FontWeight(Papira.FontWeight.Light);
    public TextSpanDescriptor Medium() => FontWeight(Papira.FontWeight.Medium);
    public TextSpanDescriptor SemiBold() => FontWeight(Papira.FontWeight.SemiBold);
    public TextSpanDescriptor Bold() => FontWeight(Papira.FontWeight.Bold);
    public TextSpanDescriptor Black() => FontWeight(Papira.FontWeight.Black);
    public TextSpanDescriptor Italic(bool value = true) => Update(s => s.Italic(value));
    public TextSpanDescriptor Underline(bool value = true) => Update(s => s.Underline(value));
    public TextSpanDescriptor Strikethrough(bool value = true) => Update(s => s.Strikethrough(value));
    public TextSpanDescriptor LineHeight(float factor) => Update(s => s.LineHeight(factor));
    public TextSpanDescriptor LetterSpacing(float points) => Update(s => s.LetterSpacing(points));

    /// <summary>
    /// Makes this piece of the paragraph open an address when it is clicked. Use
    /// <see cref="ContainerExtensions.Hyperlink"/> to make a whole block clickable instead.
    /// </summary>
    public TextSpanDescriptor Hyperlink(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _span.Uri = uri;
        _span.Section = null;
        return this;
    }

    /// <summary>Makes this piece of the paragraph jump to a named section of the document.</summary>
    public TextSpanDescriptor SectionLink(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        _span.Section = section;
        _span.Uri = null;
        return this;
    }

    public TextSpanDescriptor AlignLeft() => SetAlignment(TextAlignment.Left);
    public TextSpanDescriptor AlignCenter() => SetAlignment(TextAlignment.Center);
    public TextSpanDescriptor AlignRight() => SetAlignment(TextAlignment.Right);
    public TextSpanDescriptor Justify() => SetAlignment(TextAlignment.Justify);

    /// <summary>Marks the text as a heading of the given level; see <see cref="TextDescriptor.Heading"/>.</summary>
    public TextSpanDescriptor Heading(int level)
    {
        _element.Role = "H" + Math.Clamp(level, 1, 6).ToString(CultureInfo.InvariantCulture);
        return this;
    }

    /// <summary>The direction of the whole paragraph; see <see cref="TextDescriptor.Direction"/>.</summary>
    public TextSpanDescriptor Direction(TextDirection direction)
    {
        _element.Direction = direction;
        return this;
    }

    /// <summary>Lays the paragraph out right to left, whatever it starts with.</summary>
    public TextSpanDescriptor RightToLeft() => Direction(TextDirection.RightToLeft);

    private TextSpanDescriptor SetAlignment(TextAlignment alignment)
    {
        _element.Alignment = alignment;
        return this;
    }
}

/// <summary>A picture that has been placed: how it is fitted into its space, and what it shows.</summary>
public sealed class ImageDescriptor
{
    private readonly ImageElement _element;

    internal ImageDescriptor(ImageElement element) => _element = element;

    /// <summary>Scales to the available width (default).</summary>
    public ImageDescriptor FitWidth() => Set(ImageScaling.FitWidth);

    /// <summary>Scales to the available height.</summary>
    public ImageDescriptor FitHeight() => Set(ImageScaling.FitHeight);

    /// <summary>Scales to fit entirely in the available area, keeping the aspect ratio.</summary>
    public ImageDescriptor FitArea() => Set(ImageScaling.FitArea);

    /// <summary>Stretches the picture to the whole area it is given, whatever its own shape.</summary>
    public ImageDescriptor Stretch() => Set(ImageScaling.Stretch);

    /// <summary>Scales the picture until it covers the whole area, cutting off what hangs over the edges.</summary>
    public ImageDescriptor Cover() => Set(ImageScaling.Cover);

    /// <summary>
    /// What a reader for the blind announces in place of the picture. Every picture of a tagged document
    /// needs one, unless it is decoration that says nothing.
    /// </summary>
    public ImageDescriptor Alt(string text)
    {
        _element.Alt = text;
        return this;
    }

    private ImageDescriptor Set(ImageScaling scaling)
    {
        _element.Scaling = scaling;
        return this;
    }
}

/// <summary>Chooses how a vector drawing is scaled into the space it is given.</summary>
public sealed class SvgDescriptor
{
    private readonly SvgElement _element;

    internal SvgDescriptor(SvgElement element) => _element = element;

    /// <summary>Scales to the available width (default).</summary>
    public SvgDescriptor FitWidth() => Set(ImageScaling.FitWidth);

    /// <summary>Scales to the available height.</summary>
    public SvgDescriptor FitHeight() => Set(ImageScaling.FitHeight);

    /// <summary>Scales to fit entirely in the available area, keeping the aspect ratio.</summary>
    public SvgDescriptor FitArea() => Set(ImageScaling.FitArea);

    /// <summary>Stretches the drawing to the whole area it is given, whatever its own shape.</summary>
    public SvgDescriptor Stretch() => Set(ImageScaling.Stretch);

    /// <summary>Scales the drawing until it covers the whole area, cutting off what hangs over the edges.</summary>
    public SvgDescriptor Cover() => Set(ImageScaling.Cover);

    /// <summary>What a reader for the blind announces in place of the drawing.</summary>
    public SvgDescriptor Alt(string text)
    {
        _element.Alt = text;
        return this;
    }

    private SvgDescriptor Set(ImageScaling scaling)
    {
        _element.Scaling = scaling;
        return this;
    }
}

/// <summary>A rule that has been drawn: what colour it is.</summary>
public sealed class LineDescriptor
{
    private readonly LineElement _element;

    internal LineDescriptor(LineElement element) => _element = element;

    public LineDescriptor LineColor(Color color)
    {
        _element.Color = color;
        return this;
    }
}

/// <summary>What every field of a form has in common: whether it can be filled in, and how it looks.</summary>
/// <typeparam name="T">The descriptor itself, so that the calls of a derived type can be chained.</typeparam>
public abstract class FormFieldDescriptor<T>
    where T : FormFieldDescriptor<T>
{
    internal FormFieldDescriptor(FormField field) => Field = field;

    internal FormField Field { get; }

    /// <summary>
    /// What the field is for, in words. Shown as a tooltip, announced by a reader for the blind, and
    /// required of a field in a document that has to be accessible. Defaults to the name of the field.
    /// </summary>
    public T Tooltip(string text)
    {
        Field.Tooltip = text;
        return (T)this;
    }

    /// <summary>Marks the field as one that has to be filled in before the form is sent.</summary>
    public T Required(bool required = true)
    {
        Field.Required = required;
        return (T)this;
    }

    /// <summary>Shows the value without letting a reader change it.</summary>
    public T ReadOnly(bool readOnly = true)
    {
        Field.ReadOnly = readOnly;
        return (T)this;
    }

    /// <summary>The colour behind the field; by default the page shows through.</summary>
    public T BackgroundColor(Color color)
    {
        Field.BackgroundColor = color;
        return (T)this;
    }

    /// <summary>The colour of the frame around the field.</summary>
    public T BorderColor(Color color)
    {
        Field.BorderColor = color;
        return (T)this;
    }

    /// <summary>The height of the box, in points, instead of the one its text style implies.</summary>
    public T Height(float height)
    {
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "The height of a form field must be positive.");

        Field.RequestedHeight = height;
        return (T)this;
    }
}

/// <summary>A field a reader types into.</summary>
public sealed class TextFieldDescriptor : FormFieldDescriptor<TextFieldDescriptor>
{
    internal TextFieldDescriptor(FormField field) : base(field)
    {
    }

    /// <summary>The text the field starts out with.</summary>
    public TextFieldDescriptor Value(string text)
    {
        Field.Value = text;
        return this;
    }

    /// <summary>Lets the field hold several lines; it is three lines tall unless a height is given.</summary>
    public TextFieldDescriptor Multiline(bool multiline = true)
    {
        Field.Multiline = multiline;
        return this;
    }

    /// <summary>The largest number of characters the field accepts.</summary>
    public TextFieldDescriptor MaxLength(int characters)
    {
        if (characters <= 0)
            throw new ArgumentOutOfRangeException(nameof(characters), "The length limit of a text field must be positive.");

        Field.MaxLength = characters;
        return this;
    }
}

/// <summary>A box a reader ticks.</summary>
public sealed class CheckboxDescriptor : FormFieldDescriptor<CheckboxDescriptor>
{
    internal CheckboxDescriptor(FormField field) : base(field)
    {
    }

    /// <summary>Starts out ticked.</summary>
    public CheckboxDescriptor Checked(bool ticked = true)
    {
        Field.Checked = ticked;
        return this;
    }
}

/// <summary>One button of a group, of which a reader picks exactly one.</summary>
public sealed class RadioDescriptor : FormFieldDescriptor<RadioDescriptor>
{
    internal RadioDescriptor(FormField field) : base(field)
    {
    }

    /// <summary>Starts out as the button of the group that is chosen.</summary>
    public RadioDescriptor Checked(bool chosen = true)
    {
        Field.Checked = chosen;
        return this;
    }
}

/// <summary>A place for a signature: a field a reader signs, or a line to sign by hand.</summary>
public sealed class SignatureFieldDescriptor : FormFieldDescriptor<SignatureFieldDescriptor>
{
    internal SignatureFieldDescriptor(FormField field) : base(field)
    {
    }
}

/// <summary>A list a reader picks one entry from.</summary>
public sealed class DropdownDescriptor : FormFieldDescriptor<DropdownDescriptor>
{
    internal DropdownDescriptor(FormField field) : base(field)
    {
    }

    /// <summary>The entry the field starts out with; it has to be one of the options.</summary>
    public DropdownDescriptor Value(string option)
    {
        if (!Field.Options.Contains(option, StringComparer.Ordinal))
            throw new ArgumentException($"'{option}' is not one of the options of the dropdown '{Field.Name}'.", nameof(option));

        Field.Value = option;
        return this;
    }
}
