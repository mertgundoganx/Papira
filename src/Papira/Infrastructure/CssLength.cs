namespace Papira.Infrastructure;

/// <summary>
/// A length that is only known once the element is laid out: so many points, plus a share of the space
/// the element is offered, plus a share of the page. A CSS percentage, the viewport units and the sums
/// <c>calc()</c> writes all come out as one of these, which is why they add up like vectors.
/// </summary>
internal readonly record struct CssLength(float Points, float Percent = 0, float ViewportWidth = 0, float ViewportHeight = 0)
{
    public static CssLength FromPoints(float points) => new(points);

    /// <summary>True when the value needs nothing around it to be known, so it can be used while composing.</summary>
    public bool IsAbsolute => Percent == 0 && ViewportWidth == 0 && ViewportHeight == 0;

    /// <summary>
    /// The length in points. <paramref name="basis"/> is what a percentage is a percentage of — the width
    /// or the height of the space the element is offered.
    /// </summary>
    public float Resolve(float basis, Size viewport) =>
        Points + (Percent * basis / 100) + (ViewportWidth * viewport.Width / 100) + (ViewportHeight * viewport.Height / 100);

    public static CssLength operator +(CssLength first, CssLength second) => new(
        first.Points + second.Points,
        first.Percent + second.Percent,
        first.ViewportWidth + second.ViewportWidth,
        first.ViewportHeight + second.ViewportHeight);

    public static CssLength operator -(CssLength first, CssLength second) => new(
        first.Points - second.Points,
        first.Percent - second.Percent,
        first.ViewportWidth - second.ViewportWidth,
        first.ViewportHeight - second.ViewportHeight);

    public static CssLength operator *(CssLength length, float factor) => new(
        length.Points * factor,
        length.Percent * factor,
        length.ViewportWidth * factor,
        length.ViewportHeight * factor);

    public static CssLength operator /(CssLength length, float divisor) =>
        divisor == 0 ? default : length * (1 / divisor);
}
