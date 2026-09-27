using Papira.Rendering;

namespace Papira;

/// <summary>
/// Drawing surface handed to <see cref="ContainerExtensions.Canvas"/>. Coordinates are in points,
/// with the origin at the top-left corner of the element and y pointing down.
/// Build a shape with the path methods, then paint it with <see cref="Fill"/> or <see cref="Stroke"/>.
/// </summary>
public interface IDrawingCanvas
{
    void MoveTo(float x, float y);

    void LineTo(float x, float y);

    /// <summary>Cubic Bézier curve to (<paramref name="x3"/>, <paramref name="y3"/>) with two control points.</summary>
    void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3);

    /// <summary>Closes the current shape with a straight line back to its start.</summary>
    void Close();

    void Rectangle(float x, float y, float width, float height);

    void RoundedRectangle(float x, float y, float width, float height, float radius);

    void Circle(float centerX, float centerY, float radius);

    /// <summary>Fills the shape built so far and starts a new one.</summary>
    void Fill(Color color);

    /// <summary>Strokes the shape built so far and starts a new one.</summary>
    void Stroke(Color color, float width = 1);

    void FillAndStroke(Color fill, Color stroke, float width = 1);

    /// <summary>Convenience for a single straight line.</summary>
    void Line(float x1, float y1, float x2, float y2, Color color, float width = 1);
}

internal sealed class DrawingCanvas(Canvas canvas) : IDrawingCanvas
{
    // Circular arcs are approximated with four cubic Béziers.
    private const float Kappa = 0.5523f;

    // A path is collected first and written when it is painted: PDF does not allow color operators
    // between the path and its painting operator.
    private readonly List<(char Op, float A, float B, float C, float D, float E, float F)> _path = [];

    public void MoveTo(float x, float y) => _path.Add(('m', x, y, 0, 0, 0, 0));

    public void LineTo(float x, float y) => _path.Add(('l', x, y, 0, 0, 0, 0));

    public void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3) =>
        _path.Add(('c', x1, y1, x2, y2, x3, y3));

    public void Close() => _path.Add(('h', 0, 0, 0, 0, 0, 0));

    public void Rectangle(float x, float y, float width, float height)
    {
        MoveTo(x, y);
        LineTo(x + width, y);
        LineTo(x + width, y + height);
        LineTo(x, y + height);
        Close();
    }

    public void RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        if (radius <= 0)
        {
            Rectangle(x, y, width, height);
            return;
        }

        var c = radius * Kappa;
        float right = x + width, bottom = y + height;

        MoveTo(x + radius, y);
        LineTo(right - radius, y);
        CurveTo(right - radius + c, y, right, y + radius - c, right, y + radius);
        LineTo(right, bottom - radius);
        CurveTo(right, bottom - radius + c, right - radius + c, bottom, right - radius, bottom);
        LineTo(x + radius, bottom);
        CurveTo(x + radius - c, bottom, x, bottom - radius + c, x, bottom - radius);
        LineTo(x, y + radius);
        CurveTo(x, y + radius - c, x + radius - c, y, x + radius, y);
        Close();
    }

    public void Circle(float centerX, float centerY, float radius)
    {
        var c = radius * Kappa;
        MoveTo(centerX, centerY - radius);
        CurveTo(centerX + c, centerY - radius, centerX + radius, centerY - c, centerX + radius, centerY);
        CurveTo(centerX + radius, centerY + c, centerX + c, centerY + radius, centerX, centerY + radius);
        CurveTo(centerX - c, centerY + radius, centerX - radius, centerY + c, centerX - radius, centerY);
        CurveTo(centerX - radius, centerY - c, centerX - c, centerY - radius, centerX, centerY - radius);
        Close();
    }

    public void Fill(Color color) => Paint(color, null, 0);

    public void Stroke(Color color, float width = 1) => Paint(null, color, width);

    public void FillAndStroke(Color fill, Color stroke, float width = 1) => Paint(fill, stroke, width);

    public void Line(float x1, float y1, float x2, float y2, Color color, float width = 1)
    {
        MoveTo(x1, y1);
        LineTo(x2, y2);
        Stroke(color, width);
    }

    private void Paint(Color? fill, Color? stroke, float width)
    {
        if (_path.Count == 0)
            return;

        canvas.BeginPath(fill, stroke, width);
        foreach (var (op, a, b, c, d, e, f) in _path)
        {
            switch (op)
            {
                case 'm': canvas.MoveTo(a, b); break;
                case 'l': canvas.LineTo(a, b); break;
                case 'c': canvas.CurveTo(a, b, c, d, e, f); break;
                default: canvas.ClosePath(); break;
            }
        }

        canvas.EndPath(fill != null, stroke != null);
        _path.Clear();
    }
}
