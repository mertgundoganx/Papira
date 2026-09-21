namespace Papira;

/// <summary>Page dimensions in points (1 pt = 1/72 inch).</summary>
public readonly record struct PageSize(float Width, float Height)
{
    public PageSize Landscape() => Width >= Height ? this : new PageSize(Height, Width);
    public PageSize Portrait() => Width <= Height ? this : new PageSize(Height, Width);
}

public static class PageSizes
{
    public static readonly PageSize A3 = new(841.89f, 1190.55f);
    public static readonly PageSize A4 = new(595.28f, 841.89f);
    public static readonly PageSize A5 = new(419.53f, 595.28f);
    public static readonly PageSize A6 = new(297.64f, 419.53f);
    public static readonly PageSize Letter = new(612, 792);
    public static readonly PageSize Legal = new(612, 1008);
}

/// <summary>Unit conversion helpers. All Papira APIs use points.</summary>
public static class Unit
{
    public static float Millimetre(float value) => value * 72f / 25.4f;
    public static float Centimetre(float value) => value * 72f / 2.54f;
    public static float Inch(float value) => value * 72f;
}
