using Papira.Infrastructure;

namespace Papira.Rendering;

/// <summary>
/// One colour of a gradient. <paramref name="Alpha"/> is the transparency of that stop, which PDF cannot
/// express in a shading and which the renderer therefore turns into a soft mask.
/// </summary>
internal readonly record struct ColorStop(float Offset, Color Color, float Alpha = 1);

/// <summary>
/// A linear or radial gradient. Coordinates are in the layout space of the element being filled
/// (origin at its top-left corner); <see cref="Matrix"/> places that space on the page.
/// </summary>
internal sealed record Shading(bool Radial, float X0, float Y0, float Radius0, float X1, float Y1, float Radius1, ColorStop[] Stops)
{
    public Matrix Matrix { get; init; } = Matrix.Identity;

    /// <summary>True when some of the gradient is transparent, which needs a mask beside the colours.</summary>
    public bool HasAlpha => Array.Exists(Stops, stop => stop.Alpha < 1);
}
