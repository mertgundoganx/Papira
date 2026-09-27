using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Svg;

internal enum SvgPaintKind : byte { None, Solid, Gradient, Pattern }

/// <summary>
/// A linear or radial gradient as the file declares it. <paramref name="UserSpace"/> tells whether the
/// coordinates are user units or fractions of the shape's bounding box.
/// </summary>
internal sealed record SvgGradient(
    bool Radial,
    float X0,
    float Y0,
    float Radius0,
    float X1,
    float Y1,
    float Radius1,
    ColorStop[] Stops,
    bool UserSpace,
    Matrix Transform);

internal readonly record struct SvgPaint(SvgPaintKind Kind, Color Color, SvgGradient? Gradient, SvgPattern? Pattern = null)
{
    /// <summary>"none": the shape is not painted at all.</summary>
    public static SvgPaint None => default;

    public static SvgPaint Solid(Color color) => new(SvgPaintKind.Solid, color, null);

    /// <summary>A gradient also carries its first stop, which strokes and unsupported cases fall back to.</summary>
    public static SvgPaint FromGradient(SvgGradient gradient) =>
        new(SvgPaintKind.Gradient, gradient.Stops.Length > 0 ? gradient.Stops[0].Color : Colors.Black, gradient);

    /// <summary>A pattern also carries a colour, which is what a shape falls back to where it cannot be drawn.</summary>
    public static SvgPaint FromPattern(SvgPattern pattern) => new(SvgPaintKind.Pattern, Colors.Grey.Medium, null, pattern);

    public bool Paints => Kind != SvgPaintKind.None;
}

/// <summary>
/// The presentation attributes Papira understands. They are inherited, so each element starts from a copy
/// of its parent's style and overwrites what it declares itself.
/// </summary>
internal readonly record struct SvgStyle
{
    public static readonly SvgStyle Initial = new()
    {
        Fill = SvgPaint.Solid(Colors.Black),
        Stroke = SvgPaint.None,
        StrokeWidth = 1,
        FillOpacity = 1,
        StrokeOpacity = 1,
        CurrentColor = Colors.Black,
    };

    public SvgPaint Fill { get; init; }

    public SvgPaint Stroke { get; init; }

    public float StrokeWidth { get; init; }

    public float FillOpacity { get; init; }

    public float StrokeOpacity { get; init; }

    /// <summary>"evenodd" instead of the default "nonzero" winding rule.</summary>
    public bool EvenOdd { get; init; }

    /// <summary>PDF line cap: 0 butt, 1 round, 2 square.</summary>
    public int LineCap { get; init; }

    /// <summary>PDF line join: 0 miter, 1 round, 2 bevel.</summary>
    public int LineJoin { get; init; }

    public float[]? Dashes { get; init; }

    public float DashOffset { get; init; }

    /// <summary>The value "currentColor" resolves to, inherited from the "color" property.</summary>
    public Color CurrentColor { get; init; }

    public bool Hidden { get; init; }
}
