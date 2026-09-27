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
    private readonly List<MaskForm> _maskForms = [];
    private readonly List<TilingPattern> _tilings = [];

    public IReadOnlyCollection<FontUsage> Fonts => _fonts.Values;
    public IReadOnlyCollection<ImageUsage> Images => _images.Values;

    /// <summary>Alpha values used by opacity groups, with their graphics-state names.</summary>
    public IReadOnlyCollection<KeyValuePair<float, string>> Opacities => _opacities;

    /// <summary>Gradient patterns; one entry per use, because each carries its own placement matrix.</summary>
    public IReadOnlyList<(string Name, Shading Shading)> Shadings => _shadings;

    /// <summary>Masks that make part of a gradient transparent, with the area each one covers.</summary>
    public IReadOnlyList<(string Name, Shading Shading, float Width, float Height)> SoftMasks => _softMasks;

    /// <summary>Masks drawn from a piece of a drawing: what is light shows through, what is dark hides.</summary>
    public IReadOnlyList<MaskForm> MaskForms => _maskForms;

    /// <summary>Drawings repeated over and over to fill a shape.</summary>
    public IReadOnlyList<TilingPattern> Tilings => _tilings;

    /// <summary>
    /// A graphics state that hides what is drawn wherever the given drawing is dark. The content is a
    /// finished content stream, in the coordinates of the page.
    /// </summary>
    public string GetMaskForm(byte[] content, float left, float bottom, float right, float top)
    {
        var name = "GK" + (_maskForms.Count + 1);
        _maskForms.Add(new MaskForm(name, content, left, bottom, right, top));
        return name;
    }

    /// <summary>A pattern: one tile of a drawing, and how often and where it is repeated.</summary>
    public string GetTiling(byte[] content, float left, float bottom, float right, float top, float xStep, float yStep, Infrastructure.Matrix matrix)
    {
        var name = "Pt" + (_tilings.Count + 1);
        _tilings.Add(new TilingPattern(name, content, left, bottom, right, top, xStep, yStep, matrix));
        return name;
    }

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

/// <summary>A mask drawn from part of a drawing, with the area it covers on the page.</summary>
internal sealed record MaskForm(string Name, byte[] Content, float Left, float Bottom, float Right, float Top);

/// <summary>One tile of a pattern, with how far apart the copies stand and where they start.</summary>
internal sealed record TilingPattern(
    string Name,
    byte[] Content,
    float Left,
    float Bottom,
    float Right,
    float Top,
    float XStep,
    float YStep,
    Infrastructure.Matrix Matrix);

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
