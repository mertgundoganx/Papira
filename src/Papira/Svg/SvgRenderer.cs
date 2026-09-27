using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Svg;

/// <summary>
/// Draws a parsed SVG tree with the PDF path operators. Coordinates are transformed here rather than with
/// the canvas transform stack, which keeps the emitted content stream flat and short.
/// </summary>
internal sealed class SvgRenderer(Canvas canvas)
{
    /// <summary>Draws the document into a box of <paramref name="width"/> by <paramref name="height"/> points.</summary>
    public void Draw(SvgDocument document, float width, float height)
    {
        if (width <= 0 || height <= 0 || document.ViewBoxWidth <= 0 || document.ViewBoxHeight <= 0)
            return;

        var scaleX = width / document.ViewBoxWidth;
        var scaleY = height / document.ViewBoxHeight;
        if (document.PreserveAspectRatio)
            scaleX = scaleY = MathF.Min(scaleX, scaleY);

        // The element is sized from the aspect ratio, so the drawing is centred only when it was overridden.
        var offsetX = (width - document.ViewBoxWidth * scaleX) / 2;
        var offsetY = (height - document.ViewBoxHeight * scaleY) / 2;

        var matrix = Matrix.Multiply(
            Matrix.Multiply(Matrix.Translation(-document.ViewBoxX, -document.ViewBoxY), Matrix.Scaling(scaleX, scaleY)),
            Matrix.Translation(offsetX, offsetY));

        Draw(document.Root, matrix);
    }

    /// <summary>Draws a tree with a transform of its own, which a glyph from a font needs.</summary>
    public void DrawNode(SvgNode node, Matrix matrix) => Draw(node, matrix);

    private void Draw(SvgNode node, Matrix parent)
    {
        var matrix = Matrix.Multiply(node.Transform, parent);
        var groups = 0;

        if (node.Opacity < 1)
        {
            canvas.BeginOpacityGroup(node.Opacity);
            groups++;
        }

        if (node.Clip is { } clip)
        {
            canvas.BeginGroup();
            WritePath(clip, matrix);
            canvas.ClipPath(node.ClipEvenOdd);
            groups++;
        }

        switch (node)
        {
            case SvgGroupNode group:
                foreach (var child in group.Children)
                    Draw(child, matrix);

                break;

            case SvgShapeNode shape:
                DrawShape(shape, matrix);
                break;
        }

        while (groups-- > 0)
            canvas.EndGroup();
    }

    private void DrawShape(SvgShapeNode shape, Matrix matrix)
    {
        var style = shape.Style;
        var scale = matrix.Scale;
        var strokeWidth = style.StrokeWidth * scale;
        var strokes = style.Stroke.Paints && strokeWidth > 0;
        var fills = style.Fill.Paints;

        // One painting operator when the fill is a plain color and both parts share the same opacity.
        if (fills && strokes && style.Fill.Kind == SvgPaintKind.Solid && MathF.Abs(style.FillOpacity - style.StrokeOpacity) < 1e-3f)
        {
            BeginOpacity(style.FillOpacity);
            SetLineStyle(style, scale);
            canvas.BeginPath(style.Fill.Color, style.Stroke.Color, strokeWidth);
            WritePath(shape.Path, matrix);
            canvas.EndPath(true, true, style.EvenOdd);
            EndOpacity(style.FillOpacity);
            return;
        }

        if (fills)
        {
            BeginOpacity(style.FillOpacity);

            // A gradient that cannot be placed (a shape with no area) falls back to its first stop.
            var gradient = style.Fill.Gradient is { } declared ? Shading(declared, shape.Path, matrix) : null;
            if (gradient is { } placed)
                canvas.BeginShadingPath(placed.Shading, placed.Local);
            else
                canvas.BeginPath(style.Fill.Color, null, 0);

            WritePath(shape.Path, matrix);
            canvas.EndPath(true, false, style.EvenOdd);
            EndOpacity(style.FillOpacity);
        }

        if (strokes)
        {
            BeginOpacity(style.StrokeOpacity);
            SetLineStyle(style, scale);

            // A gradient stroke falls back to the first stop of the gradient.
            canvas.BeginPath(null, style.Stroke.Color, strokeWidth);
            WritePath(shape.Path, matrix);
            canvas.EndPath(false, true);
            EndOpacity(style.StrokeOpacity);
        }
    }

    private void SetLineStyle(SvgStyle style, float scale)
    {
        float[]? dashes = null;
        if (style.Dashes is { Length: > 0 } pattern)
        {
            dashes = new float[pattern.Length];
            for (var i = 0; i < pattern.Length; i++)
                dashes[i] = pattern[i] * scale;
        }

        canvas.SetLineStyle(style.LineCap, style.LineJoin, dashes, style.DashOffset * scale);
    }

    /// <summary>
    /// Places a gradient: in user space units its coordinates only need the element's transform, while in
    /// object bounding box units they are fractions of the shape's own box.
    /// </summary>
    private static (Shading Shading, Matrix Local)? Shading(SvgGradient gradient, SvgPath path, Matrix matrix)
    {
        var local = matrix;
        if (!gradient.UserSpace)
        {
            var (minX, minY, maxX, maxY) = path.Bounds();
            float width = maxX - minX, height = maxY - minY;
            if (width <= 0 || height <= 0)
                return null; // a degenerate box has no gradient to map onto

            local = Matrix.Multiply(Matrix.Multiply(Matrix.Scaling(width, height), Matrix.Translation(minX, minY)), matrix);
        }

        var shading = new Shading(
            gradient.Radial,
            gradient.X0,
            gradient.Y0,
            gradient.Radius0,
            gradient.X1,
            gradient.Y1,
            gradient.Radius1,
            gradient.Stops);

        return (shading, Matrix.Multiply(gradient.Transform, local));
    }

    private void WritePath(SvgPath path, Matrix matrix)
    {
        foreach (var command in path.Commands)
        {
            var (x, y) = matrix.Apply(command.X, command.Y);
            switch (command.Op)
            {
                case 'm':
                    canvas.MoveTo(x, y);
                    break;
                case 'l':
                    canvas.LineTo(x, y);
                    break;
                case 'c':
                    var (x1, y1) = matrix.Apply(command.X1, command.Y1);
                    var (x2, y2) = matrix.Apply(command.X2, command.Y2);
                    canvas.CurveTo(x1, y1, x2, y2, x, y);
                    break;
                default:
                    canvas.ClosePath();
                    break;
            }
        }
    }

    private void BeginOpacity(float opacity)
    {
        if (opacity < 1)
            canvas.BeginOpacityGroup(opacity);
    }

    private void EndOpacity(float opacity)
    {
        if (opacity < 1)
            canvas.EndGroup();
    }
}
