using Papira.Fonts;
using Papira.Text;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Colour glyphs: the three ways a font can draw one, and the rules that build an emoji out of several
/// characters. The fonts are a subset of Noto Color Emoji (SIL Open Font License) and two small fonts
/// built for these tests, whose glyphs are plain squares.
/// </summary>
public class ColorGlyphTests
{
    private const string Emoji = "Noto Color Emoji";
    private const string Layers = "Papira Test Color";

    // The characters the built fonts map: a layered glyph, a picture, and the picture of the second font.
    private const string LayeredGlyph = "";
    private const string PictureGlyph = "";

    static ColorGlyphTests()
    {
        foreach (var file in new[] { "NotoColorEmoji-subset.ttf", "PapiraTestColor.ttf", "PapiraTestBitmap.ttf" })
            FontManager.RegisterFont(Path.Combine(AppContext.BaseDirectory, "Fonts", file));
    }

    private static TrueTypeFont Font(string family)
    {
        Assert.True(FontManager.TryResolveFamily(family, FontWeight.Normal, false, out var font), $"{family} must be registered");
        return font.Font;
    }

    // ---- Layered outlines --------------------------------------------------------------------------

    [Fact]
    public void A_glyph_made_of_layers_is_recognised_with_its_colours()
    {
        var font = Font(Layers);
        var glyph = font.GetGlyph(LayeredGlyph[0]);

        Assert.Equal(ColorGlyphKind.Layers, font.Colors.KindOf(glyph));

        var layers = font.Colors.Layers(glyph).ToArray();
        Assert.Equal(2, layers.Length);
        Assert.Equal(new Color(255, 0, 0), layers[0].Color);
        Assert.Equal(new Color(0, 0, 255), layers[1].Color);
        Assert.NotEqual(layers[0].Glyph, layers[1].Glyph);
    }

    [Fact]
    public void The_layers_are_drawn_in_their_own_colours()
    {
        var page = Inspect(Generate(c => c.Text(LayeredGlyph).FontFamily(Layers))).PageContents()[0];

        Assert.Contains("1 0 0 rg", page);
        Assert.Contains("0 0 1 rg", page);

        // The glyph itself is drawn invisibly, so the text can still be selected.
        Assert.Contains("3 Tr", page);
    }

    [Fact]
    public void A_layered_glyph_still_extracts_as_its_character()
    {
        var pdf = Inspect(Generate(c => c.Text(LayeredGlyph).FontFamily(Layers)));

        Assert.Contains(LayeredGlyph, pdf.ExtractText());
    }

    // ---- Pictures ----------------------------------------------------------------------------------

    [Fact]
    public void A_glyph_stored_as_a_picture_is_drawn_as_an_image()
    {
        var font = Font(Layers);
        var glyph = font.GetGlyph(PictureGlyph[0]);
        Assert.Equal(ColorGlyphKind.Bitmap, font.Colors.KindOf(glyph));

        var bitmap = font.Colors.Bitmap(glyph);
        Assert.NotNull(bitmap);
        Assert.Equal(8, bitmap!.Value.Image.Width);

        var pdf = Inspect(Generate(c => c.Text(PictureGlyph).FontFamily(Layers)));
        Assert.Contains("/Subtype/Image", pdf.Raw);
        Assert.Contains(" Do", pdf.PageContents()[0]);
    }

    [Fact]
    public void The_bitmap_tables_of_android_are_read_too()
    {
        var font = Font("Papira Test Bitmap");
        var glyph = font.GetGlyph(LayeredGlyph[0]);

        Assert.Equal(ColorGlyphKind.Bitmap, font.Colors.KindOf(glyph));
        Assert.NotNull(font.Colors.Bitmap(glyph));
    }

    // ---- Drawings ----------------------------------------------------------------------------------

    [Fact]
    public void A_glyph_stored_as_a_drawing_is_drawn_with_the_svg_engine()
    {
        var font = Font(Emoji);
        var glyph = font.GetGlyph(0x1F600); // 😀

        Assert.Equal(ColorGlyphKind.Drawing, font.Colors.KindOf(glyph));
        Assert.NotNull(font.Colors.Drawing(glyph));

        var page = Inspect(Generate(c => c.Text("\U0001F600").FontFamily(Emoji))).PageContents()[0];

        // Filled paths in colour, not a single glyph in the text colour.
        Assert.Contains(" f\n", page);
        Assert.Contains("3 Tr", page);
    }

    // ---- Sequences ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("\U0001F1F9\U0001F1F7")]                  // 🇹🇷, two regional indicators
    [InlineData("\U0001F44D\U0001F3FD")]                  // 👍🏽, a gesture with a skin tone
    [InlineData("1️⃣")]                         // 1️⃣, a keycap
    public void Several_characters_become_one_emoji(string text)
    {
        var font = Font(Emoji);
        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var buffer = new ShapingBuffer();

        TextShaper.Shape(font, codepoints, 0, false, buffer);

        Assert.Equal(1, buffer.Length);
        Assert.NotEqual(0, buffer[0].Glyph);
    }

    [Fact]
    public void The_presentation_selector_is_not_drawn()
    {
        var font = Font(Emoji);
        var buffer = new ShapingBuffer();

        TextShaper.Shape(font, [0x2764, 0xFE0F], 0, false, buffer);

        Assert.Equal(1, buffer.Length);
        Assert.Equal(font.GetGlyph(0x2764), buffer[0].Glyph);
    }

    [Fact]
    public void Emoji_are_recognised_without_looking_at_the_font()
    {
        Assert.True(TextShaper.IsEmoji(0x1F600));   // 😀
        Assert.True(TextShaper.IsEmoji(0x1F1F9));   // a regional indicator
        Assert.True(TextShaper.IsEmoji(0x20E3));    // the keycap mark
        Assert.False(TextShaper.IsEmoji('1'));      // a digit on its own is just a digit
        Assert.False(TextShaper.IsEmoji('a'));
    }

    [Fact]
    public void Text_without_emoji_is_untouched_by_any_of_this()
    {
        var pdf = Inspect(Generate(c => c.Text("Fatura 2026")));

        Assert.DoesNotContain("3 Tr", pdf.PageContents()[0]);
        Assert.Contains("Fatura 2026", pdf.ExtractText());
    }
}
