using Papira.Fonts;
using Papira.Text;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// The scripts of India, which are written in syllables: what is written is not the order the glyphs
/// are drawn in. Papira's output was compared with HarfBuzz glyph by glyph and position by position —
/// every word of a page of real text in all nine scripts came out identical, and so did 17,903 lines
/// of generated syllables but for a handful.
/// </summary>
public class IndicTests
{
    private const string Devanagari = "Noto Sans Devanagari";
    private const string Tamil = "Noto Sans Tamil";

    static IndicTests()
    {
        FontManager.RegisterFontWithCustomName(Devanagari, Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansDevanagari-subset.ttf"));
        FontManager.RegisterFontWithCustomName(Tamil, Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansTamil-subset.ttf"));
    }

    private static TrueTypeFont Font(string family)
    {
        Assert.True(FontManager.TryResolveFamily(family, FontWeight.Normal, false, out var font), $"{family} must be registered");
        return font.Font;
    }

    private static ShapedGlyph[] Shape(string text, string family = Devanagari)
    {
        var buffer = new ShapingBuffer();
        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        TextShaper.Shape(Font(family), codepoints, TextShaper.ScriptOf(codepoints[0]), false, buffer);
        return buffer.Glyphs.ToArray();
    }

    private static byte[] Draw(string text, string family = Devanagari) =>
        Generate(c => c.Text(text).FontFamily(family).FontSize(20));

    /// <summary>The text of a document, as a reader that follows what the drawing stands for reads it.</summary>
    private static string Text(PdfInspector pdf) =>
        pdf.ExtractText().Replace("\n", string.Empty, StringComparison.Ordinal);

    // ---- What the characters are -----------------------------------------------------------------

    [Theory]
    [InlineData('क', IndicCategory.C, IndicPosition.BaseC)]          // a consonant
    [InlineData('र', IndicCategory.Ra, IndicPosition.BaseC)]         // the letter that becomes a reph
    [InlineData('्', IndicCategory.H, IndicPosition.BelowC)]         // the virama
    [InlineData('ि', IndicCategory.M, IndicPosition.PreM)]           // a vowel sign written before its consonant
    [InlineData('ा', IndicCategory.M, IndicPosition.AfterSub)]       // one written after it
    [InlineData('ं', IndicCategory.SM, IndicPosition.Smvd)]          // a syllable modifier
    [InlineData('़', IndicCategory.N, IndicPosition.End)]            // the nukta, which hangs on its letter
    [InlineData('अ', IndicCategory.V, IndicPosition.BaseC)]          // an independent vowel
    internal void Characters_are_read_from_the_unicode_database(int codepoint, IndicCategory category, IndicPosition position)
    {
        var (actual, where) = IndicSyllables.Lookup(codepoint);

        Assert.Equal(category, actual);
        Assert.Equal(position, where);
    }

    [Fact]
    public void Text_is_split_into_syllables()
    {
        var text = "नमस्ते".EnumerateRunes().Select(rune => rune.Value).ToArray();
        var categories = new IndicCategory[text.Length];
        var positions = new IndicPosition[text.Length];
        IndicSyllables.Classify(text, categories, positions);

        var syllables = new List<(int Start, int End, IndicSyllableKind Kind)>();
        IndicSyllables.Split(categories, syllables);

        // na | ma | ste: the last one holds two consonants joined by a virama, and a vowel sign.
        Assert.Equal(3, syllables.Count);
        Assert.All(syllables, syllable => Assert.Equal(IndicSyllableKind.Consonant, syllable.Kind));
        Assert.Equal([(0, 1), (1, 2), (2, 6)], syllables.Select(s => (s.Start, s.End)));
    }

    [Fact]
    public void A_vowel_sign_on_its_own_is_shown_around_a_dotted_circle()
    {
        // Nothing to hang it on, so a reader is shown what is missing.
        var glyphs = Shape("ा");

        Assert.Equal(2, glyphs.Length);
        Assert.Equal(Font(Devanagari).GetGlyph(0x25CC), glyphs[0].Glyph);
    }

    // ---- Putting a syllable in order -------------------------------------------------------------

    [Fact]
    public void Consonants_joined_by_a_virama_become_one_glyph()
    {
        // क + ् + ष becomes the conjunct क्ष.
        var joined = Shape("क्ष");
        var apart = Shape("कष");

        Assert.Single(joined);
        Assert.Equal(2, apart.Length);
    }

    [Fact]
    public void A_vowel_sign_written_after_a_consonant_may_be_drawn_before_it()
    {
        // In "कि" the vowel sign is written second and drawn first.
        var glyphs = Shape("कि");

        // The vowel sign comes second in the text and first in the drawing.
        Assert.Equal(2, glyphs.Length);
        Assert.Equal(1, glyphs[0].Cluster);
        Assert.Equal(0, glyphs[1].Cluster);
    }

    [Fact]
    public void An_initial_ra_climbs_onto_the_syllable_as_a_reph()
    {
        // "र्म" is written Ra, virama, Ma and drawn as Ma with the reph above it, at the end.
        var glyphs = Shape("र्म");

        // Ra and the virama become the reph, which is drawn after the consonant it belongs to.
        Assert.Equal(2, glyphs.Length);
        Assert.Equal(2, glyphs[0].Cluster);
        Assert.Equal(0, glyphs[1].Cluster);
        Assert.NotEqual(Font(Devanagari).GetGlyph(0x0930), glyphs[1].Glyph);
    }

    [Fact]
    public void A_vowel_sign_written_in_two_parts_is_drawn_in_two_places()
    {
        // Tamil "மொ" holds a vowel sign that Unicode composes; one half goes before the consonant.
        var glyphs = Shape("மொ", Tamil);

        // Three glyphs out of two characters: the two halves of the sign, with the consonant between.
        Assert.Equal(3, glyphs.Length);
        Assert.Equal(1, glyphs[0].Cluster);
        Assert.Equal(0, glyphs[1].Cluster);
        Assert.Equal(1, glyphs[2].Cluster);
    }

    [Fact]
    public void A_non_joiner_keeps_the_letters_apart()
    {
        var joined = Shape("क्ष");
        var apart = Shape("क्‌ष");

        Assert.Single(joined);
        Assert.True(apart.Length > 1, "the non-joiner asked for the letters to stay apart");
    }

    // ---- In a document ---------------------------------------------------------------------------

    [Fact]
    public void The_text_of_a_document_extracts_as_it_was_written()
    {
        // Whatever the glyphs became, the characters behind them are the ones that were written.
        var pdf = Inspect(Draw("नमस्ते दुनिया"));

        Assert.Equal("नमस्ते दुनिया", Text(pdf));
    }

    [Fact]
    public void Tamil_text_extracts_as_it_was_written()
    {
        var pdf = Inspect(Draw("வணக்கம் தமிழ்", Tamil));

        Assert.Equal("வணக்கம் தமிழ்", Text(pdf));
    }

    [Fact]
    public void A_line_of_indic_text_is_narrower_than_the_characters_it_was_written_with()
    {
        // The conjuncts and the reph are drawn as single glyphs, so the line comes out shorter.
        var glyphs = Shape("प्रधानमंत्री");

        Assert.True(glyphs.Length < "प्रधानमंत्री".Length, $"{glyphs.Length} glyphs for {"प्रधानमंत्री".Length} characters");
    }

    [Fact]
    public void The_script_is_recognised_and_asked_for_by_the_name_the_font_uses()
    {
        var font = Font(Devanagari);
        var script = TextShaper.ScriptOf('न');

        Assert.True(IndicShaper.IsIndic(script));

        // The font follows the newer rules, which have a tag of their own.
        Assert.Equal(TrueTypeFont.Tag("dev2"), IndicShaper.ScriptTag(font, script));
    }

    [Fact]
    public void Every_indic_script_is_recognised()
    {
        var scripts = new[] { 'न', 'ব', 'ਪ', 'ગ', 'ଓ', 'த', 'త', 'ಕ', 'മ' };

        Assert.All(scripts, character => Assert.True(
            IndicShaper.IsIndic(TextShaper.ScriptOf(character)),
            $"{character} should be shaped by the Indic rules"));
    }

    [Fact]
    public void Text_of_a_script_the_font_does_not_have_still_draws_something()
    {
        // The Tamil font has no Devanagari; nothing should throw.
        var pdf = Inspect(Draw("नमस्ते", Tamil));

        Assert.Equal(1, pdf.PageCount);
    }
}
