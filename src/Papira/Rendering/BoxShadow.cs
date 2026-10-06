using Papira.Images;
using Papira.Infrastructure;

namespace Papira.Rendering;

/// <summary>
/// A shadow cast by a box: how far it is moved, how far it is blurred, how much larger than the box it
/// is cast, and in what colour. This is what a style sheet's <c>box-shadow</c> states.
/// </summary>
internal readonly record struct BoxShadow(float OffsetX, float OffsetY, float Blur, float Spread, Color Color, float Alpha);

/// <summary>
/// Draws the shadows of a box. A shadow with nothing to blur is a shape like any other; a blurred one is
/// a grey picture of the blurred shape, laid over the page as a mask, with the colour poured through it.
/// This is how a browser writes one into a PDF, and it keeps the page a page: the colour stays a colour
/// and only the softness of the edge is a picture.
/// </summary>
internal static class ShadowPainter
{
    /// <summary>The widest a shadow's picture may be, in pixels; a large blur is drawn more coarsely.</summary>
    private const int MaximumPixels = 1400;

    public static void Draw(Canvas canvas, Size box, float radius, in BoxShadow shadow)
    {
        if (shadow.Alpha <= 0 || box.Width <= 0 || box.Height <= 0)
            return;

        // The shape the shadow is cast by: the box, grown by the spread and moved by the offset.
        var left = shadow.OffsetX - shadow.Spread;
        var top = shadow.OffsetY - shadow.Spread;
        var width = box.Width + (2 * shadow.Spread);
        var height = box.Height + (2 * shadow.Spread);
        if (width <= 0 || height <= 0)
            return;

        var shapeRadius = Math.Max(0, radius + shadow.Spread);
        if (shadow.Blur <= 0.05f)
        {
            if (shadow.Alpha < 1)
                canvas.BeginOpacityGroup(shadow.Alpha);

            canvas.FillRoundedRectangle(left, top, width, height, shapeRadius, shadow.Color);
            if (shadow.Alpha < 1)
                canvas.EndGroup();

            return;
        }

        // A blur of this much fades away within about one and a half times its own width.
        var margin = shadow.Blur * 1.5f;
        var area = new Size(width + (2 * margin), height + (2 * margin));
        var scale = Resolution(shadow.Blur, area);
        var pixels = ((int)MathF.Ceiling(area.Width * scale), (int)MathF.Ceiling(area.Height * scale));
        if (pixels.Item1 <= 0 || pixels.Item2 <= 0)
            return;

        var samples = Blurred(pixels.Item1, pixels.Item2, scale, margin, width, height, shapeRadius, shadow.Blur / 2, shadow.Alpha);
        canvas.DrawShadow(
            Image.FromGrey(samples, pixels.Item1, pixels.Item2),
            left - margin,
            top - margin,
            area.Width,
            area.Height,
            shadow.Color);
    }

    /// <summary>
    /// How many pixels a point of the shadow is drawn with. A soft edge holds no detail, so a wide blur
    /// is drawn coarsely and a narrow one finely; either way the picture is kept to a sensible size.
    /// </summary>
    private static float Resolution(float blur, Size area)
    {
        var scale = Math.Clamp(8 / Math.Max(blur, 1), 2, 6);
        var longest = Math.Max(area.Width, area.Height) * scale;
        return longest > MaximumPixels ? scale * MaximumPixels / longest : scale;
    }

    /// <summary>
    /// The shadow as a grey picture: white where it is at its darkest and black where it has faded away.
    /// The shape is drawn first and then blurred, which three passes of a running average do as a bell
    /// curve would, without the cost of one.
    /// </summary>
    private static byte[] Blurred(
        int columns,
        int rows,
        float scale,
        float margin,
        float width,
        float height,
        float radius,
        float sigma,
        float alpha)
    {
        var coverage = new float[columns * rows];
        var (left, top) = (margin * scale, margin * scale);
        var (right, bottom) = (left + (width * scale), top + (height * scale));
        var corner = radius * scale;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                // How far outside the shape the middle of this pixel lies; one pixel of it is the edge.
                var distance = Distance(column + 0.5f, row + 0.5f, left, top, right, bottom, corner);
                coverage[(row * columns) + column] = Math.Clamp(0.5f - distance, 0, 1);
            }
        }

        var radiusInPixels = Math.Max(1, (int)MathF.Round(sigma * scale));
        var scratch = new float[coverage.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            Average(coverage, scratch, columns, rows, radiusInPixels, horizontal: true);
            Average(scratch, coverage, columns, rows, radiusInPixels, horizontal: false);
        }

        var samples = new byte[coverage.Length];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (byte)Math.Clamp(MathF.Round(coverage[i] * alpha * 255), 0, 255);

        return samples;
    }

    /// <summary>How far a point lies outside a rounded rectangle, in pixels; inside it the distance is negative.</summary>
    private static float Distance(float x, float y, float left, float top, float right, float bottom, float radius)
    {
        var halfWidth = (right - left) / 2;
        var halfHeight = (bottom - top) / 2;
        radius = Math.Min(radius, Math.Min(halfWidth, halfHeight));

        // Measured from the middle of the shape, where the two halves are the same.
        var dx = Math.Abs(x - (left + halfWidth)) - (halfWidth - radius);
        var dy = Math.Abs(y - (top + halfHeight)) - (halfHeight - radius);
        var outside = MathF.Sqrt((Math.Max(dx, 0) * Math.Max(dx, 0)) + (Math.Max(dy, 0) * Math.Max(dy, 0)));
        return outside + Math.Min(Math.Max(dx, dy), 0) - radius;
    }

    /// <summary>One pass of a running average, along the rows or down the columns.</summary>
    private static void Average(float[] source, float[] target, int columns, int rows, int radius, bool horizontal)
    {
        var (lines, length) = horizontal ? (rows, columns) : (columns, rows);
        var step = horizontal ? 1 : columns;
        var window = (2 * radius) + 1;

        for (var line = 0; line < lines; line++)
        {
            var start = horizontal ? line * columns : line;
            float sum = 0;

            // Outside the picture there is nothing, so the window starts with what little it covers.
            for (var i = 0; i <= radius && i < length; i++)
                sum += source[start + (i * step)];

            for (var i = 0; i < length; i++)
            {
                target[start + (i * step)] = sum / window;

                var leaving = i - radius;
                var entering = i + radius + 1;
                if (leaving >= 0)
                    sum -= source[start + (leaving * step)];

                if (entering < length)
                    sum += source[start + (entering * step)];
            }
        }
    }
}
