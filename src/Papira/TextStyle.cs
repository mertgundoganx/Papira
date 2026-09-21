namespace Papira;

public enum FontWeight
{
    Thin = 100,
    ExtraLight = 200,
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800,
    Black = 900,
}

/// <summary>
/// Immutable text style. Unset properties are inherited from the enclosing default style
/// (span → text block → container → page → document → built-in defaults).
/// </summary>
public sealed record TextStyle
{
    internal string? Family { get; init; }
    internal IReadOnlyList<string>? FallbackFamilies { get; init; }
    internal float? Size { get; init; }
    internal FontWeight? Weight { get; init; }
    internal bool? IsItalic { get; init; }
    internal Color? TextColor { get; init; }
    internal float? LineHeightFactor { get; init; }
    internal float? Spacing { get; init; }
    internal bool? IsUnderline { get; init; }
    internal bool? IsStrikethrough { get; init; }

    /// <summary>A style with nothing set; everything is inherited.</summary>
    public static TextStyle Default { get; } = new();

    internal static TextStyle BuiltIn { get; } = new()
    {
        Family = FontManager.DefaultFontFamily,
        Size = 12,
        Weight = Papira.FontWeight.Normal,
        IsItalic = false,
        TextColor = Colors.Black,
        LineHeightFactor = null,
        Spacing = 0,
        IsUnderline = false,
        IsStrikethrough = false,
    };

    /// <summary>
    /// Sets the font family. Characters missing from it are taken from <paramref name="fallbacks"/> in order,
    /// then from <see cref="FontManager.FallbackFontFamilies"/> and finally from any other registered font.
    /// </summary>
    public TextStyle FontFamily(string family, params string[] fallbacks) =>
        this with { Family = family, FallbackFamilies = fallbacks.Length > 0 ? fallbacks.ToArray() : FallbackFamilies };
    public TextStyle FontSize(float size) => this with { Size = size > 0 ? size : throw new ArgumentOutOfRangeException(nameof(size)) };
    public TextStyle FontWeight(FontWeight weight) => this with { Weight = weight };
    public TextStyle Thin() => FontWeight(Papira.FontWeight.Thin);
    public TextStyle Light() => FontWeight(Papira.FontWeight.Light);
    public TextStyle NormalWeight() => FontWeight(Papira.FontWeight.Normal);
    public TextStyle Medium() => FontWeight(Papira.FontWeight.Medium);
    public TextStyle SemiBold() => FontWeight(Papira.FontWeight.SemiBold);
    public TextStyle Bold() => FontWeight(Papira.FontWeight.Bold);
    public TextStyle Black() => FontWeight(Papira.FontWeight.Black);
    public TextStyle Italic(bool value = true) => this with { IsItalic = value };
    public TextStyle FontColor(Color color) => this with { TextColor = color };

    /// <summary>Line height as a multiple of the font size. When unset, the font's natural line spacing is used.</summary>
    public TextStyle LineHeight(float factor) => this with { LineHeightFactor = factor };

    /// <summary>Additional space between characters, in points.</summary>
    public TextStyle LetterSpacing(float points) => this with { Spacing = points };

    public TextStyle Underline(bool value = true) => this with { IsUnderline = value };
    public TextStyle Strikethrough(bool value = true) => this with { IsStrikethrough = value };

    /// <summary>Fills unset properties of this style from <paramref name="parent"/>.</summary>
    internal TextStyle InheritFrom(TextStyle parent)
    {
        if (ReferenceEquals(this, Default))
            return parent;

        return new TextStyle
        {
            Family = Family ?? parent.Family,
            FallbackFamilies = FallbackFamilies ?? parent.FallbackFamilies,
            Size = Size ?? parent.Size,
            Weight = Weight ?? parent.Weight,
            IsItalic = IsItalic ?? parent.IsItalic,
            TextColor = TextColor ?? parent.TextColor,
            LineHeightFactor = LineHeightFactor ?? parent.LineHeightFactor,
            Spacing = Spacing ?? parent.Spacing,
            IsUnderline = IsUnderline ?? parent.IsUnderline,
            IsStrikethrough = IsStrikethrough ?? parent.IsStrikethrough,
        };
    }
}
