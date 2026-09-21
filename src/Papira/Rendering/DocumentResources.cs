using Papira.Fonts;

namespace Papira.Rendering;

/// <summary>Fonts and images referenced by a document, with the glyphs actually used per font.</summary>
internal sealed class DocumentResources
{
    private readonly Dictionary<TrueTypeFont, FontUsage> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Image, ImageUsage> _images = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyCollection<FontUsage> Fonts => _fonts.Values;
    public IReadOnlyCollection<ImageUsage> Images => _images.Values;

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
