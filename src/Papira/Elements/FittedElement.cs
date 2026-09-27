using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

internal enum ImageScaling : byte { FitWidth, FitHeight, FitArea }

/// <summary>
/// Shared layout for content with a fixed aspect ratio — raster images and vector drawings. It scales to the
/// space it is given, and moves to the next page rather than shrinking into a sliver.
/// </summary>
internal abstract class FittedElement : Element
{
    private const float MinimumSize = 1;
    private const float MinimumShrunkHeight = 24;

    public ImageScaling Scaling = ImageScaling.FitWidth;

    /// <summary>What a reader for the blind announces in place of the picture.</summary>
    public string? Alt;

    private bool _drawn;

    /// <summary>Height divided by width.</summary>
    protected abstract float AspectRatio { get; }

    protected abstract void DrawContent(Size target, LayoutContext context);

    private Size Target(Size available)
    {
        var ratio = AspectRatio;
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

        // FitHeight and FitArea shrink to the remaining space. When only a sliver is left, continue on the next
        // page instead of drawing a thumbnail.
        var fullWidthHeight = available.Width * AspectRatio;
        if (target.Height < Math.Min(MinimumShrunkHeight, fullWidthHeight) - Size.Epsilon)
            return SpacePlan.Wrap;

        return SpacePlan.Full(target.Width, target.Height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;

        using var tag = context.Tag("Figure", Alt ?? string.Empty);
        DrawContent(Target(available), context);
    }

    internal override void Reset() => _drawn = false;
}

/// <summary>Draws a vector drawing, scaled into the space the layout gives it.</summary>
internal sealed class SvgElement(SvgImage image) : FittedElement
{
    protected override float AspectRatio => image.AspectRatio;

    protected override void DrawContent(Size target, LayoutContext context) =>
        new Papira.Svg.SvgRenderer(context.Canvas).Draw(image.Document, target.Width, target.Height);
}
