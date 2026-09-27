using Papira.Fonts;

namespace Papira.Rendering;

/// <summary>Fonts and images referenced by a document, with the glyphs actually used per font.</summary>
internal sealed class DocumentResources
{
    private readonly Dictionary<TrueTypeFont, FontUsage> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Image, ImageUsage> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<float, string> _opacities = [];
    private readonly List<(string Name, Shading Shading)> _shadings = [];
    private readonly List<(string Name, Shading Shading, float Width, float Height)> _softMasks = [];

    public IReadOnlyCollection<FontUsage> Fonts => _fonts.Values;
    public IReadOnlyCollection<ImageUsage> Images => _images.Values;

    /// <summary>Alpha values used by opacity groups, with their graphics-state names.</summary>
    public IReadOnlyCollection<KeyValuePair<float, string>> Opacities => _opacities;

    /// <summary>Gradient patterns; one entry per use, because each carries its own placement matrix.</summary>
    public IReadOnlyList<(string Name, Shading Shading)> Shadings => _shadings;

    /// <summary>Masks that make part of a gradient transparent, with the area each one covers.</summary>
    public IReadOnlyList<(string Name, Shading Shading, float Width, float Height)> SoftMasks => _softMasks;

    public string GetOpacity(float alpha)
    {
        var rounded = MathF.Round(Math.Clamp(alpha, 0, 1), 3);
        if (!_opacities.TryGetValue(rounded, out var name))
            _opacities[rounded] = name = "GS" + (_opacities.Count + 1);
        return name;
    }

    public string GetShading(Shading shading)
    {
        var name = "Sh" + (_shadings.Count + 1);
        _shadings.Add((name, shading));
        return name;
    }

    /// <summary>
    /// A graphics state that masks what is drawn with the transparency of a gradient. PDF keeps colour and
    /// transparency apart: the colours come from the shading, the transparency from a mask built out of
    /// the same gradient in shades of grey.
    /// </summary>
    public string GetSoftMask(Shading shading, float width, float height)
    {
        var name = "GM" + (_softMasks.Count + 1);
        _softMasks.Add((name, shading, width, height));
        return name;
    }

    public FontUsage GetFont(TrueTypeFont font)
    {
        if (!_fonts.TryGetValue(font, out var usage))
            _fonts[font] = usage = new FontUsage(font, "F" + (_fonts.Count + 1));
        return usage;
    }

    public ImageUsage GetImage(Image image)
    {
        if (!_images.TryGetValue(image, out var usage))
            _images[image] = usage = new ImageUsage(image, "I" + (_images.Count + 1));
        return usage;
    }
}

internal sealed class FontUsage(TrueTypeFont font, string name)
{
    public TrueTypeFont Font { get; } = font;
    public string Name { get; } = name;

    /// <summary>Unicode codepoint per used glyph id; 0 means unused, -1 used without a mapping. Glyph 0 is always embedded.</summary>
    public int[] GlyphToUnicode { get; } = new int[font.GlyphCount + 1];

    /// <summary>The characters behind the glyphs that stand for several of them, such as a ligature.</summary>
    public Dictionary<ushort, int[]> GlyphToText { get; } = [];

    /// <summary>Records that a glyph stands for a whole sequence of characters, so it extracts as all of them.</summary>
    public void UseSequence(ushort glyph, ReadOnlySpan<int> codepoints)
    {
        if (glyph == 0 || glyph >= GlyphToUnicode.Length || codepoints.Length == 0)
            return;

        if (codepoints.Length > 1)
            GlyphToText.TryAdd(glyph, codepoints.ToArray());

        Use(glyph, codepoints[0]);
    }

    public void Use(ushort glyph, int codepoint)
    {
        // Glyph 0 (.notdef) stands for every character missing from the font; it gets no Unicode mapping,
        // otherwise all missing characters would extract as the first one encountered.
        if (glyph == 0 || glyph >= GlyphToUnicode.Length || GlyphToUnicode[glyph] != 0)
            return;

        GlyphToUnicode[glyph] = codepoint == 0 ? -1 : codepoint;
    }
}

internal sealed class ImageUsage(Image image, string name)
{
    public Image Image { get; } = image;
    public string Name { get; } = name;
}
