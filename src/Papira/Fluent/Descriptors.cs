using System.Globalization;
using Papira.Elements;

namespace Papira;

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
            var marker = new TextElement { Alignment = TextAlignment.Right };
            marker.Spans.Add(new TextSpan { Text = _marker(_start + i) });

            var markerItem = new RowItem(RowItemKind.Constant, markerWidth) { Child = marker };
            var contentItem = new RowItem(RowItemKind.Relative, 1) { Child = _items[i] };

            var row = new RowElement { Spacing = 6 };
            row.Items.Add(markerItem);
            row.Items.Add(contentItem);
            column.Items.Add(row);
        }

        return column;
    }
}

public sealed class TableDescriptor
{
    internal TableElement Element { get; } = new();

    public void ColumnsDefinition(Action<TableColumnsDescriptor> columns) => columns(new TableColumnsDescriptor(Element));

    /// <summary>Header cells, repeated at the top of every page the table spans.</summary>
    public void Header(Action<TableHeaderDescriptor> header) => header(new TableHeaderDescriptor(Element));

    public ITableCellContainer Cell()
    {
        var cell = new TableCell();
        Element.Cells.Add(cell);
        return cell;
    }
}

public sealed class TableColumnsDescriptor
{
    private readonly TableElement _table;

    internal TableColumnsDescriptor(TableElement table) => _table = table;

    public void ConstantColumn(float width) => _table.Columns.Add(new TableColumn(true, width));

    public void RelativeColumn(float size = 1) => _table.Columns.Add(new TableColumn(false, size));
}

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

public sealed class TextDescriptor
{
    private readonly TextElement _element;

    internal TextDescriptor(TextElement element) => _element = element;

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

    public TextSpanDescriptor AlignLeft() => SetAlignment(TextAlignment.Left);
    public TextSpanDescriptor AlignCenter() => SetAlignment(TextAlignment.Center);
    public TextSpanDescriptor AlignRight() => SetAlignment(TextAlignment.Right);
    public TextSpanDescriptor Justify() => SetAlignment(TextAlignment.Justify);

    private TextSpanDescriptor SetAlignment(TextAlignment alignment)
    {
        _element.Alignment = alignment;
        return this;
    }
}

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

    private ImageDescriptor Set(ImageScaling scaling)
    {
        _element.Scaling = scaling;
        return this;
    }
}

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
