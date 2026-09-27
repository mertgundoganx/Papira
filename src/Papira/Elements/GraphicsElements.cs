using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>Rotates its content around the centre of the space it was given; the layout size does not change.</summary>
internal sealed class RotateElement(float degrees) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        var canvas = context.Canvas;
        canvas.PushTransform();
        canvas.Translate(available.Width / 2, available.Height / 2);
        canvas.Rotate(degrees);
        canvas.Translate(-available.Width / 2, -available.Height / 2);
        Child.Draw(available, context);
        canvas.PopTransform();
    }
}

/// <summary>Rotates its content by a quarter turn, swapping width and height in the layout.</summary>
internal sealed class QuarterTurnElement(bool clockwise) : ContainerElement
{
    private static Size Swap(Size size) => new(size.Height, size.Width);

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var plan = Child.Measure(Swap(available), context);
        return plan.HasContent ? plan with { Width = plan.Height, Height = plan.Width } : plan;
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var inner = Swap(available);
        var canvas = context.Canvas;
        canvas.PushTransform();

        if (clockwise)
        {
            canvas.Translate(inner.Height, 0);
            canvas.Rotate(90);
        }
        else
        {
            canvas.Translate(0, inner.Width);
            canvas.Rotate(-90);
        }

        Child.Draw(inner, context);
        canvas.PopTransform();
    }
}

internal sealed class ScaleElement(float scaleX, float scaleY) : ContainerElement
{
    private Size Inner(Size available) => new(available.Width / scaleX, available.Height / scaleY);

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (scaleX <= 0 || scaleY <= 0)
            return SpacePlan.Empty;

        var plan = Child.Measure(Inner(available), context);
        return plan.HasContent ? plan with { Width = plan.Width * scaleX, Height = plan.Height * scaleY } : plan;
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (scaleX <= 0 || scaleY <= 0)
            return;

        var canvas = context.Canvas;
        canvas.PushTransform();
        canvas.Scale(scaleX, scaleY);
        Child.Draw(Inner(available), context);
        canvas.PopTransform();
    }
}

/// <summary>Draws its content with the given opacity (0 = invisible, 1 = opaque).</summary>
internal sealed class OpacityElement(float opacity) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        if (opacity >= 1)
        {
            Child.Draw(available, context);
            return;
        }

        if (opacity <= 0)
            return;

        context.Canvas.BeginOpacityGroup(opacity);
        Child.Draw(available, context);
        context.Canvas.EndGroup();
    }
}

/// <summary>
/// Rounds the corners of the backgrounds, borders and gradients below it, and clips its content
/// to the rounded rectangle so images and fills follow the corners too.
/// </summary>
internal sealed class CornerRadiusElement(float radius) : ContainerElement
{
    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var previous = context.CornerRadius;
        context.CornerRadius = radius;
        try
        {
            return Child.Measure(available, context);
        }
        finally
        {
            context.CornerRadius = previous;
        }
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var previous = context.CornerRadius;
        context.CornerRadius = radius;
        context.Canvas.BeginClipGroup(0, 0, available.Width, available.Height, radius);
        try
        {
            Child.Draw(available, context);
        }
        finally
        {
            context.Canvas.EndGroup();
            context.CornerRadius = previous;
        }
    }
}

internal sealed class GradientElement(bool radial, float angleDegrees, Color[] colors) : ContainerElement
{
    internal override void Draw(Size available, LayoutContext context)
    {
        if (context.DrawEmptyDecorations || Child.Measure(available, context).HasContent)
        {
            var shading = Build(available, context.CornerRadius);
            context.Canvas.FillRectangleWithShading(0, 0, available.Width, available.Height, shading);
        }

        Child.Draw(available, context);
    }

    private Shading Build(Size available, float cornerRadius)
    {
        var stops = new ColorStop[colors.Length];
        for (var i = 0; i < colors.Length; i++)
            stops[i] = new ColorStop(colors.Length == 1 ? 0 : (float)i / (colors.Length - 1), colors[i]);

        if (radial)
        {
            var radius = MathF.Sqrt(available.Width * available.Width + available.Height * available.Height) / 2;
            var (cx, cy) = (available.Width / 2, available.Height / 2);
            return new Shading(true, cx, cy, 0, cx, cy, radius, stops);
        }

        // The gradient axis crosses the box through its centre at the given angle (0° = left to right).
        var radians = angleDegrees * MathF.PI / 180;
        float dx = MathF.Cos(radians), dy = MathF.Sin(radians);
        var half = (MathF.Abs(dx) * available.Width + MathF.Abs(dy) * available.Height) / 2;
        var centerX = available.Width / 2;
        var centerY = available.Height / 2;

        _ = cornerRadius;
        return new Shading(false, centerX - dx * half, centerY - dy * half, 0, centerX + dx * half, centerY + dy * half, 0, stops);
    }
}

/// <summary>Hands the drawing surface to user code; it occupies all the space it is given.</summary>
internal sealed class DrawingElement(Action<IDrawingCanvas, float, float> draw) : Element
{
    private bool _drawn;

    internal override SpacePlan Measure(Size available, LayoutContext context) =>
        _drawn ? SpacePlan.Empty : SpacePlan.Full(available.Width, available.Height);

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;
        draw(new DrawingCanvas(context.Canvas), available.Width, available.Height);
    }

    internal override void Reset() => _drawn = false;
}
