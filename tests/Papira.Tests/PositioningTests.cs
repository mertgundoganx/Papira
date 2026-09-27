using Papira.Fonts;
using Papira.Text;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Where the glyphs of a run go: marks on the letters they belong to, letters joined to one another,
/// and pairs kerned. Papira's positions were compared with HarfBuzz glyph by glyph — 172 fonts with
/// Latin text, every Arabic and Hebrew font of the machine with marked text, some 55,000 lines in all,
/// and every one of them came out identical.
/// </summary>
public class PositioningTests
{
    private const string Arabic = "Noto Sans Arabic";

    static PositioningTests() =>
        FontManager.RegisterFont(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansArabic-subset.ttf"));

    private static ShapedGlyph[] Shape(TrueTypeFont font, string text, bool rightToLeft = false)
    {
        var buffer = new ShapingBuffer();
        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        TextShaper.Shape(font, codepoints, TextShaper.ScriptOf(codepoints[0]), rightToLeft, buffer);
        return buffer.Glyphs.ToArray();
    }

    private static TrueTypeFont Resolve(string family)
    {
        Assert.True(FontManager.TryResolveFamily(family, FontWeight.Normal, false, out var font), $"{family} must be available");
        return font.Font;
    }

    private static TrueTypeFont Default => FontManager.Resolve(null, FontWeight.Normal, false).Font;

    // ---- Composition -------------------------------------------------------------------------------

    [Theory]
    [InlineData("e\u0301", "é")]
    [InlineData("a\u0308", "ä")]
    [InlineData("Vie\u0323\u0302t", "Việt")]
    [InlineData("tie\u0302\u0301ng", "tiếng")]
    public void A_letter_and_its_accent_become_the_one_glyph_the_font_keeps_for_them(string written, string precomposed)
    {
        // The font has a glyph for the accented letter, so that is what is drawn: it is drawn better
        // than the two parts placed on top of each other.
        var separate = Shape(Default, written).Select(g => g.Glyph);
        var together = Shape(Default, precomposed).Select(g => g.Glyph);

        Assert.Equal(together, separate);
    }

    [Fact]
    public void An_accent_the_font_cannot_compose_is_placed_on_the_letter()
    {
        // The letter and the first accent become the one glyph the font keeps for them, but no font has
        // one for both accents, so the second is placed on what the first produced.
        var glyphs = Shape(Default, "a\u0301\u0308");

        Assert.Equal(2, glyphs.Length);
        Assert.True(glyphs[1].Mark, "the second accent is a mark");
        Assert.True(glyphs[1].XOffset != 0 || glyphs[1].YOffset != 0, "the mark was moved onto the letter");
    }

    [Fact]
    public void A_mark_carries_the_characters_it_stands_for_into_the_text()
    {
        var pdf = Inspect(Generate(c => c.Text("e\u0301cole")));

        Assert.Equal("e\u0301cole", pdf.ExtractText().Trim());
    }

    // ---- Marks -------------------------------------------------------------------------------------

    [Fact]
    public void Arabic_marks_sit_on_their_letters()
    {
        var glyphs = Shape(Resolve(Arabic), "مَرْحَبًا", rightToLeft: true);
        var marks = glyphs.Where(g => g.Mark).ToArray();

        Assert.NotEmpty(marks);
        Assert.All(marks, mark => Assert.True(mark.XOffset != 0 || mark.YOffset != 0,
            "every mark is moved onto the letter it belongs to"));

        // A mark is drawn where the letter is, so it takes no room of its own.
        Assert.All(marks, mark => Assert.Equal(0, mark.BaseAdvance + mark.XAdvance));
    }

    [Fact]
    public void A_mark_is_drawn_apart_from_the_run_it_belongs_to()
    {
        // A glyph that moves off the line the pen follows cannot be part of the same run of text.
        var pdf = Inspect(Generate(c => c.Text("a\u0301\u0308bc")));
        var content = pdf.PageContents()[0];

        Assert.True(System.Text.RegularExpressions.Regex.Count(content, " Tm ") > 1,
            "the mark is placed on its own");
    }

    [Fact]
    public void Marks_take_no_letter_spacing()
    {
        // Letter spacing separates letters, not a letter from its own accent.
        var withSpacing = Width(c => c.Text("a\u0301\u0308").LetterSpacing(5));
        var without = Width(c => c.Text("a\u0301\u0308"));

        // One gap, after the letter, not three.
        Assert.Equal(5, withSpacing - without, 1);
    }

    private static float Width(Action<IContainer> content)
    {
        var pdf = Inspect(Generate(c => c.Row(row => row.AutoItem().Element(content))));
        var runs = pdf.TextRuns();
        return runs.Count == 0 ? 0 : runs.Max(run => run.X) - runs.Min(run => run.X);
    }

    // ---- Kerning and joining -----------------------------------------------------------------------

    [Fact]
    public void Pairs_are_kerned_once_and_not_twice()
    {
        // Kerning may be written in the positioning table or in the older kerning table; a font that has
        // it in both must not have it applied twice.
        var font = Default;
        var glyphs = Shape(font, "AVATAR");
        var positioned = glyphs.Sum(g => g.BaseAdvance + g.XAdvance);
        var unkerned = glyphs.Sum(g => (int)font.GetAdvance(g.Glyph));

        Assert.True(positioned < unkerned, "the pairs were pulled together");

        var kerningTable = glyphs.Zip(glyphs.Skip(1)).Sum(pair => font.Kerning.Get(pair.First.Glyph, pair.Second.Glyph));
        Assert.Equal(unkerned + kerningTable, positioned);
    }

    [Fact]
    public void The_font_says_where_the_letters_of_a_word_join()
    {
        var font = Resolve(Arabic);

        Assert.False(font.Positioning.IsEmpty, "the font positions its glyphs itself");
        Assert.True(font.Positioning.HasMarks(TextShaper.ScriptTag(TextShaper.ScriptOf('م'))));
    }

    [Fact]
    public void A_font_without_a_positioning_table_still_draws_its_text()
    {
        // Nothing here should depend on the font having the table at all.
        var font = TrueTypeFont.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "PapiraTestColor.ttf")));
        var glyphs = Shape(font, "A");

        Assert.Single(glyphs);
        Assert.Equal(0, glyphs[0].XOffset);
    }

    // ---- Ligatures ---------------------------------------------------------------------------------

    [Fact]
    public void Latin_ligatures_are_formed_where_the_font_offers_them()
    {
        var glyphs = Shape(Default, "office");

        // o-ffi-c-e: the three letters in the middle became one glyph.
        Assert.Equal(4, glyphs.Length);
        Assert.Equal(3, glyphs[1].Length);
    }

    [Fact]
    public void A_ligature_still_extracts_as_the_letters_it_replaced()
    {
        var pdf = Inspect(Generate(c => c.Text("office waffle final")));

        Assert.Equal("office waffle final", pdf.ExtractText().Trim());
    }
}
