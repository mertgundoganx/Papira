namespace Papira.Infrastructure;

/// <summary>
/// A 2D affine transform, written as PDF writes it: <c>[A B C D E F]</c>.
/// Papira's layout coordinates have their origin at the top left with y pointing down;
/// the canvas keeps a matrix that maps them to PDF page coordinates (origin bottom left, y up).
/// </summary>
internal readonly record struct Matrix(float A, float B, float C, float D, float E, float F)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translation(float x, float y) => new(1, 0, 0, 1, x, y);

    public static Matrix Scaling(float x, float y) => new(x, 0, 0, y, 0, 0);

    /// <summary>Rotation by <paramref name="degrees"/>, clockwise in layout coordinates.</summary>
    public static Matrix Rotation(float degrees)
    {
        var radians = degrees * MathF.PI / 180;
        float sin = MathF.Sin(radians), cos = MathF.Cos(radians);
        return new Matrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>The transform that applies <paramref name="first"/> and then <paramref name="second"/>.</summary>
    public static Matrix Multiply(Matrix first, Matrix second) => new(
        first.A * second.A + first.B * second.C,
        first.A * second.B + first.B * second.D,
        first.C * second.A + first.D * second.C,
        first.C * second.B + first.D * second.D,
        first.E * second.A + first.F * second.C + second.E,
        first.E * second.B + first.F * second.D + second.F);

    public (float X, float Y) Apply(float x, float y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>True when the transform keeps horizontal and vertical lines axis-aligned.</summary>
    public bool IsAxisAligned => MathF.Abs(B) < 1e-6f && MathF.Abs(C) < 1e-6f;

    /// <summary>Average scale factor, used for line widths and flatness of curves.</summary>
    public float Scale => (MathF.Sqrt(A * A + B * B) + MathF.Sqrt(C * C + D * D)) / 2;
}
