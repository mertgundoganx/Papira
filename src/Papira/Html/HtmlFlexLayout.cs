using System.Globalization;
using Papira.Elements;
using Papira.Infrastructure;

namespace Papira.Html;

/// <summary>
/// The two layouts that put several elements beside one another: a flexible box, where the items share
/// out the space along a line, and a grid, where they fall into columns of a stated width.
/// </summary>
internal sealed partial class HtmlComposer
{
    // ---- flexible boxes ----

    private void Flex(IContainer container, HtmlNode node, HtmlStyle style, Dictionary<string, string> declarations, in HtmlBox box)
    {
        var (direction, wrap) = Direction(declarations);
        var justify = Justify(declarations.GetValueOrDefault("justify-content"));
        var align = Align(declarations.GetValueOrDefault("align-items")) ?? FlexAlign.Stretch;
        var (rowGap, columnGap) = Gaps(declarations, style.FontSize);
        var groups = Cells(node);
        if (groups.Count == 0)
            return;

        // A column of items that only stacks is an ordinary column of blocks. Laid out that way it goes on
        // breaking over pages, which a flexible box — laid out as one piece — cannot.
        if (direction == FlexDirection.Column && !wrap && justify == FlexJustify.Start && align == FlexAlign.Stretch)
        {
            container.Column(column =>
            {
                column.Spacing(rowGap);
                foreach (var group in groups)
                {
                    // The margins of the items of a flexible box do not collapse into one another.
                    if (group.Block is { } block)
                        Block(column, block, style, 0);
                    else
                        Paragraph(column.Item(), group.Inline!, style, heading: 0);
                }
            });

            return;
        }

        var element = new FlexElement
        {
            Direction = direction,
            Wrap = wrap,
            Justify = justify,
            Align = align,
            RowGap = rowGap,
            ColumnGap = columnGap,
            MainSizeFixed = direction is FlexDirection.Row or FlexDirection.RowReverse || box.Height != null,
        };

        var horizontal = direction is FlexDirection.Row or FlexDirection.RowReverse;
        foreach (var group in groups)
        {
            if (group.Block is { } block)
                Item(element, block, style, horizontal);
            else
                Paragraph(Add(element, new FlexItem()), group.Inline!, style, heading: 0);
        }

        container.Assign(element);
    }

    /// <summary>
    /// What a flexible box or a grid puts in its places. Every element of such a container is a place of
    /// its own, whether it would otherwise flow with the text or not — that is what CSS means by saying
    /// that the items of a flexible box are made into blocks. Only loose text is gathered into a place.
    /// </summary>
    private static List<Flow> Cells(HtmlNode node)
    {
        var groups = new List<Flow>();
        List<HtmlNode>? run = null;

        foreach (var child in node.Children)
        {
            if (child.Tag is "head" or "script" or "style" or "title" or "meta" or "link" or "col" or "colgroup")
                continue;

            if (!child.IsText)
            {
                if (run != null)
                {
                    groups.Add(new Flow(null, run));
                    run = null;
                }

                groups.Add(new Flow(child, null));
                continue;
            }

            // The spaces and line breaks that markup is indented with are not items of their own.
            if (child.Text.AsSpan().Trim().IsEmpty)
                continue;

            run ??= [];
            run.Add(child);
        }

        if (run != null)
            groups.Add(new Flow(null, run));

        return groups;
    }

    /// <summary>One item of a flexible box, with what it says about taking and giving up space.</summary>
    private void Item(FlexElement element, HtmlNode node, HtmlStyle parent, bool horizontal)
    {
        Push(node);
        try
        {
            var declarations = Declarations(node);
            var style = Inherit(parent, declarations);
            var box = Box(declarations, style, node);
            if (box.Hidden)
                return;

            var (grow, shrink, basis) = Flexibility(declarations, style.FontSize);

            // Without a basis of its own an item starts from the size it was given along the line, and that
            // size is then the flexible box's to change — so it is taken off the box around the item.
            var stated = horizontal ? box.Width : box.Height;
            var item = new FlexItem
            {
                Grow = grow,
                Shrink = shrink,
                Basis = basis ?? stated,
                Edges = horizontal
                    ? box.Padding.Left + box.Padding.Right + box.Border.Left + box.Border.Right
                    : box.Padding.Top + box.Padding.Bottom + box.Border.Top + box.Border.Bottom,
                Margins = horizontal ? box.Margin.Left + box.Margin.Right : box.Margin.Top + box.Margin.Bottom,
            };

            if (Align(declarations.GetValueOrDefault("align-self")) is { } self)
                item.AlignSelf = self;

            var inner = horizontal ? box with { Width = null } : box with { Height = null };
            Fill(Add(element, item), node, style, inner, declarations);
        }
        finally
        {
            Pop();
        }
    }

    private static FlexItem Add(FlexElement element, FlexItem item)
    {
        element.Items.Add(item);
        return item;
    }

    /// <summary>The direction and the wrapping, which <c>flex-flow</c> may state together.</summary>
    private static (FlexDirection Direction, bool Wrap) Direction(Dictionary<string, string> declarations)
    {
        var direction = FlexDirection.Row;
        var wrap = false;

        foreach (var property in (string[])["flex-flow", "flex-direction", "flex-wrap"])
        {
            if (!declarations.TryGetValue(property, out var value))
                continue;

            foreach (var word in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word.ToLowerInvariant())
                {
                    case "column":
                        direction = FlexDirection.Column;
                        break;
                    case "column-reverse":
                        direction = FlexDirection.ColumnReverse;
                        break;
                    case "row":
                        direction = FlexDirection.Row;
                        break;
                    case "row-reverse":
                        direction = FlexDirection.RowReverse;
                        break;
                    case "wrap" or "wrap-reverse":
                        wrap = true;
                        break;
                    case "nowrap":
                        wrap = false;
                        break;
                    default:
                        break;
                }
            }
        }

        return (direction, wrap);
    }

    private static FlexJustify Justify(string? value) => value?.ToLowerInvariant() switch
    {
        "center" => FlexJustify.Center,
        "flex-end" or "end" or "right" => FlexJustify.End,
        "space-between" => FlexJustify.SpaceBetween,
        "space-around" => FlexJustify.SpaceAround,
        "space-evenly" => FlexJustify.SpaceEvenly,
        _ => FlexJustify.Start,
    };

    private static FlexAlign? Align(string? value) => value?.ToLowerInvariant() switch
    {
        "center" => FlexAlign.Center,
        "flex-start" or "start" or "self-start" => FlexAlign.Start,
        "flex-end" or "end" or "self-end" => FlexAlign.End,
        "baseline" or "first baseline" or "last baseline" => FlexAlign.Baseline,
        "stretch" or "normal" => FlexAlign.Stretch,
        _ => null,
    };

    /// <summary>
    /// How eagerly an item takes the space left over, how readily it gives space up, and the size it
    /// starts from. The <c>flex</c> shorthand states all three; "flex: 1" starts from nothing, which is
    /// why such items share the line equally however much each of them holds.
    /// </summary>
    private static (float Grow, float Shrink, CssLength? Basis) Flexibility(Dictionary<string, string> declarations, float fontSize)
    {
        float grow = 0, shrink = 1;
        CssLength? basis = null;

        if (declarations.TryGetValue("flex", out var shorthand))
        {
            var words = shorthand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words is ["none"])
            {
                (grow, shrink) = (0, 0);
            }
            else if (words is ["auto"])
            {
                (grow, shrink) = (1, 1);
            }
            else if (words is ["initial"])
            {
                (grow, shrink) = (0, 1);
            }
            else
            {
                var numbers = 0;
                foreach (var word in words)
                {
                    if (numbers < 2 && float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        if (numbers == 0)
                            grow = Math.Max(0, number);
                        else
                            shrink = Math.Max(0, number);

                        numbers++;
                        continue;
                    }

                    // The third value is the size to start from; "flex: 1" leaves it out and means zero.
                    basis = word.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : HtmlValues.Measure(word, fontSize);
                }

                basis ??= numbers > 0 && !words.Contains("auto", StringComparer.OrdinalIgnoreCase) ? new CssLength(0) : null;
            }
        }

        if (declarations.TryGetValue("flex-grow", out var growText) &&
            float.TryParse(growText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedGrow))
        {
            grow = Math.Max(0, parsedGrow);
        }

        if (declarations.TryGetValue("flex-shrink", out var shrinkText) &&
            float.TryParse(shrinkText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedShrink))
        {
            shrink = Math.Max(0, parsedShrink);
        }

        if (declarations.TryGetValue("flex-basis", out var basisText))
            basis = HtmlValues.Measure(basisText, fontSize);

        return (grow, shrink, basis);
    }

    /// <summary>The gaps between rows and between columns, which one declaration may state together.</summary>
    private static (float Row, float Column) Gaps(Dictionary<string, string> declarations, float fontSize)
    {
        float row = 0, column = 0;

        foreach (var property in (string[])["gap", "grid-gap"])
        {
            if (!declarations.TryGetValue(property, out var value))
                continue;

            var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            row = HtmlValues.Length(parts.ElementAtOrDefault(0), fontSize) ?? row;
            column = HtmlValues.Length(parts.ElementAtOrDefault(1) ?? parts.ElementAtOrDefault(0), fontSize) ?? column;
        }

        foreach (var property in (string[])["row-gap", "grid-row-gap"])
            row = HtmlValues.Length(declarations.GetValueOrDefault(property), fontSize) ?? row;

        foreach (var property in (string[])["column-gap", "grid-column-gap"])
            column = HtmlValues.Length(declarations.GetValueOrDefault(property), fontSize) ?? column;

        return (Math.Max(0, row), Math.Max(0, column));
    }

    // ---- boxes in a line of text ----

    /// <summary>
    /// A line of text that holds boxes which draw themselves — what <c>display: inline-block</c> asks for.
    /// It is laid out as a line that wraps and whose pieces are lined up on their text, which is how such
    /// boxes sit among words. Each box keeps its own size, padding and background, which it would lose
    /// were it simply written out as more text.
    /// </summary>
    private void InlineBlocks(IContainer container, List<HtmlNode> nodes, HtmlStyle style)
    {
        var element = new FlexElement
        {
            Wrap = true,
            Align = FlexAlign.Baseline,
            MainSizeFixed = true,
            Strut = style.ToTextStyle(),
        };

        var groups = Runs(nodes);
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Block is { } block)
                Item(element, block, style, horizontal: true);
            else
                Paragraph(Add(element, new FlexItem()), groups[i].Inline!, style, 0, i > 0, i < groups.Count - 1);
        }

        container.Assign(element);
    }

    /// <summary>
    /// Splits the children into the boxes that draw themselves and the runs of text between them. The
    /// spaces between two such boxes are a run of their own, because a space between them is drawn.
    /// </summary>
    private List<Flow> Runs(List<HtmlNode> nodes)
    {
        var groups = new List<Flow>();
        List<HtmlNode>? run = null;

        foreach (var child in nodes)
        {
            if (child.Tag is "head" or "script" or "style" or "title" or "meta" or "link")
                continue;

            if (IsInlineBlock(child) || IsBlock(child))
            {
                if (run != null)
                {
                    groups.Add(new Flow(null, run));
                    run = null;
                }

                groups.Add(new Flow(child, null));
                continue;
            }

            // Nothing stands before the first box, so the space the markup is indented with is not drawn.
            if (groups.Count == 0 && run == null && child.IsText && child.Text.AsSpan().Trim().IsEmpty)
                continue;

            run ??= [];
            run.Add(child);
        }

        if (run != null && run.Exists(node => !node.IsText || !node.Text.AsSpan().Trim().IsEmpty))
            groups.Add(new Flow(null, run));

        return groups;
    }

    // ---- grids ----

    /// <summary>
    /// A grid of columns of a stated width: the children fall into them in order, a row at a time. It is
    /// built as a column of rows, so a grid taller than a page still breaks between its rows.
    /// </summary>
    private void Grid(IContainer container, HtmlNode node, HtmlStyle style, Dictionary<string, string> declarations)
    {
        var groups = Cells(node);
        if (groups.Count == 0)
            return;

        var columns = Tracks(declarations.GetValueOrDefault("grid-template-columns"), style.FontSize);
        var (rowGap, columnGap) = Gaps(declarations, style.FontSize);

        container.Column(column =>
        {
            column.Spacing(rowGap);
            for (var first = 0; first < groups.Count; first += columns.Count)
            {
                var start = first;
                column.Item().Row(row =>
                {
                    row.Spacing(columnGap);
                    for (var track = 0; track < columns.Count; track++)
                    {
                        var (kind, value) = columns[track];
                        var cell = kind switch
                        {
                            TrackKind.Constant => row.ConstantItem(value),
                            TrackKind.Content => row.AutoItem(),
                            _ => row.RelativeItem(value),
                        };

                        if (start + track >= groups.Count)
                            continue;

                        var group = groups[start + track];
                        if (group.Block is { } block)
                            BlockIn(cell, block, style);
                        else
                            Paragraph(cell, group.Inline!, style, heading: 0);
                    }
                });
            }
        });
    }

    private enum TrackKind : byte { Relative, Constant, Content }

    /// <summary>
    /// The columns a grid states: a share of what is left over (<c>1fr</c>), a width in its own right, or
    /// as much as the content needs. A percentage is read as a share, which comes to the same thing where
    /// the percentages of a grid add up to the whole of it.
    /// </summary>
    private static List<(TrackKind Kind, float Value)> Tracks(string? template, float fontSize)
    {
        var tracks = new List<(TrackKind, float)>();
        if (string.IsNullOrWhiteSpace(template))
            return [(TrackKind.Relative, 1)];

        foreach (var piece in Pieces(template))
        {
            var text = piece.Trim();
            if (text.Length == 0)
                continue;

            if (text.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase))
            {
                var inner = text[7..].TrimEnd(')');
                var comma = inner.IndexOf(',', StringComparison.Ordinal);
                if (comma < 0 || !int.TryParse(inner[..comma].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var times))
                    continue;

                var repeated = Tracks(inner[(comma + 1)..], fontSize);
                for (var i = 0; i < Math.Clamp(times, 1, 64); i++)
                    tracks.AddRange(repeated);

                continue;
            }

            // Of a track that may stretch between two sizes, the larger is the one that shapes the page.
            if (text.StartsWith("minmax(", StringComparison.OrdinalIgnoreCase))
            {
                var inner = text[7..].TrimEnd(')');
                var comma = inner.LastIndexOf(',');
                text = comma < 0 ? inner : inner[(comma + 1)..].Trim();
            }

            if (text.EndsWith("fr", StringComparison.OrdinalIgnoreCase))
            {
                var share = float.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 1;
                tracks.Add((TrackKind.Relative, Math.Max(0.01f, share)));
                continue;
            }

            if (text.Contains('%', StringComparison.Ordinal) &&
                HtmlValues.Measure(text, fontSize) is { } percentage && percentage.Percent > 0)
            {
                tracks.Add((TrackKind.Relative, percentage.Percent));
                continue;
            }

            if (HtmlValues.Measure(text, fontSize) is { } length && length.IsAbsolute && length.Points > 0)
            {
                tracks.Add((TrackKind.Constant, length.Points));
                continue;
            }

            tracks.Add((TrackKind.Content, 0));
        }

        return tracks.Count > 0 ? tracks : [(TrackKind.Relative, 1)];
    }

    /// <summary>Splits a list of tracks on the spaces that are not inside brackets.</summary>
    private static List<string> Pieces(string text)
    {
        var pieces = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ' ' or '\t' or '\n' or '\r' or ',' when depth == 0:
                    if (i > start)
                        pieces.Add(text[start..i]);

                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        if (start < text.Length)
            pieces.Add(text[start..]);

        return pieces;
    }
}
