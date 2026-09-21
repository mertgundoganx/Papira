using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

internal sealed class PaddingElement : ContainerElement
{
    public float Left, Top, Right, Bottom;

    private Size Inner(Size available) =>
        new(Math.Max(0, available.Width - Left - Right), Math.Max(0, available.Height - Top - Bottom));

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (Left + Right > available.Width + Size.Epsilon || Top + Bottom > available.Height + Size.Epsilon)
            return SpacePlan.Wrap;

        var plan = Child.Measure(Inner(available), context);
        return plan.HasContent
            ? plan with { Width = plan.Width + Left + Right, Height = plan.Height + Top + Bottom }
            : plan;
    }

    internal override void Draw(Size available, LayoutContext context) =>
        DrawChildAt(Left, Top, Inner(available), context);
}

internal sealed class BackgroundElement(Color color) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        if (context.DrawEmptyDecorations || Child.Measure(available, context).HasContent)
            context.Canvas.FillRectangle(0, 0, available.Width, available.Height, color);
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

        // Borders are drawn as filled rectangles centred on the edges: crisp and joined at the corners.
        var canvas = context.Canvas;
        var (w, h) = (available.Width, available.Height);
        canvas.FillRectangle(-Left / 2, -Top / 2, w + Left / 2 + Right / 2, Top, Color);
        canvas.FillRectangle(-Left / 2, h - Bottom / 2, w + Left / 2 + Right / 2, Bottom, Color);
        canvas.FillRectangle(-Left / 2, -Top / 2, Left, h + Top / 2 + Bottom / 2, Color);
        canvas.FillRectangle(w - Right / 2, -Top / 2, Right, h + Top / 2 + Bottom / 2, Color);
    }
}

/// <summary>Min/max width and height constraints. Also used as fixed-size spacer when it has no content.</summary>
internal sealed class ConstrainedElement : ContainerElement
{
    public float MinWidth, MinHeight;
    public float MaxWidth = float.PositiveInfinity, MaxHeight = float.PositiveInfinity;
    private bool _drawn;

    private Size Inner(Size available) => new(Math.Min(available.Width, MaxWidth), Math.Min(available.Height, MaxHeight));

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (MinWidth > available.Width + Size.Epsilon || MinHeight > available.Height + Size.Epsilon)
            return SpacePlan.Wrap;

        var plan = Child.Measure(Inner(available), context);
        if (plan.IsWrap)
            return plan;

        if (plan.IsEmpty)
            return !_drawn && (MinWidth > 0 || MinHeight > 0) ? SpacePlan.Full(MinWidth, MinHeight) : plan;

        return plan with { Width = Math.Max(plan.Width, MinWidth), Height = Math.Max(plan.Height, MinHeight) };
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var inner = Inner(available);

        // A fixed-size box without content (e.g. Height(20).Background(...)) still shows its decorations.
        var isSizedBox = !_drawn && (MinWidth > 0 || MinHeight > 0) && Child.Measure(inner, context).IsEmpty;
        _drawn = true;

        var previous = context.DrawEmptyDecorations;
        context.DrawEmptyDecorations |= isSizedBox;
        Child.Draw(inner, context);
        context.DrawEmptyDecorations = previous;
    }

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

internal enum ImageScaling : byte { FitWidth, FitHeight, FitArea }

internal sealed class ImageElement(Image image) : Element
{
    private const float MinimumSize = 1;
    private const float MinimumShrunkHeight = 24;

    public ImageScaling Scaling = ImageScaling.FitWidth;
    private bool _drawn;

    private Size Target(Size available)
    {
        var ratio = image.AspectRatio;
        return Scaling switch
        {
            ImageScaling.FitWidth => new Size(available.Width, available.Width * ratio),
            ImageScaling.FitHeight => new Size(available.Height / ratio, available.Height),
            _ => available.Width * ratio <= available.Height
                ? new Size(available.Width, available.Width * ratio)
                : new Size(available.Height / ratio, available.Height),
        };
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (_drawn)
            return SpacePlan.Empty;

        var target = Target(available);
        if (target.Width > available.Width + Size.Epsilon || target.Height > available.Height + Size.Epsilon)
            return SpacePlan.Wrap;

        if (target.Width < MinimumSize || target.Height < MinimumSize)
            return SpacePlan.Wrap;

        // FitHeight/FitArea shrink to the remaining space. When only a sliver is left, continue on the next page
        // instead of drawing a tiny thumbnail.
        var fullWidthHeight = available.Width * image.AspectRatio;
        if (target.Height < Math.Min(MinimumShrunkHeight, fullWidthHeight) - Size.Epsilon)
            return SpacePlan.Wrap;

        return SpacePlan.Full(target.Width, target.Height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;
        var target = Target(available);
        context.Canvas.DrawImage(image, 0, 0, target.Width, target.Height);
    }

    internal override void Reset() => _drawn = false;
}
