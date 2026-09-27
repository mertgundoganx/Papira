using System.Globalization;
using System.Text;
using Papira.Elements;
using Papira.Infrastructure;

namespace Papira.Html;

/// <summary>
/// Turns a parsed document into Papira's own elements: blocks become a column, runs of text become
/// paragraphs, tables become tables. It is a document formatter, not a browser engine — there are no
/// floats, no flexbox and no absolute positioning — but it lays out the markup templates are written in.
/// </summary>
internal sealed class HtmlComposer(HtmlOptions options)
{
    /// <summary>Elements that start a block of their own rather than flowing with the text.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "dd", "div", "dl", "dt", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "img", "li", "main", "nav",
        "ol", "p", "pre", "section", "table", "ul",
    };

    private readonly StyleSheet _sheet = new();
    private readonly List<StyleTarget> _path = [];

    public void Compose(IContainer container, HtmlNode root)
    {
        foreach (var css in options.StyleSheets)
            _sheet.Add(css);

        CollectStyles(root);

        var body = Find(root, "body") ?? root;
        var style = new HtmlStyle
        {
            FontSize = options.BaseFontSize,
            Color = options.TextColor,
            Family = options.FontFamily,
        };

        Blocks(container, body, style);
    }

    private void CollectStyles(HtmlNode node)
    {
        if (node.Tag == "style")
        {
            foreach (var child in node.Children)
            {
                if (child.IsText)
                    _sheet.Add(child.Text);
            }

            return;
        }

        foreach (var child in node.Children)
            CollectStyles(child);
    }

    private static HtmlNode? Find(HtmlNode node, string tag)
    {
        foreach (var child in node.Children)
        {
            if (child.Tag == tag)
                return child;

            if (Find(child, tag) is { } found)
                return found;
        }

        return null;
    }

    // ---- blocks ----

    /// <summary>
    /// Lays out the children of an element: every block becomes an item of a column, and the text
    /// between the blocks is gathered into paragraphs of its own, as a browser does.
    /// </summary>
    private void Blocks(IContainer container, HtmlNode node, HtmlStyle style)
    {
        var groups = Split(node);
        if (groups.Count == 0)
            return;

        container.Column(column =>
        {
            // The space between two blocks is the larger of the two margins that meet, not their sum:
            // that is how CSS collapses them, and why a heading after a paragraph is not pushed twice.
            float previousMargin = 0;
            foreach (var group in groups)
            {
                if (group.Block is { } block)
                {
                    previousMargin = Block(column, block, style, previousMargin);
                }
                else
                {
                    Paragraph(column.Item(), group.Inline!, style, heading: 0);
                    previousMargin = 0;
                }
            }
        });
    }

    /// <summary>One block on its own, where there is no column to add page breaks to.</summary>
    private void BlockIn(IContainer container, HtmlNode node, HtmlStyle style) =>
        container.Column(column => Block(column, node, style, 0));

    private readonly record struct Flow(HtmlNode? Block, List<HtmlNode>? Inline);

    /// <summary>Splits the children into blocks and the runs of text between them.</summary>
    private List<Flow> Split(HtmlNode node)
    {
        var groups = new List<Flow>();
        List<HtmlNode>? run = null;

        foreach (var child in node.Children)
        {
            if (child.Tag is "head" or "script" or "style" or "title" or "meta" or "link" or "col" or "colgroup")
                continue;

            if (IsBlock(child))
            {
                if (run != null)
                {
                    groups.Add(new Flow(null, run));
                    run = null;
                }

                groups.Add(new Flow(child, null));
                continue;
            }

            // Whitespace between two blocks is not a paragraph of its own.
            if (child.IsText && run == null && child.Text.AsSpan().Trim().IsEmpty)
                continue;

            run ??= [];
            run.Add(child);
        }

        if (run != null && run.Exists(HasText))
            groups.Add(new Flow(null, run));

        return groups;
    }

    private static bool HasText(HtmlNode node)
    {
        if (node.IsText)
            return !node.Text.AsSpan().Trim().IsEmpty;

        if (node.Tag is "br" or "img")
            return true;

        return node.Children.Exists(HasText);
    }

    private bool IsBlock(HtmlNode node)
    {
        if (node.IsText)
            return false;

        if (Declaration(node, "display") is { } display)
        {
            if (display.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
                return false;

            if (display is "block" or "flex" or "grid" or "table" or "list-item")
                return true;
        }

        if (node.Tag == "a")
            return node.Children.Exists(child => IsBlock(child) || child.Tag == "img");

        return BlockTags.Contains(node.Tag);
    }

    /// <summary>A declaration of the style attribute, which is enough to tell how an element is displayed.</summary>
    private static string? Declaration(HtmlNode node, string property) =>
        node.Attribute("style") is { Length: > 0 } inline && StyleSheet.ParseDeclarations(inline).TryGetValue(property, out var value)
            ? value
            : null;

    /// <summary>Lays out one block and returns the margin it leaves below it, for the block after it.</summary>
    private float Block(ColumnDescriptor column, HtmlNode node, HtmlStyle parent, float previousMargin)
    {
        Push(node);
        try
        {
            var declarations = Declarations(node);
            var style = Inherit(parent, declarations);
            var box = Box(declarations, style, node);
            if (box.Hidden)
                return previousMargin;

            // A list inside a list item is part of that item, not a block of its own with space around it.
            if (node.Tag is "ul" or "ol" && _path.Exists(ancestor => ancestor.Tag == "li"))
                box = box with { Margin = new Edges(box.Margin.Left, 0, box.Margin.Right, 0) };

            if (box.BreakBefore)
                column.Item().PageBreak();

            // What the block above already left behind is taken off this block's own top margin.
            box = box with { Margin = box.Margin with { Top = Math.Max(0, box.Margin.Top - previousMargin) } };
            var container = Decorate(column.Item(), box);

            // An element with an id is where a link to "#id" jumps to.
            if (node.Attribute("id") is { Length: > 0 } id)
                container = container.Section(id);

            switch (node.Tag)
            {
                case "table":
                    Table(container, node, style);
                    break;

                case "ul" or "ol":
                    List(container, node, style, numbered: node.Tag == "ol");
                    break;

                case "img":
                    Picture(container, node, style);
                    break;

                case "hr":
                    container.LineHorizontal(box.Border.Top > 0 ? box.Border.Top : 0.75f).LineColor(box.BorderColor);
                    break;

                case "pre":
                    Preformatted(container, node, style);
                    break;

                case "a" when Link(node) is { } link:
                    var linked = link.Section != null ? container.SectionLink(link.Section) : container.Hyperlink(link.Uri!);
                    style.LinkUri = null;
                    style.LinkSection = null;
                    Blocks(linked, node, style);
                    break;

                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" when !node.Children.Exists(IsBlock):
                    Paragraph(container, node.Children, style, node.Tag[1] - '0');
                    break;

                default:
                    Blocks(container, node, style);
                    break;
            }

            if (box.BreakAfter)
                column.Item().PageBreak();

            return box.Margin.Bottom;
        }
        finally
        {
            Pop();
        }
    }

    /// <summary>Wraps the content in what the box around it says: margins, a background, borders, padding.</summary>
    private static IContainer Decorate(IContainer container, in HtmlBox box)
    {
        if (box.Margin.Any)
            container = container.Padding(box.Margin.Left, box.Margin.Top, box.Margin.Right, box.Margin.Bottom);

        if (box.Width is { } width)
            container = container.Width(width);

        container = box.Align switch
        {
            HorizontalAlignment.Center => container.AlignCenter(),
            HorizontalAlignment.Right => container.AlignRight(),
            _ => container,
        };

        if (box.KeepTogether)
            container = container.ShowEntire();

        if (box.Background is { } background)
            container = container.Background(background);

        if (box.Border.Any)
            container = container.Border(box.Border.Left, box.Border.Top, box.Border.Right, box.Border.Bottom)
                .BorderColor(box.BorderColor);

        if (box.Padding.Any)
            container = container.Padding(box.Padding.Left, box.Padding.Top, box.Padding.Right, box.Padding.Bottom);

        if (box.Height is { } height)
            container = container.Height(height);

        return container;
    }

    // ---- text ----

    private void Paragraph(IContainer container, List<HtmlNode> nodes, HtmlStyle style, int heading)
    {
        container.Text(text =>
        {
            if (style.Align is { } alignment)
            {
                _ = alignment switch
                {
                    TextAlignment.Center => text.AlignCenter(),
                    TextAlignment.Right => text.AlignRight(),
                    TextAlignment.Justify => text.Justify(),
                    _ => text.AlignLeft(),
                };
            }

            if (heading > 0)
                text.Heading(heading);

            var writer = new InlineWriter(text);
            foreach (var node in nodes)
                Inline(node, style, writer);
        });
    }

    private void Inline(HtmlNode node, HtmlStyle parent, InlineWriter writer)
    {
        if (node.IsText)
        {
            writer.Write(node.Text, parent);
            return;
        }

        if (node.Tag is "script" or "style" or "head" or "title")
            return;

        Push(node);
        try
        {
            var declarations = Declarations(node);
            var style = Inherit(parent, declarations);
            if (declarations.TryGetValue("display", out var display) && display == "none")
                return;

            switch (node.Tag)
            {
                case "br":
                    writer.Break();
                    return;

                case "img":
                    // A picture cannot sit inside a line of text, but the words that stand for it can.
                    if (node.Attribute("alt") is { Length: > 0 } alt)
                        writer.Write(alt, style);

                    return;

                case "a" when Link(node) is { } link:
                    style.LinkUri = link.Uri;
                    style.LinkSection = link.Section;
                    break;

                default:
                    break;
            }

            foreach (var child in node.Children)
                Inline(child, style, writer);
        }
        finally
        {
            Pop();
        }
    }

    /// <summary>Where a link leads: a named section of the document, or an address.</summary>
    private static (string? Uri, string? Section)? Link(HtmlNode node)
    {
        var href = node.Attribute("href");
        if (string.IsNullOrWhiteSpace(href))
            return null;

        href = href.Trim();
        return href.StartsWith('#') && href.Length > 1 ? (null, href[1..]) : (href, null);
    }

    /// <summary>
    /// Writes the text of a paragraph, collapsing runs of spaces and line breaks into single spaces the
    /// way a browser does, so that markup indented for reading does not come out full of gaps.
    /// </summary>
    private sealed class InlineWriter(TextDescriptor text)
    {
        private readonly StringBuilder _builder = new();
        private HtmlStyle? _spaceStyle;
        private bool _pendingSpace;
        private bool _any;

        public void Write(string raw, HtmlStyle style)
        {
            if (style.Preformatted)
            {
                Add(raw, style);
                return;
            }

            _builder.Clear();
            foreach (var ch in raw)
            {
                // Runs of spaces, tabs and line breaks all stand for a single space, as they do in a
                // browser; markup indented for reading must not come out full of gaps.
                if (ch is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    _pendingSpace = _any || _builder.Length > 0;

                    // The space belongs to the text it was written in, which is what it is drawn with.
                    _spaceStyle = style;
                    continue;
                }

                if (_pendingSpace)
                {
                    _pendingSpace = false;
                    if (_builder.Length > 0)
                    {
                        _builder.Append(' ');
                    }
                    else
                    {
                        // A space between two spans keeps the style of the text it was written in:
                        // drawn as part of a link it would be underlined and clickable, which the
                        // markup does not say.
                        Add(" ", _spaceStyle ?? style);
                    }
                }

                _builder.Append(ch);
            }

            if (_builder.Length > 0)
                Add(_builder.ToString(), style);
        }

        public void Break()
        {
            _pendingSpace = false;
            text.Span("\n");
            _any = false;
        }

        private void Add(string value, HtmlStyle style)
        {
            var span = text.Span(value).Style(style.ToTextStyle());
            if (style.LinkUri != null)
                span.Hyperlink(style.LinkUri);
            else if (style.LinkSection != null)
                span.SectionLink(style.LinkSection);

            _any = true;
        }
    }

    /// <summary>Preformatted text: the spaces and the line breaks are the ones in the markup.</summary>
    private static void Preformatted(IContainer container, HtmlNode node, HtmlStyle style)
    {
        var builder = new StringBuilder();
        Collect(node);

        var content = builder.ToString().Trim('\n');
        container.Text(text =>
        {
            var writer = new InlineWriter(text);
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                    writer.Write(lines[i].Replace("\t", "    ", StringComparison.Ordinal), style);

                if (i < lines.Length - 1)
                    writer.Break();
            }
        });

        void Collect(HtmlNode current)
        {
            foreach (var child in current.Children)
            {
                if (child.IsText)
                    builder.Append(child.Text);
                else if (child.Tag == "br")
                    builder.Append('\n');
                else
                    Collect(child);
            }
        }
    }

    // ---- lists ----

    private void List(IContainer container, HtmlNode node, HtmlStyle style, bool numbered)
    {
        var items = node.Children.Where(child => child.Tag == "li").ToList();
        if (items.Count == 0)
            return;

        void Build(ListDescriptor list)
        {
            if (numbered && int.TryParse(node.Attribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
                list.StartAt(start);

            if (Marker(style.ListStyle, node.Attribute("type")) is { } marker)
                list.Marker(marker);

            list.Spacing(0);
            foreach (var item in items)
                BlockIn(list.Item(), item, style);
        }

        if (numbered)
            container.NumberedList(Build);
        else
            container.List(Build);
    }

    /// <summary>The marker of a list whose kind was stated, e.g. "list-style-type: circle" or type="a".</summary>
    private static Func<int, string>? Marker(string? listStyle, string? type)
    {
        var kind = type switch
        {
            "1" => "decimal",
            "a" => "lower-alpha",
            "A" => "upper-alpha",
            "i" => "lower-roman",
            "I" => "upper-roman",
            "disc" or "circle" or "square" => type,
            _ => listStyle?.ToLowerInvariant(),
        };

        return kind switch
        {
            "circle" => _ => "◦",
            "square" => _ => "▪",
            "disc" => _ => "•",
            "none" => _ => string.Empty,
            "lower-alpha" or "lower-latin" => n => Alphabetic(n, 'a') + ".",
            "upper-alpha" or "upper-latin" => n => Alphabetic(n, 'A') + ".",
            "lower-roman" => n => Roman(n).ToLowerInvariant() + ".",
            "upper-roman" => n => Roman(n) + ".",
            _ => null,
        };
    }

    private static string Alphabetic(int number, char first)
    {
        var text = string.Empty;
        while (number > 0)
        {
            number--;
            text = (char)(first + (number % 26)) + text;
            number /= 26;
        }

        return text;
    }

    private static string Roman(int number)
    {
        if (number is <= 0 or > 3999)
            return number.ToString(CultureInfo.InvariantCulture);

        int[] values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] letters = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var builder = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (number >= values[i])
            {
                builder.Append(letters[i]);
                number -= values[i];
            }
        }

        return builder.ToString();
    }

    // ---- pictures ----

    private void Picture(IContainer container, HtmlNode node, HtmlStyle style)
    {
        var source = node.Attribute("src");
        if (string.IsNullOrWhiteSpace(source))
            return;

        var declarations = Declarations(node);
        var width = HtmlValues.Length(declarations.GetValueOrDefault("width") ?? node.Attribute("width"), style.FontSize);
        var height = HtmlValues.Length(declarations.GetValueOrDefault("height") ?? node.Attribute("height"), style.FontSize);

        if (width is { } w)
            container = container.Width(w);
        if (height is { } h)
            container = container.Height(h);

        var alt = node.Attribute("alt");
        if (options.LoadSvg(source) is { } drawing)
        {
            var svg = container.Svg(drawing);
            if (!string.IsNullOrEmpty(alt))
                svg.Alt(alt);

            return;
        }

        var picture = container.Image(options.LoadImage(source));
        if (!string.IsNullOrEmpty(alt))
            picture.Alt(alt);
    }

    // ---- tables ----

    /// <summary>A row of the table, with the section it sits in so that style rules can name both.</summary>
    private readonly record struct Row(HtmlNode? Section, HtmlNode Node);

    private void Table(IContainer container, HtmlNode node, HtmlStyle style)
    {
        var header = new List<Row>();
        var body = new List<Row>();
        CollectRows(node, header, body);

        // Rows of header cells at the top of a table are its header, whether they sit in a <thead> or not.
        var declared = header.Count > 0;
        while (!declared && body.Count > 1 && body[0].Node.Children.Any(IsCell) &&
            body[0].Node.Children.All(cell => cell.Tag != "td"))
        {
            header.Add(body[0]);
            body.RemoveAt(0);
        }

        var columns = Math.Max(1, header.Concat(body).Select(row => ColumnsOf(row.Node)).DefaultIfEmpty(1).Max());
        var widths = ColumnWidths(node, header.Concat(body).Select(row => row.Node).FirstOrDefault(), columns, style);
        var cellPadding = HtmlValues.Length(node.Attribute("cellpadding"), style.FontSize);
        var defaultBorder = HtmlValues.Length(node.Attribute("border"), style.FontSize) ?? 0;

        container.Table(table =>
        {
            table.ColumnsDefinition(definition =>
            {
                foreach (var (constant, value) in widths)
                {
                    if (constant)
                        definition.ConstantColumn(value);
                    else
                        definition.RelativeColumn(value);
                }
            });

            if (header.Count > 0)
            {
                table.Header(headerDescriptor => Rows(header, columns, style, cellPadding, defaultBorder,
                    () => headerDescriptor.Cell()));
            }

            Rows(body, columns, style, cellPadding, defaultBorder, table.Cell);
        });
    }

    private static void CollectRows(HtmlNode node, List<Row> header, List<Row> body)
    {
        foreach (var child in node.Children)
        {
            switch (child.Tag)
            {
                case "tr":
                    body.Add(new Row(null, child));
                    break;
                case "thead":
                    header.AddRange(Rows(child));
                    break;
                case "tbody" or "tfoot":
                    body.AddRange(Rows(child));
                    break;
                default:
                    break;
            }
        }

        static IEnumerable<Row> Rows(HtmlNode section) =>
            section.Children.Where(row => row.Tag == "tr").Select(row => new Row(section, row));
    }

    private static int ColumnsOf(HtmlNode row) =>
        row.Children.Where(IsCell).Sum(cell => Math.Max(1, Number(cell.Attribute("colspan"))));

    private static bool IsCell(HtmlNode node) => node.Tag is "td" or "th";

    private static int Number(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(1, value) : 1;

    /// <summary>
    /// The columns of the table, taken from its &lt;col&gt; elements or from the widths of the first row.
    /// A column without a width shares what is left over with the others.
    /// </summary>
    private List<(bool Constant, float Value)> ColumnWidths(HtmlNode table, HtmlNode? firstRow, int columns, HtmlStyle style)
    {
        var declared = new List<string?>();
        foreach (var child in table.Children)
        {
            if (child.Tag == "col")
                declared.Add(WidthOf(child));
            else if (child.Tag == "colgroup")
                declared.AddRange(child.Children.Where(c => c.Tag == "col").Select(WidthOf));
        }

        if (declared.Count == 0 && firstRow != null)
            declared.AddRange(firstRow.Children.Where(IsCell).Select(WidthOf));

        var widths = new List<(bool, float)>(columns);
        for (var i = 0; i < columns; i++)
        {
            var text = i < declared.Count ? declared[i] : null;
            if (text != null && text.Contains('%', StringComparison.Ordinal))
                widths.Add((false, Math.Max(0.01f, HtmlValues.Length(text, style.FontSize, 100) ?? 1)));
            else if (HtmlValues.Length(text, style.FontSize) is { } points and > 0)
                widths.Add((true, points));
            else
                widths.Add((false, 1));
        }

        return widths;

        string? WidthOf(HtmlNode node) =>
            Declaration(node, "width") ?? node.Attribute("width");
    }

    /// <summary>
    /// Writes the cells of the rows in the order the table expects them, filling the places a short row
    /// leaves empty so that the columns still line up.
    /// </summary>
    private void Rows(
        List<Row> rows,
        int columns,
        HtmlStyle style,
        float? cellPadding,
        float defaultBorder,
        Func<ITableCellContainer> cell)
    {
        var occupied = new int[columns];

        for (var r = 0; r < rows.Count; r++)
        {
            var (section, node) = rows[r];
            if (section != null)
                Push(section);

            Push(node);

            // A row has a style of its own: what it says is inherited by its cells, and the colour
            // behind it is the colour behind every cell that has none.
            var declarations = Declarations(node);
            var rowStyle = Inherit(style, declarations);
            var background = Box(declarations, rowStyle, node).Background;

            var column = 0;
            foreach (var child in node.Children.Where(IsCell))
            {
                while (column < columns && occupied[column] > r)
                    column++;

                if (column >= columns)
                    break;

                var columnSpan = Math.Min(Number(child.Attribute("colspan")), columns - column);
                var rowSpan = Number(child.Attribute("rowspan"));
                Cell(cell().ColumnSpan(columnSpan).RowSpan(rowSpan), child, rowStyle, cellPadding, defaultBorder, background);

                for (var c = column; c < column + columnSpan; c++)
                    occupied[c] = r + rowSpan;

                column += columnSpan;
            }

            // The places left over in this row, which the table would otherwise fill with the next row.
            for (; column < columns; column++)
            {
                if (occupied[column] > r)
                    continue;

                var filler = (IContainer)cell();
                if (background is { } color)
                    filler.Background(color);

                occupied[column] = r + 1;
            }

            Pop();
            if (section != null)
                Pop();
        }
    }

    private void Cell(ITableCellContainer cell, HtmlNode node, HtmlStyle parent, float? cellPadding, float defaultBorder, Color? rowBackground)
    {
        Push(node);
        try
        {
            var declarations = Declarations(node);
            var style = Inherit(parent, declarations);
            var box = Box(declarations, style, node);

            var container = (IContainer)cell;
            if ((box.Background ?? rowBackground) is { } background)
                container = container.Background(background);

            var border = box.Border.Any ? box.Border : new Edges(defaultBorder, defaultBorder, defaultBorder, defaultBorder);
            if (border.Any)
                container = container.Border(border.Left, border.Top, border.Right, border.Bottom).BorderColor(box.BorderColor);

            var padding = box.Padding.Any ? box.Padding : new Edges(cellPadding ?? 2, cellPadding ?? 2, cellPadding ?? 2, cellPadding ?? 2);
            container = container.Padding(padding.Left, padding.Top, padding.Right, padding.Bottom);

            container = box.Align switch
            {
                HorizontalAlignment.Center when style.Align == null => container.AlignCenter(),
                HorizontalAlignment.Right when style.Align == null => container.AlignRight(),
                _ => container,
            };

            Blocks(container, node, style);
        }
        finally
        {
            Pop();
        }
    }

    // ---- styles ----

    private void Push(HtmlNode node) =>
        _path.Add(new StyleTarget(node.Tag, node.Attribute("id"), node.Classes));

    private void Pop() => _path.RemoveAt(_path.Count - 1);

    /// <summary>
    /// Everything that applies to an element: the defaults of its tag, the attributes that carry
    /// presentation, the rules of the style sheets and finally its own style attribute.
    /// </summary>
    private Dictionary<string, string> Declarations(HtmlNode node)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (property, value) in HtmlDefaults.For(node.Tag))
            declarations[property] = value;

        Presentation(node, declarations);
        _sheet.Apply(_path, declarations);

        if (node.Attribute("style") is { Length: > 0 } inline)
        {
            foreach (var (property, value) in StyleSheet.ParseDeclarations(inline))
                declarations[property] = value;
        }

        return declarations;
    }

    /// <summary>The attributes older markup carries its presentation in.</summary>
    private static void Presentation(HtmlNode node, Dictionary<string, string> declarations)
    {
        if (node.Attributes == null)
            return;

        foreach (var (name, value) in node.Attributes)
        {
            switch (name)
            {
                case "align" when node.Tag is not ("img" or "table"):
                    declarations["text-align"] = value;
                    break;
                case "bgcolor":
                    declarations["background-color"] = value;
                    break;
                case "color":
                    declarations["color"] = value;
                    break;
                case "face":
                    declarations["font-family"] = value;
                    break;
                case "width" when node.Tag is not ("img" or "col" or "td" or "th" or "table"):
                    declarations["width"] = value;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The style an element passes on, which is its parent's with what it declares itself on top.</summary>
    private static HtmlStyle Inherit(HtmlStyle parent, Dictionary<string, string> declarations)
    {
        var style = parent.Clone();

        if (declarations.TryGetValue("font-size", out var size))
            style.FontSize = FontSize(size, parent.FontSize, ref style);

        if (declarations.TryGetValue("font-family", out var family) && HtmlValues.Family(family) is { } resolved)
            style.Family = resolved;

        if (declarations.TryGetValue("font-weight", out var weight) && HtmlValues.Weight(weight) is { } parsed)
            style.Weight = parsed;

        if (declarations.TryGetValue("font-style", out var fontStyle))
            style.Italic = fontStyle is "italic" or "oblique";

        if (declarations.TryGetValue("text-decoration", out var decoration) ||
            declarations.TryGetValue("text-decoration-line", out decoration))
        {
            style.Underline = decoration.Contains("underline", StringComparison.OrdinalIgnoreCase);
            style.Strikethrough = decoration.Contains("line-through", StringComparison.OrdinalIgnoreCase);
        }

        if (declarations.TryGetValue("color", out var color) && HtmlValues.Color(color) is { } parsedColor)
            style.Color = parsedColor;

        if (declarations.TryGetValue("line-height", out var lineHeight))
            style.LineHeight = LineHeight(lineHeight, style.FontSize) ?? style.LineHeight;

        if (declarations.TryGetValue("letter-spacing", out var spacing))
            style.LetterSpacing = HtmlValues.Length(spacing, style.FontSize) ?? style.LetterSpacing;

        if (declarations.TryGetValue("text-align", out var align) && HtmlValues.Align(align) is { } alignment)
            style.Align = alignment;

        if (declarations.TryGetValue("white-space", out var whiteSpace))
            style.Preformatted = whiteSpace is "pre" or "pre-wrap" or "break-spaces";

        if (declarations.TryGetValue("list-style-type", out var listStyle))
            style.ListStyle = listStyle;

        return style;
    }

    private static float FontSize(string text, float parent, ref HtmlStyle style)
    {
        style.FontSizeSet = true;
        return text.ToLowerInvariant() switch
        {
            "xx-small" => 9,
            "x-small" => 10,
            "small" => 10.5f,
            "medium" => 12,
            "large" => 13.5f,
            "x-large" => 18,
            "xx-large" => 24,
            "smaller" => parent * 0.83f,
            "larger" => parent * 1.2f,
            _ => HtmlValues.Length(text, parent, parent) ?? parent,
        };
    }

    /// <summary>A line height as a factor of the font size, which is how Papira states it.</summary>
    private static float? LineHeight(string text, float fontSize)
    {
        if (text.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return null;

        // A bare number is already a factor; a length has to be divided by the size of the text.
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
            return factor > 0 ? factor : null;

        var length = HtmlValues.Length(text, fontSize, fontSize);
        return length > 0 && fontSize > 0 ? length / fontSize : null;
    }

    /// <summary>What the element draws around its content.</summary>
    private static HtmlBox Box(Dictionary<string, string> declarations, HtmlStyle style, HtmlNode node)
    {
        var size = style.FontSize;
        var margin = Sides(declarations, "margin", size);
        var padding = Sides(declarations, "padding", size);
        var (border, borderColor) = Borders(declarations, size);

        var background = declarations.TryGetValue("background-color", out var color)
            ? HtmlValues.Color(color)
            : declarations.TryGetValue("background", out var shorthand)
                ? HtmlValues.Color(shorthand)
                : null;

        var width = declarations.TryGetValue("width", out var widthText) && !widthText.Contains('%', StringComparison.Ordinal)
            ? HtmlValues.Length(widthText, size)
            : null;

        var height = declarations.TryGetValue("height", out var heightText) && !heightText.Contains('%', StringComparison.Ordinal)
            ? HtmlValues.Length(heightText, size)
            : null;

        HorizontalAlignment? align = null;
        if (width != null && declarations.GetValueOrDefault("margin-left") == "auto" &&
            declarations.GetValueOrDefault("margin-right") == "auto")
        {
            align = HorizontalAlignment.Center;
            margin = new Edges(0, margin.Top, 0, margin.Bottom);
        }

        return new HtmlBox
        {
            Margin = margin,
            Padding = padding,
            Border = border,
            BorderColor = borderColor ?? Colors.Grey.Medium,
            Background = background,
            Width = width,
            Height = height,
            Hidden = declarations.GetValueOrDefault("display") == "none" ||
                declarations.GetValueOrDefault("visibility") == "hidden" ||
                node.Attributes?.ContainsKey("hidden") == true,
            BreakBefore = Breaks(declarations, "page-break-before") || Breaks(declarations, "break-before"),
            BreakAfter = Breaks(declarations, "page-break-after") || Breaks(declarations, "break-after"),
            KeepTogether = declarations.GetValueOrDefault("page-break-inside") == "avoid" ||
                declarations.GetValueOrDefault("break-inside") == "avoid",
            Align = align,
        };
    }

    private static bool Breaks(Dictionary<string, string> declarations, string property) =>
        declarations.GetValueOrDefault(property) is "always" or "page" or "left" or "right";

    private static Edges Sides(Dictionary<string, string> declarations, string property, float fontSize)
    {
        var edges = declarations.TryGetValue(property, out var shorthand)
            ? HtmlValues.Sides(shorthand, fontSize, default)
            : default;

        var left = HtmlValues.Length(declarations.GetValueOrDefault(property + "-left"), fontSize) ?? edges.Left;
        var top = HtmlValues.Length(declarations.GetValueOrDefault(property + "-top"), fontSize) ?? edges.Top;
        var right = HtmlValues.Length(declarations.GetValueOrDefault(property + "-right"), fontSize) ?? edges.Right;
        var bottom = HtmlValues.Length(declarations.GetValueOrDefault(property + "-bottom"), fontSize) ?? edges.Bottom;
        return new Edges(left, top, right, bottom);
    }

    private static (Edges Widths, Color? Color) Borders(Dictionary<string, string> declarations, float fontSize)
    {
        float left = 0, top = 0, right = 0, bottom = 0;
        Color? color = null;

        if (declarations.TryGetValue("border", out var all))
        {
            var (width, borderColor, none) = HtmlValues.Border(all, fontSize);
            left = top = right = bottom = none ? 0 : width;
            color ??= borderColor;
        }

        foreach (var side in (string[])["left", "top", "right", "bottom"])
        {
            if (!declarations.TryGetValue("border-" + side, out var text))
                continue;

            var (width, borderColor, none) = HtmlValues.Border(text, fontSize);
            Set(side, none ? 0 : width);
            color = borderColor ?? color;
        }

        if (declarations.TryGetValue("border-width", out var widths))
        {
            var edges = HtmlValues.Sides(widths, fontSize, new Edges(left, top, right, bottom));
            (left, top, right, bottom) = (edges.Left, edges.Top, edges.Right, edges.Bottom);
        }

        foreach (var side in (string[])["left", "top", "right", "bottom"])
        {
            if (declarations.TryGetValue($"border-{side}-width", out var text) && HtmlValues.Length(text, fontSize) is { } width)
                Set(side, width);
        }

        if (declarations.TryGetValue("border-color", out var colorText) && HtmlValues.Color(colorText.Split(' ')[0]) is { } parsed)
            color = parsed;

        if (declarations.GetValueOrDefault("border-style") is "none" or "hidden")
            left = top = right = bottom = 0;

        return (new Edges(left, top, right, bottom), color);

        void Set(string side, float width)
        {
            switch (side)
            {
                case "left":
                    left = width;
                    break;
                case "top":
                    top = width;
                    break;
                case "right":
                    right = width;
                    break;
                default:
                    bottom = width;
                    break;
            }
        }
    }
}
