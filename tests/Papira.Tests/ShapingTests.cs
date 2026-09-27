using Papira.Fonts;
using Papira.Text;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Cursive shaping of Arabic script. The expected output was compared with the text renderer of macOS
/// (CoreText) drawing the same font: letters join the same way and the lines come out the same width.
/// The font is a subset of Noto Sans Arabic (SIL Open Font License), kept next to its licence.
/// </summary>
public class ShapingTests
{
    private const string Family = "Noto Sans Arabic";

    // مرحبا — "hello": meem, reh, hah, beh, alef.
    private const string Hello = "مرحبا";

    // لا — lam followed by alef, which Arabic fonts draw with glyphs of their own.
    private const string LamAlef = "لا";

    // بِّ — a letter with a shadda and a kasra on it; the two marks become one glyph.
    private const string MarkLigature = "بِّ";

    static ShapingTests() =>
        FontManager.RegisterFont(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansArabic-subset.ttf"));

    private static TrueTypeFont Font
    {
        get
        {
            Assert.True(FontManager.TryResolveFamily(Family, FontWeight.Normal, false, out var font), $"{Family} must be registered");
            return font.Font;
        }
    }

    private static ShapedGlyph[] ShapeBuffer(string text)
    {
        var buffer = new ShapingBuffer();
        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        TextShaper.Shape(Font, codepoints, TextShaper.ScriptOf(codepoints[0]), true, buffer);
        return buffer.Glyphs.ToArray();
    }

    private static ushort[] Shape(string text) => ShapeBuffer(text).Select(shaped => shaped.Glyph).ToArray();

    private static JoiningForm[] Forms(string text)
    {
        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var forms = new JoiningForm[codepoints.Length];
        ArabicShaper.ComputeForms(codepoints, forms);
        return forms;
    }

    // ---- Joining -----------------------------------------------------------------------------------

    [Theory]
    [InlineData('ب', JoiningType.Dual)]      // beh joins on both sides
    [InlineData('ا', JoiningType.Right)]     // alef joins only to the letter before it
    [InlineData('ـ', JoiningType.Causing)]   // tatweel joins whatever is around it
    [InlineData('َ', JoiningType.Transparent)] // fatha, a mark that is skipped
    [InlineData('‍', JoiningType.Causing)]   // zero width joiner
    [InlineData('‌', JoiningType.None)]      // zero width non-joiner
    [InlineData('A', JoiningType.None)]
    internal void Joining_types_come_from_the_unicode_database(int codepoint, JoiningType expected) =>
        Assert.Equal(expected, Bidi.JoiningOf(codepoint));

    [Fact]
    public void Letters_take_the_form_their_neighbours_allow()
    {
        // م ر ح ب ا: the first letter has nothing before it, alef ends the word, and reh joins only backwards.
        Assert.Equal(
            [JoiningForm.Initial, JoiningForm.Final, JoiningForm.Initial, JoiningForm.Medial, JoiningForm.Final],
            Forms(Hello));

        Assert.Equal([JoiningForm.Isolated], Forms("ب"));
        Assert.Equal([JoiningForm.Initial, JoiningForm.Final], Forms("بب"));
    }

    [Fact]
    public void Marks_do_not_break_the_joining_of_the_letters_around_them()
    {
        // بَب: a fatha between two behs leaves them joined.
        var forms = Forms("بَب");

        Assert.Equal(JoiningForm.Initial, forms[0]);
        Assert.Equal(JoiningForm.Final, forms[2]);
    }

    [Fact]
    public void The_joiners_decide_where_letters_connect()
    {
        // A zero width non-joiner cuts the connection, a zero width joiner keeps it.
        Assert.Equal(JoiningForm.Isolated, Forms("ب‌ب")[0]);
        Assert.Equal(JoiningForm.Initial, Forms("ب‍")[0]);
    }

    [Fact]
    public void Text_is_recognised_as_needing_a_shaper()
    {
        Assert.True(ArabicShaper.NeedsShaping(Hello.EnumerateRunes().Select(r => r.Value).ToArray()));
        Assert.False(ArabicShaper.NeedsShaping("Papira".EnumerateRunes().Select(r => r.Value).ToArray()));
        Assert.False(ArabicShaper.NeedsShaping("שלום".EnumerateRunes().Select(r => r.Value).ToArray())); // Hebrew does not join
    }

    // ---- Substitution ------------------------------------------------------------------------------

    [Fact]
    public void The_font_has_the_features_a_cursive_script_needs()
    {
        var substitution = Font.Substitution;

        Assert.False(substitution.IsEmpty);
        foreach (var feature in new[] { "init", "medi", "fina", "rlig" })
            Assert.True(substitution.Has(TrueTypeFont.Tag("arab"), TrueTypeFont.Tag(feature)), $"the font should have {feature}");
    }

    [Fact]
    public void Every_letter_is_drawn_with_the_glyph_of_its_form()
    {
        // The same letter twice: the first joins forwards, the second backwards. This font draws a letter
        // as a skeleton plus its dots, so each of them takes more than one glyph.
        var alone = Shape("ب");
        var joined = Shape("بب");

        Assert.All(joined, glyph => Assert.NotEqual(0, glyph));
        Assert.Equal(2 * alone.Length, joined.Length);
        Assert.NotEqual(alone, joined[..alone.Length]);                  // not the isolated form any more
        Assert.NotEqual(joined[..alone.Length], joined[alone.Length..]); // and the two differ from each other
    }

    [Fact]
    public void Lam_and_alef_are_drawn_with_the_glyphs_the_font_keeps_for_them()
    {
        var shaped = Shape(LamAlef);

        Assert.Equal(2, shaped.Length);
        Assert.NotEqual(Font.GetGlyph('ل'), shaped[1]);
        Assert.NotEqual(Font.GetGlyph('ا'), shaped[0]);
    }

    [Fact]
    public void Several_characters_can_become_one_glyph()
    {
        // The shadda and the kasra are drawn as a single glyph that stands for both characters.
        Assert.Contains(ShapeBuffer(MarkLigature), shaped => shaped.Length == 2);
    }

    [Fact]
    public void A_ligature_still_extracts_as_the_characters_it_replaced()
    {
        var pdf = Inspect(Generate(c => c.Text(MarkLigature).FontFamily(Family)));

        // The ToUnicode map has to give both characters for the one glyph, as two UTF-16 values.
        Assert.Contains("<06510650>", pdf.Streams().First(stream => stream.Contains("beginbfchar")));
    }

    // ---- Drawing -----------------------------------------------------------------------------------

    [Fact]
    public void Arabic_text_is_drawn_from_the_right()
    {
        var pdf = Inspect(Generate(c => c.Text(Hello + " 2026").FontFamily(Family)));
        var runs = pdf.TextRuns();

        // The number is written after the words but drawn to their left.
        Assert.True(runs.Count >= 2, "expected the words and the number as separate runs");
        var number = runs.First(run => run.Text.Contains("2026"));
        var letters = runs.First(run => !run.Text.Contains("2026"));
        Assert.True(number.X < letters.X, $"the number at {number.X} should be left of the letters at {letters.X}");
    }

    [Fact]
    public void A_right_to_left_paragraph_is_aligned_to_the_right_by_default()
    {
        var right = Inspect(Generate(c => c.Text(Hello).FontFamily(Family))).TextRuns()[0];
        var left = Inspect(Generate(c => c.Text(Hello).FontFamily(Family).AlignLeft())).TextRuns()[0];

        Assert.True(right.X > left.X, $"the default should sit right ({right.X}) of an explicit AlignLeft ({left.X})");
    }

    [Fact]
    public void The_direction_can_be_forced()
    {
        // "abc" alone is left to right; asked for the other direction, the paragraph is laid out right to left.
        var pdf = Inspect(Generate(c => c.Text(t => t.Span("abc (1)").Direction(TextDirection.RightToLeft))));

        Assert.Contains("abc (1)", pdf.ExtractText());
    }

    [Fact]
    public void Brackets_in_right_to_left_text_are_drawn_mirrored()
    {
        // The opening bracket of the source text must come out as the closing one.
        var pdf = Inspect(Generate(c => c.Text("ب (ب)").FontFamily(Family)));
        var text = pdf.ExtractText();

        Assert.Contains(")", text);
        Assert.Contains("(", text);
    }

    [Fact]
    public void Latin_text_is_unaffected_by_the_shaper()
    {
        var pdf = Inspect(Generate(c => c.Text("Papira 2026")));

        Assert.Contains("Papira 2026", pdf.ExtractText());
    }
}
