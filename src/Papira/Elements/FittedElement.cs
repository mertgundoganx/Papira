using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>
/// How content with a fixed shape is fitted into the space it is given: scaled to the width, to the
/// height, inside the whole area, stretched to fill it exactly, or scaled to cover it and cropped.
/// These are what the <c>object-fit</c> of a style sheet asks for.
/// </summary>
internal enum ImageScaling : byte { FitWidth, FitHeight, FitArea, Stretch, Cover }

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
            ImageScaling.Stretch or ImageScaling.Cover => available,
            _ => available.Width * ratio <= available.Height
                ? new Size(available.Width, available.Width * ratio)
                : new Size(available.Height / ratio, available.Height),
        };
    }

    /// <summary>The size the content is drawn at to cover the whole box, which crops what hangs over.</summary>
    private Size Covering(Size box)
    {
        var ratio = AspectRatio;
        return box.Width * ratio >= box.Height
            ? new Size(box.Width, box.Width * ratio)
            : new Size(box.Height / ratio, box.Height);
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
        // page instead of drawing a thumbnail. A box of a stated size is already as tall as it was asked to be.
        var fullWidthHeight = available.Width * AspectRatio;
        if (Scaling is not (ImageScaling.Stretch or ImageScaling.Cover) &&
            target.Height < Math.Min(MinimumShrunkHeight, fullWidthHeight) - Size.Epsilon)
        {
            return SpacePlan.Wrap;
        }

        return SpacePlan.Full(target.Width, target.Height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;

        using var tag = context.Tag("Figure", Alt ?? string.Empty);
        if (Scaling != ImageScaling.Cover)
        {
            DrawContent(Target(available), context);
            return;
        }

        // Covering the box means drawing larger than it and cutting off what hangs over the edges,
        // centred as a browser centres it.
        var content = Covering(available);
        var canvas = context.Canvas;
        var (x, y) = ((available.Width - content.Width) / 2, (available.Height - content.Height) / 2);
        canvas.BeginClipGroup(0, 0, available.Width, available.Height, context.CornerRadius);
        canvas.Translate(x, y);
        DrawContent(content, context);
        canvas.Translate(-x, -y);
        canvas.EndGroup();
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
