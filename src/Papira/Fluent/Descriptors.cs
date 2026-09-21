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

/// <summary>A table cell container; use <see cref="TableExtensions.ColumnSpan"/> to span columns.</summary>
public interface ITableCellContainer : IContainer;

public static class TableExtensions
{
    public static IContainer ColumnSpan(this ITableCellContainer cell, int columns)
    {
        if (cell is not TableCell tableCell)
            throw new ArgumentException("Table cells must be created with table.Cell() or header.Cell().", nameof(cell));

        tableCell.ColumnSpan = Math.Max(1, columns);
        return tableCell;
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
    public TextSpanDescriptor FontFamily(string family) => Update(s => s.FontFamily(family));
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
