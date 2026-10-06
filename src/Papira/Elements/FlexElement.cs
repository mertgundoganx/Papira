using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>Which way the items of a flexible box follow one another.</summary>
internal enum FlexDirection : byte { Row, RowReverse, Column, ColumnReverse }

/// <summary>How the space left over along the line is shared out.</summary>
internal enum FlexJustify : byte { Start, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }

/// <summary>How the items sit across the line: filling it, at one end, in the middle, or lined up on their text.</summary>
internal enum FlexAlign : byte { Stretch, Start, Center, End, Baseline }

/// <summary>
/// One item of a flexible box: how eagerly it takes the space left over, how readily it gives space up,
/// the size it starts from, and how it sits across the line when that differs from its neighbours.
/// </summary>
internal sealed class FlexItem : ContainerElement
{
    public float Grow;
    public float Shrink = 1;

    /// <summary>The size the item starts from along the line; null means the size of its content.</summary>
    public CssLength? Basis;

    /// <summary>
    /// The padding and the border the item draws along the line. A size it starts from counts them in, so
    /// an item that starts from nothing is still as long as what it draws — which is what makes a row of
    /// "flex: 1" items share out what is left over after their padding, as a browser does.
    /// </summary>
    public float Edges;

    /// <summary>The margins the item keeps along the line, which stand outside the size it states.</summary>
    public float Margins;

    public FlexAlign? AlignSelf;
}

/// <summary>
/// A flexible box: items follow one another along a line, take or give up space until the line is full,
/// and are lined up across it. Items are laid out together, so the box spans pages as a unit — a column
/// of blocks that only stacks is built as an ordinary column instead, which keeps it breaking over pages.
/// </summary>
internal sealed class FlexElement : Element
{
    public readonly List<FlexItem> Items = [];

    public FlexDirection Direction;

    public bool Wrap;

    public FlexJustify Justify;

    public FlexAlign Align = FlexAlign.Stretch;

    public float RowGap;

    public float ColumnGap;

    /// <summary>
    /// The text a line of this box is at least as tall as. A line of text is as tall as the text it could
    /// hold even where it holds none, which is what leaves the small gap under a picture in a line of its
    /// own; a box that stands for such a line says here what text that would be.
    /// </summary>
    public TextStyle? Strut;

    /// <summary>
    /// True when the box was told how long it is along its own direction. Only then is there space left
    /// over to share out: a box as long as its content has none, which is why a column of items that was
    /// given no height ignores <c>justify-content</c>, exactly as a browser does.
    /// </summary>
    public bool MainSizeFixed;

    private Layout? _layout;
    private float _layoutFor = float.NaN;

    private bool Horizontal => Direction is FlexDirection.Row or FlexDirection.RowReverse;

    private bool Reversed => Direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse;

    /// <summary>The gap between two items along the line, and between two lines across it.</summary>
    private float MainGap => Horizontal ? ColumnGap : RowGap;

    private float CrossGap => Horizontal ? RowGap : ColumnGap;

    /// <summary>Where one item ended up: its place along the line and across it, and the size it was given.</summary>
    private readonly record struct Placement(FlexItem Item, float Main, float Cross, float MainSize, float CrossSize);

    private sealed class Layout
    {
        public readonly List<Placement> Placements = [];
        public float Main;
        public float Cross;
    }

    private Size Frame(float main, float cross) => Horizontal ? new Size(main, cross) : new Size(cross, main);

    private static float Main(Size size, bool horizontal) => horizontal ? size.Width : size.Height;

    private Layout Arrange(Size available, LayoutContext context)
    {
        // The box is laid out against the room it has along its own direction. Across it, what is laid out
        // once is kept: the parent measures before it draws, and draws in the height the measurement gave,
        // so working it out again in that smaller height would place the items somewhere else.
        var key = Horizontal ? available.Width : MainSizeFixed ? available.Height : 0;
        if (_layout != null && Math.Abs(_layoutFor - key) < Size.Epsilon)
            return _layout;

        var horizontal = Horizontal;
        var mainRoom = Main(available, horizontal);
        var crossRoom = horizontal ? available.Height : available.Width;

        // What each item asks for along the line: the size it states, or the size of its content.
        var asked = new float[Items.Count];
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.Basis is { } basis)
            {
                asked[i] = item.Margins + Math.Max(item.Edges, basis.Resolve(mainRoom, context.Viewport));
                continue;
            }

            var plan = item.Measure(Frame(mainRoom, crossRoom), context);
            asked[i] = plan.HasContent ? Main(new Size(plan.Width, plan.Height), horizontal) : 0;
        }

        var layout = new Layout();
        var lines = Lines(asked, mainRoom);
        float cross = 0;
        var gap = MainGap;

        foreach (var (start, end) in lines)
        {
            var count = end - start;
            var gaps = gap * Math.Max(0, count - 1);
            var content = gaps;
            for (var i = start; i < end; i++)
                content += asked[i];

            // A box as long as its content has nothing to share out, so there the line is as long as it is.
            var lineRoom = horizontal || MainSizeFixed ? mainRoom : content;
            var sizes = Distribute(asked, start, end, lineRoom - gaps, crossRoom, context);

            // Across the line, every item is as thick as what it holds; those lined up on their text need
            // to know where that text sits, because the line has to make room above the deepest baseline.
            var thickness = new float[count];
            var baselines = new float[count];
            float aboveBaseline = 0, belowBaseline = 0, plain = 0;

            for (var i = start; i < end; i++)
            {
                var item = Items[i];
                var size = Frame(sizes[i - start], crossRoom);
                var plan = item.Measure(size, context);
                var measured = plan.HasContent ? (horizontal ? plan.Height : plan.Width) : 0;
                thickness[i - start] = measured;

                if ((item.AlignSelf ?? Align) != FlexAlign.Baseline || !horizontal)
                {
                    plain = Math.Max(plain, measured);
                    baselines[i - start] = -1;
                    continue;
                }

                var baseline = item.FirstBaseline(size, context) ?? measured;
                baselines[i - start] = baseline;
                aboveBaseline = Math.Max(aboveBaseline, baseline);
                belowBaseline = Math.Max(belowBaseline, measured - baseline);
            }

            // The line is at least as tall as the text it could hold, and its baseline at least as deep.
            if (Strut is { } strut && horizontal)
            {
                var resolved = new ResolvedTextStyle(strut.InheritFrom(context.DefaultStyle));
                var halfLeading = (resolved.LineHeight - resolved.Ascent - resolved.Descent) / 2;
                aboveBaseline = Math.Max(aboveBaseline, halfLeading + resolved.Ascent);
                belowBaseline = Math.Max(belowBaseline, halfLeading + resolved.Descent);
            }

            var lineCross = Math.Max(plain, aboveBaseline + belowBaseline);

            // A box whose items stack downwards is as wide as it was offered, being a block like any
            // other, and that is the width its items are placed across.
            if (!horizontal && lines.Count == 1)
                lineCross = Math.Max(lineCross, crossRoom);
            var positions = Positions(sizes, lineRoom, gap);

            for (var i = start; i < end; i++)
            {
                var item = Items[i];
                var mainSize = sizes[i - start];
                var own = thickness[i - start];
                var alignment = item.AlignSelf ?? Align;

                var (crossOffset, crossSize) = alignment switch
                {
                    FlexAlign.Baseline when horizontal => (aboveBaseline - baselines[i - start], own),
                    FlexAlign.Center => ((lineCross - own) / 2, own),
                    FlexAlign.End => (lineCross - own, own),
                    FlexAlign.Start => (0f, own),
                    _ => (0f, lineCross),
                };

                var main = positions[i - start];
                if (Reversed)
                    main = lineRoom - main - mainSize;

                layout.Placements.Add(new Placement(item, main, cross + Math.Max(0, crossOffset), mainSize, crossSize));
            }

            layout.Main = Math.Max(layout.Main, lineRoom);
            cross += lineCross + CrossGap;
        }

        layout.Cross = Math.Max(0, cross - (lines.Count > 0 ? CrossGap : 0));
        _layout = layout;
        _layoutFor = key;
        return layout;
    }

    /// <summary>
    /// How the items are shared between lines. Without wrapping they are all on one line, however long it
    /// is; with it, an item that no longer fits starts a line of its own.
    /// </summary>
    private List<(int Start, int End)> Lines(float[] asked, float room)
    {
        var lines = new List<(int, int)>();
        if (Items.Count == 0)
            return lines;

        if (!Wrap)
        {
            lines.Add((0, Items.Count));
            return lines;
        }

        var gap = MainGap;
        var start = 0;
        float used = 0;

        for (var i = 0; i < Items.Count; i++)
        {
            var spacing = i > start ? gap : 0;
            if (i > start && used + spacing + asked[i] > room + Size.Epsilon)
            {
                lines.Add((start, i));
                start = i;
                used = asked[i];
                continue;
            }

            used += spacing + asked[i];
        }

        lines.Add((start, Items.Count));
        return lines;
    }

    /// <summary>
    /// The size of every item of one line once the space left over has been shared out: items that want to
    /// grow take it in proportion to how eagerly they want it, and items give space back in proportion to
    /// how readily they give it up and to how large they are, which is what CSS asks for.
    /// </summary>
    private float[] Distribute(float[] asked, int start, int end, float room, float crossRoom, LayoutContext context)
    {
        var sizes = new float[end - start];
        float total = 0, grow = 0, shrink = 0;

        for (var i = start; i < end; i++)
        {
            sizes[i - start] = asked[i];
            total += asked[i];
            grow += Items[i].Grow;
            shrink += Items[i].Shrink * asked[i];
        }

        var free = room - total;
        if (Math.Abs(free) < Size.Epsilon)
            return sizes;

        if (free > 0 && grow > 0)
        {
            for (var i = start; i < end; i++)
                sizes[i - start] += free * Items[i].Grow / grow;

            return sizes;
        }

        if (free < 0 && shrink > 0)
        {
            for (var i = start; i < end; i++)
            {
                var share = Items[i].Shrink * asked[i] / shrink;
                sizes[i - start] = Math.Max(0, asked[i] + (free * share));
            }

            // An item cannot be shrunk below what its content needs, so what it keeps is given back.
            for (var i = start; i < end; i++)
            {
                var item = Items[i];
                if (item.Basis != null)
                    continue;

                if (item.Measure(Frame(sizes[i - start], crossRoom), context).IsWrap)
                    sizes[i - start] = asked[i];
            }
        }

        return sizes;
    }

    /// <summary>Where each item of a line starts, once the space left over has been placed around them.</summary>
    private float[] Positions(float[] sizes, float room, float gap)
    {
        float used = gap * Math.Max(0, sizes.Length - 1);
        foreach (var size in sizes)
            used += size;

        var free = Math.Max(0, room - used);
        var count = sizes.Length;
        var (leading, between) = Justify switch
        {
            FlexJustify.Center => (free / 2, 0f),
            FlexJustify.End => (free, 0f),
            FlexJustify.SpaceBetween when count > 1 => (0f, free / (count - 1)),
            FlexJustify.SpaceAround when count > 0 => (free / count / 2, free / count),
            FlexJustify.SpaceEvenly when count > 0 => (free / (count + 1), free / (count + 1)),
            _ => (0f, 0f),
        };

        var positions = new float[count];
        var offset = leading;
        for (var i = 0; i < count; i++)
        {
            positions[i] = offset;
            offset += sizes[i] + gap + between;
        }

        return positions;
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (Items.Count == 0)
            return SpacePlan.Empty;

        var layout = Arrange(available, context);
        if (layout.Placements.Count == 0)
            return SpacePlan.Empty;

        var size = Frame(layout.Main, layout.Cross);
        if (size.Height > available.Height + Size.Epsilon)
            return SpacePlan.Wrap;

        // The box is as long as the line it fills, but no wider than it was offered: content that hangs
        // over the edge is what the markup asked for and is drawn, not counted.
        return SpacePlan.Full(Math.Min(size.Width, available.Width), size.Height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (Items.Count == 0)
            return;

        var layout = Arrange(available, context);
        var canvas = context.Canvas;

        foreach (var placement in layout.Placements)
        {
            var (x, y) = Horizontal ? (placement.Main, placement.Cross) : (placement.Cross, placement.Main);
            var size = Frame(placement.MainSize, placement.CrossSize);

            var finished = !placement.Item.Measure(size, context).HasContent;
            var previous = context.DrawEmptyDecorations;
            canvas.Translate(x, y);
            context.DrawEmptyDecorations = previous || finished;
            placement.Item.Draw(size, context);
            context.DrawEmptyDecorations = previous;
            canvas.Translate(-x, -y);
        }
    }

    internal override float? FirstBaseline(Size available, LayoutContext context)
    {
        var layout = Arrange(available, context);
        foreach (var placement in layout.Placements)
        {
            var size = Frame(placement.MainSize, placement.CrossSize);
            if (placement.Item.FirstBaseline(size, context) is { } baseline)
                return placement.Cross + baseline;
        }

        return null;
    }

    internal override void Reset()
    {
        _layout = null;
        foreach (var item in Items)
            item.Reset();
    }
}
