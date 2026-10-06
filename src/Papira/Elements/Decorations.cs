using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

internal sealed class PaddingElement : ContainerElement
{
    public float Left, Top, Right, Bottom;

    /// <summary>
    /// True where the padding belongs to the box as a whole rather than to each page of it. A box that
    /// carries on over a page is then padded where it begins and where it ends, and not again at the
    /// fold, which is how CSS treats the padding of a block that breaks.
    /// </summary>
    public bool AtTheEnds;

    private bool _started;

    /// <summary>How much is padded away at the top and at the bottom of the space given on this page.</summary>
    private (float Top, float Bottom) Edges(Size available, LayoutContext context)
    {
        if (!AtTheEnds)
            return (Top, Bottom);

        var top = _started ? 0 : Top;

        // The padding below the content is only drawn once the content itself has ended.
        var plan = Child.Measure(new Size(Math.Max(0, available.Width - Left - Right), Math.Max(0, available.Height - top)), context);
        return (top, plan.Kind == SpacePlanKind.Partial ? 0 : Bottom);
    }

    private Size Inner(Size available, (float Top, float Bottom) edges) =>
        new(Math.Max(0, available.Width - Left - Right), Math.Max(0, available.Height - edges.Top - edges.Bottom));

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var edges = Edges(available, context);
        if (Left + Right > available.Width + Size.Epsilon || edges.Top + edges.Bottom > available.Height + Size.Epsilon)
            return SpacePlan.Wrap;

        var plan = Child.Measure(Inner(available, edges), context);
        return plan.HasContent
            ? plan with { Width = plan.Width + Left + Right, Height = plan.Height + edges.Top + edges.Bottom }
            : plan;
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var edges = Edges(available, context);
        DrawChildAt(Left, edges.Top, Inner(available, edges), context);
        _started = true;
    }

    internal override float? FirstBaseline(Size available, LayoutContext context)
    {
        var edges = Edges(available, context);
        return Child.FirstBaseline(Inner(available, edges), context) + edges.Top;
    }

    internal override void Reset()
    {
        _started = false;
        base.Reset();
    }
}

internal sealed class BackgroundElement(Color color) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        if (context.DrawEmptyDecorations || Child.Measure(available, context).HasContent)
            context.Canvas.FillRoundedRectangle(0, 0, available.Width, available.Height, context.CornerRadius, color);
        Child.Draw(available, context);
    }
}

/// <summary>
/// Draws the shadows a box casts, behind everything the box itself draws. A shadow lies outside the box,
/// so it is drawn before the corners are rounded — what rounds them also cuts away what falls outside.
/// </summary>
internal sealed class ShadowElement(BoxShadow[] shadows, float radius) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        if (context.DrawEmptyDecorations || Child.Measure(available, context).HasContent)
        {
            // The first shadow of the list is the one nearest the reader, so it is drawn last.
            for (var i = shadows.Length - 1; i >= 0; i--)
                ShadowPainter.Draw(context.Canvas, available, radius > 0 ? radius : context.CornerRadius, shadows[i]);
        }

        Child.Draw(available, context);
    }
}

internal sealed class BorderElement : ContainerElement
{
    public float Left, Top, Right, Bottom;
    public Color Color = Colors.Black;

    internal override void Draw(Size available, LayoutContext context)
    {
        var hasContent = context.DrawEmptyDecorations || Child.Measure(available, context).HasContent;
        Child.Draw(available, context);
        if (!hasContent)
            return;

        var canvas = context.Canvas;
        var (w, h) = (available.Width, available.Height);

        // Rounded corners need a stroked path, which requires the same width on all sides.
        if (context.CornerRadius > 0 && Left > 0 && Left == Top && Top == Right && Right == Bottom)
        {
            canvas.StrokeRoundedRectangle(Left / 2, Left / 2, w - Left, h - Left, context.CornerRadius, Color, Left);
            return;
        }

        // Borders are drawn as filled rectangles centred on the edges: crisp and joined at the corners.
        canvas.FillRectangle(-Left / 2, -Top / 2, w + Left / 2 + Right / 2, Top, Color);
        canvas.FillRectangle(-Left / 2, h - Bottom / 2, w + Left / 2 + Right / 2, Bottom, Color);
        canvas.FillRectangle(-Left / 2, -Top / 2, Left, h + Top / 2 + Bottom / 2, Color);
        canvas.FillRectangle(w - Right / 2, -Top / 2, Right, h + Top / 2 + Bottom / 2, Color);
    }
}

/// <summary>
/// What an element says about its own size: a size it asks for, and the smallest and largest it may be.
/// Also used as a fixed-size spacer when it has no content. Any of the three can be a share of the space
/// the element is offered — what a CSS percentage states — and is then worked out as it is laid out.
/// </summary>
internal sealed class ConstrainedElement : ContainerElement
{
    public CssLength? Width, Height, MinWidth, MaxWidth, MinHeight, MaxHeight;
    private bool _drawn;

    /// <summary>
    /// The smallest and largest size the element may take, in points. A size it asks for is held inside
    /// the largest and then inside the smallest, which is the order CSS resolves the three in.
    /// </summary>
    private Limits Resolve(Size available, LayoutContext context)
    {
        var viewport = context.Viewport;
        return new Limits(Axis(Width, MinWidth, MaxWidth, available.Width), Axis(Height, MinHeight, MaxHeight, available.Height));

        (float Min, float Max) Axis(CssLength? preferred, CssLength? smallest, CssLength? largest, float basis)
        {
            var min = smallest is { } value ? Math.Max(0, value.Resolve(basis, viewport)) : 0;
            var max = largest is { } limit ? Math.Max(min, limit.Resolve(basis, viewport)) : float.PositiveInfinity;
            if (preferred is not { } asked)
                return (min, max);

            var size = Math.Clamp(Math.Max(0, asked.Resolve(basis, viewport)), min, max);
            return (size, size);
        }
    }

    private readonly record struct Limits((float Min, float Max) Horizontal, (float Min, float Max) Vertical)
    {
        public Size Inner(Size available) =>
            new(Math.Min(available.Width, Horizontal.Max), Math.Min(available.Height, Vertical.Max));

        public bool Sized => Horizontal.Min > 0 || Vertical.Min > 0;
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var limits = Resolve(available, context);
        if (limits.Horizontal.Min > available.Width + Size.Epsilon || limits.Vertical.Min > available.Height + Size.Epsilon)
            return SpacePlan.Wrap;

        var plan = Child.Measure(limits.Inner(available), context);
        if (plan.IsWrap)
            return plan;

        if (plan.IsEmpty)
            return !_drawn && limits.Sized ? SpacePlan.Full(limits.Horizontal.Min, limits.Vertical.Min) : plan;

        return plan with
        {
            Width = Math.Max(plan.Width, limits.Horizontal.Min),
            Height = Math.Max(plan.Height, limits.Vertical.Min),
        };
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var limits = Resolve(available, context);
        var inner = limits.Inner(available);

        // A fixed-size box without content (e.g. Height(20).Background(...)) still shows its decorations.
        var isSizedBox = !_drawn && limits.Sized && Child.Measure(inner, context).IsEmpty;
        _drawn = true;

        var previous = context.DrawEmptyDecorations;
        context.DrawEmptyDecorations |= isSizedBox;
        Child.Draw(inner, context);
        context.DrawEmptyDecorations = previous;
    }

    internal override float? FirstBaseline(Size available, LayoutContext context) =>
        Child.FirstBaseline(Resolve(available, context).Inner(available), context);

    internal override void Reset()
    {
        _drawn = false;
        base.Reset();
    }
}

internal enum HorizontalAlignment : byte { Left, Center, Right }

internal enum VerticalAlignment : byte { Top, Middle, Bottom }

internal sealed class AlignmentElement : ContainerElement
{
    public HorizontalAlignment? Horizontal;
    public VerticalAlignment? Vertical;

    internal override void Draw(Size available, LayoutContext context)
    {
        var plan = Child.Measure(available, context);
        if (!plan.HasContent)
        {
            // Nothing to position, but decorations below may still need to cover the area.
            if (context.DrawEmptyDecorations)
                Child.Draw(available, context);
            return;
        }

        var x = Horizontal switch
        {
            HorizontalAlignment.Center => (available.Width - plan.Width) / 2,
            HorizontalAlignment.Right => available.Width - plan.Width,
            _ => 0,
        };

        var y = Vertical switch
        {
            VerticalAlignment.Middle => (available.Height - plan.Height) / 2,
            VerticalAlignment.Bottom => available.Height - plan.Height,
            _ => 0,
        };

        var width = Horizontal.HasValue ? plan.Width : available.Width;
        var height = Vertical.HasValue ? plan.Height : available.Height;
        DrawChildAt(Math.Max(0, x), Math.Max(0, y), new Size(width, height), context);
    }
}

/// <summary>Makes the element occupy all available width and/or height.</summary>
internal sealed class ExtendElement : ContainerElement
{
    public bool Horizontal, Vertical;

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var plan = Child.Measure(available, context);
        if (plan.IsWrap)
            return plan;

        var kind = plan.IsEmpty ? SpacePlanKind.Full : plan.Kind;
        return new SpacePlan(kind, Horizontal ? available.Width : plan.Width, Vertical ? available.Height : plan.Height);
    }
}

/// <summary>Moves the whole child to the next page instead of splitting it.</summary>
internal sealed class ShowEntireElement : ContainerElement
{
    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var plan = Child.Measure(available, context);
        return plan.Kind == SpacePlanKind.Partial ? SpacePlan.Wrap : plan;
    }
}

/// <summary>
/// Moves the child to the next page when less than <c>minHeight</c> points are left on the current one,
/// or when only a fragment shorter than <c>minHeight</c> would fit. Keeps headings together with what follows.
/// Ignored when <see cref="LayoutContext.RelaxKeepTogether"/> is set (nothing else fits on an empty page).
/// </summary>
internal sealed class EnsureSpaceElement(float minHeight) : ContainerElement
{
    private bool _started;

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var plan = Child.Measure(available, context);
        if (_started || !plan.HasContent || context.RelaxKeepTogether)
            return plan;

        // Never require more than a page can offer.
        var required = Math.Min(minHeight, context.BodyHeight);
        if (available.Height < required - Size.Epsilon || (plan.Kind == SpacePlanKind.Partial && plan.Height < required))
            return SpacePlan.Wrap;

        return plan;
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        _started = true;
        Child.Draw(available, context);
    }

    internal override void Reset()
    {
        _started = false;
        base.Reset();
    }
}

internal sealed class DefaultTextStyleElement(TextStyle style) : ContainerElement
{
    private TextStyle? _parent;
    private TextStyle? _resolved;

    private TextStyle Apply(LayoutContext context)
    {
        var parent = context.DefaultStyle;
        if (!ReferenceEquals(parent, _parent))
        {
            _parent = parent;
            _resolved = style.InheritFrom(parent);
        }

        context.DefaultStyle = _resolved!;
        return parent;
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var parent = Apply(context);
        try
        {
            return Child.Measure(available, context);
        }
        finally
        {
            context.DefaultStyle = parent;
        }
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var parent = Apply(context);
        try
        {
            Child.Draw(available, context);
        }
        finally
        {
            context.DefaultStyle = parent;
        }
    }
}

/// <summary>
/// Content that is there to be looked at and not read: a rule, a watermark, a picture that says nothing
/// the words around it do not already say. It is marked as an artifact of the page, which a reader for
/// the blind passes over.
/// </summary>
internal sealed class DecorationElement : ContainerElement
{
    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var previous = context.Artifact;
        context.Artifact = true;
        try
        {
            return Child.Measure(available, context);
        }
        finally
        {
            context.Artifact = previous;
        }
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var previous = context.Artifact;
        context.Artifact = true;
        try
        {
            Child.Draw(available, context);
        }
        finally
        {
            context.Artifact = previous;
        }
    }
}

internal sealed class PageBreakElement : Element
{
    private bool _done;

    internal override SpacePlan Measure(Size available, LayoutContext context) =>
        _done ? SpacePlan.Empty : SpacePlan.Partial(0, 0);

    internal override void Draw(Size available, LayoutContext context) => _done = true;

    internal override void Reset() => _done = false;
}

internal sealed class LineElement(bool vertical, float thickness) : Element
{
    public Color Color = Colors.Black;
    private bool _drawn;

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (_drawn)
            return SpacePlan.Empty;

        if (vertical)
            return thickness > available.Width + Size.Epsilon ? SpacePlan.Wrap : SpacePlan.Full(thickness, 0);

        return thickness > available.Height + Size.Epsilon ? SpacePlan.Wrap : SpacePlan.Full(0, thickness);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;
        if (vertical)
            context.Canvas.FillRectangle(0, 0, thickness, available.Height, Color);
        else
            context.Canvas.FillRectangle(0, 0, available.Width, thickness, Color);
    }

    internal override void Reset() => _drawn = false;
}

internal sealed class ImageElement(Image image) : FittedElement
{
    protected override float AspectRatio => image.AspectRatio;

    protected override void DrawContent(Size target, LayoutContext context) =>
        context.Canvas.DrawImage(image, 0, 0, target.Width, target.Height);
}

/// <summary>
/// Gives its content a place in the structure of a tagged document — a list, an item of one — without
/// drawing anything itself.
/// </summary>
internal sealed class TaggedElement(string role) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        using var tag = context.Tag(role, content: false);
        Child.Draw(available, context);
    }
}
