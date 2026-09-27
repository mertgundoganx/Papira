using System.Globalization;
using Papira.Text;

namespace Papira.Tests;

/// <summary>
/// The Unicode bidirectional algorithm. The cases below are taken from the conformance files of the Unicode
/// Character Database; the whole of both files — 861,948 cases — is checked by
/// <see cref="The_conformance_files_of_the_unicode_database_pass"/> when the database is available.
/// </summary>
public class BidiTests
{
    private const string Alef = "א";       // א, Hebrew, class R
    private const string Beh = "ب";        // ب, Arabic, class AL
    private const string ArabicOne = "١";  // ١, an Arabic-Indic digit, class AN

    private static (byte Paragraph, byte[] Levels, int[] Order) Run(string text, TextDirection direction = TextDirection.Auto)
    {
        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var levels = new byte[codepoints.Length];
        var paragraph = Bidi.Resolve(codepoints, direction, levels);

        var visual = new int[codepoints.Length];
        var count = Bidi.Reorder(codepoints, levels, paragraph, visual);
        return (paragraph, levels, visual[..count]);
    }

    private static string Visual(string text, TextDirection direction = TextDirection.Auto)
    {
        var runes = text.EnumerateRunes().ToArray();
        var (_, _, order) = Run(text, direction);
        return string.Concat(order.Select(index => runes[index].ToString()));
    }

    [Theory]
    [InlineData('A', BidiClass.L)]
    [InlineData('5', BidiClass.EN)]
    [InlineData(' ', BidiClass.WS)]
    [InlineData('(', BidiClass.ON)]
    [InlineData(',', BidiClass.CS)]
    [InlineData('א', BidiClass.R)]
    [InlineData('ب', BidiClass.AL)]
    [InlineData('١', BidiClass.AN)]
    [InlineData('̀', BidiClass.NSM)]
    [InlineData('‫', BidiClass.RLE)]
    [InlineData('⁧', BidiClass.RLI)]
    internal void Characters_have_the_class_the_database_gives_them(int codepoint, BidiClass expected) =>
        Assert.Equal(expected, Bidi.ClassOf(codepoint));

    [Theory]
    [InlineData('(', ')')]
    [InlineData(']', '[')]
    [InlineData('«', '»')]  // « »
    [InlineData('a', 'a')]            // not mirrored
    public void Mirrored_characters_swap_sides(int codepoint, int expected) =>
        Assert.Equal(expected, Bidi.Mirror(codepoint));

    [Fact]
    public void The_shortcut_for_plain_text_covers_every_character_the_algorithm_could_move()
    {
        // Papira skips the algorithm for text that holds none of these characters, so the ranges the
        // shortcut tests must cover every character with a class that reorders.
        for (var codepoint = 0; codepoint <= 0x10FFFF; codepoint++)
        {
            var moves = Bidi.ClassOf(codepoint) is BidiClass.R or BidiClass.AL or BidiClass.AN
                or BidiClass.LRE or BidiClass.LRO or BidiClass.RLE or BidiClass.RLO or BidiClass.PDF
                or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

            if (moves)
                Assert.False(Bidi.IsPlainLeftToRight(codepoint), $"U+{codepoint:X4} reorders but is treated as plain");
        }
    }

    [Fact]
    public void Left_to_right_text_keeps_its_order()
    {
        var (paragraph, levels, order) = Run("Papira 2026");

        Assert.Equal(0, paragraph);
        Assert.All(levels, level => Assert.Equal(0, level));
        Assert.Equal(Enumerable.Range(0, 11), order);
    }

    [Fact]
    public void A_paragraph_takes_its_direction_from_the_first_strong_character()
    {
        Assert.Equal(1, Run(Alef + " abc").Paragraph);
        Assert.Equal(0, Run("abc " + Alef).Paragraph);
        Assert.Equal(1, Run("123 " + Beh).Paragraph);     // numbers are not strong
        Assert.Equal(1, Run("abc", TextDirection.RightToLeft).Paragraph);
    }

    [Fact]
    public void Right_to_left_text_is_drawn_from_the_other_end()
    {
        Assert.Equal("גבא", Visual("אבג"));
    }

    [Fact]
    public void Latin_inside_right_to_left_text_keeps_reading_left_to_right()
    {
        // The Hebrew runs swap places, the Latin word stays as it is written.
        Assert.Equal("בא PDF דג", Visual("גד PDF אב"));
    }

    [Fact]
    public void Numbers_in_arabic_text_read_left_to_right()
    {
        var text = Beh + " 2026";
        var (paragraph, levels, _) = Run(text);

        Assert.Equal(1, paragraph);
        Assert.Equal(1, levels[0]);                       // the letter
        Assert.All(levels[2..], level => Assert.Equal(2, level));  // the digits, one level deeper
        Assert.Equal("2026 " + Beh, Visual(text));
    }

    [Fact]
    public void European_digits_after_an_arabic_letter_become_arabic_numbers()
    {
        // W2: the digits take the direction of the Arabic number they now are.
        var (_, levels, _) = Run(Beh + ArabicOne);

        Assert.Equal(1, levels[0]);
        Assert.Equal(2, levels[1]);
    }

    [Fact]
    public void Brackets_take_the_direction_of_what_they_enclose()
    {
        // N0: a pair whose contents run the other way keeps the direction of the text around it, so the
        // brackets of "(א)" stay put inside a Latin sentence and only the Hebrew letter turns around.
        Assert.Equal("a (" + Alef + ") b", Visual("a (" + Alef + ") b"));

        var latin = Run("a (" + Alef + ") b").Levels;
        Assert.Equal(0, latin[2]);
        Assert.Equal(1, latin[3]);
        Assert.Equal(0, latin[4]);

        // In a right to left paragraph the pair belongs to the Hebrew around it.
        var hebrew = Run(Alef + " (" + Alef + ") " + Alef).Levels;
        Assert.All(hebrew, level => Assert.Equal(1, level));
    }

    [Fact]
    public void An_explicit_override_turns_even_latin_text_around()
    {
        // U+202E starts a right to left override and U+202C ends it.
        Assert.Equal("cba", Visual("‮abc‬"));
    }

    [Fact]
    public void An_isolate_keeps_its_contents_out_of_the_surrounding_text()
    {
        // U+2067 opens a right to left isolate, U+2069 closes it.
        // The invisible isolate characters stay where they are; only what is between them turns around.
        var isolated = Visual("a ⁧" + Alef + " 12⁩ b");

        Assert.Equal("a ⁧12 " + Alef + "⁩ b", isolated);
    }

    [Fact]
    public void Trailing_whitespace_stays_at_the_end_of_the_line()
    {
        // L1: the spaces at the end of a right to left line return to the paragraph direction.
        var text = Alef + "  ";
        var (paragraph, levels, _) = Run(text);

        Assert.Equal(1, paragraph);
        Assert.Equal([1, 1, 1], levels);

        var codepoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var visual = new int[codepoints.Length];
        Bidi.Reorder(codepoints, levels, paragraph, visual);
        Assert.Equal([1, 1, 1], levels);   // the spaces were already at the paragraph level
    }

    [Theory]
    // Cases from BidiCharacterTest.txt: text, direction, expected paragraph level, expected visual order.
    [InlineData("0061 0028 05D0 005B 05D1 005D 0021 0029 0062", 0, 0, "0 1 5 4 3 2 6 7 8")]
    [InlineData("0061 0028 05D0 005B 05D1 005D 0021 0029 0062", 1, 1, "8 7 6 5 4 3 2 1 0")]
    [InlineData("0061 0028 05D0 0029", 0, 0, "0 1 2 3")]
    [InlineData("05D0 0028 05D1 0029 0331", 0, 0, "4 3 2 1 0")]
    [InlineData("05D0 0028 0332 05D1 0029 0333", 0, 0, "5 4 3 2 1 0")]
    [InlineData("0627 0661 0662", 2, 1, "1 2 0")]
    [InlineData("05D0 0031 0032 0033 05D1", 2, 1, "4 1 2 3 0")]
    [InlineData("0061 202B 0062 05D0 202C 0063", 2, 0, "0 3 2 5")]
    public void Cases_from_the_conformance_file(string codepoints, int direction, int expectedParagraph, string expectedOrder)
    {
        var text = codepoints.Split(' ').Select(value => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        var levels = new byte[text.Length];
        var paragraph = Bidi.Resolve(text, direction switch { 0 => TextDirection.LeftToRight, 1 => TextDirection.RightToLeft, _ => TextDirection.Auto }, levels);

        var visual = new int[text.Length];
        var count = Bidi.Reorder(text, levels, paragraph, visual);

        Assert.Equal(expectedParagraph, paragraph);
        Assert.Equal(expectedOrder, string.Join(' ', visual[..count]));
    }

    [Fact]
    public void The_conformance_files_of_the_unicode_database_pass()
    {
        // Set PAPIRA_UCD to a directory holding BidiCharacterTest.txt and BidiTest.txt (from
        // https://www.unicode.org/Public/UCD/latest/ucd/) to run the whole conformance suite.
        var directory = Environment.GetEnvironmentVariable("PAPIRA_UCD");
        if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, "BidiCharacterTest.txt")))
            return;

        var failures = 0;
        var cases = 0;
        foreach (var raw in File.ReadLines(Path.Combine(directory, "BidiCharacterTest.txt")))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;

            var fields = line.Split(';');
            var text = fields[0].Split(' ').Select(value => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            var direction = fields[1] switch { "0" => TextDirection.LeftToRight, "1" => TextDirection.RightToLeft, _ => TextDirection.Auto };
            var expectedLevels = fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var levels = new byte[text.Length];
            var paragraph = Bidi.Resolve(text, direction, levels);
            var visual = new int[text.Length];
            var count = Bidi.Reorder(text, levels, paragraph, visual);

            var ok = paragraph == byte.Parse(fields[2], CultureInfo.InvariantCulture);
            for (var i = 0; i < expectedLevels.Length && ok; i++)
            {
                if (expectedLevels[i] != "x")
                    ok = byte.Parse(expectedLevels[i], CultureInfo.InvariantCulture) == levels[i];
            }

            var order = visual[..count].Where(index => expectedLevels[index] != "x");
            ok &= string.Join(' ', order) == string.Join(' ', fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries));

            cases++;
            if (!ok)
                failures++;
        }

        Assert.Equal(0, failures);
        Assert.True(cases > 90_000, $"expected the whole file, read {cases} cases");
    }
}
