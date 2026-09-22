using Papira.Elements;
using Papira.Fonts;
using Papira.Infrastructure;
using Papira.Rendering;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

public class KerningTests
{
    private static TrueTypeFont Font(FontWeight weight) => FontManager.Resolve("Lato", weight, false).Font;

    // Reference values computed with fontTools from the bundled Lato fonts (GPOS 'kern' feature).
    [Theory]
    [InlineData(FontWeight.Normal, "AV", -108)]
    [InlineData(FontWeight.Normal, "To", -244)]
    [InlineData(FontWeight.Normal, "Yo", -219)]
    [InlineData(FontWeight.Normal, "LT", -188)]
    [InlineData(FontWeight.Normal, "Av", -80)]
    [InlineData(FontWeight.Normal, "ov", -28)]
    [InlineData(FontWeight.Normal, "P.", -201)]
    [InlineData(FontWeight.Normal, "Şa", -14)]
    [InlineData(FontWeight.Normal, "ab", 0)]
    [InlineData(FontWeight.Normal, "ğü", 0)]
    [InlineData(FontWeight.Normal, "11", 0)]
    [InlineData(FontWeight.Bold, "AV", -115)]
    [InlineData(FontWeight.Bold, "To", -241)]
    [InlineData(FontWeight.Bold, "F,", -168)]
    public void Reads_gpos_pair_kerning(FontWeight weight, string pair, int expected)
    {
        var font = Font(weight);
        Assert.Equal(expected, font.Kerning.Get(font.GetGlyph(pair[0]), font.GetGlyph(pair[1])));
    }

    [Fact]
    public void Kerned_text_is_narrower_than_the_sum_of_advances()
    {
        var font = Font(FontWeight.Normal);
        var unkerned = "AVATAR To".Sum(c => font.GetAdvance(font.GetGlyph(c))) * 12f / font.UnitsPerEm;

        var element = new TextElement();
        element.Spans.Add(new TextSpan { Text = "AVATAR To" });
        var plan = element.Measure(new Size(1000, 1000), new LayoutContext(new Canvas(new DocumentResources())) { PageNumber = 1 });

        Assert.True(plan.Width < unkerned - 1, $"kerned width {plan.Width} should be clearly below {unkerned}");
    }

    [Fact]
    public void Kerned_runs_are_written_with_tj_adjustments()
    {
        var pdf = Inspect(Generate(c => c.Text("AVATAR")));
        var page = pdf.PageContents()[0];

        // "AV" kerns by -108 units on a 2000 unit em => +54 thousandths in the TJ array.
        Assert.Contains("] TJ", page);
        Assert.Contains(">54<", page);
        Assert.Equal("AVATAR\n", pdf.ExtractText());
    }

    [Fact]
    public void Text_without_kerning_pairs_uses_plain_tj()
    {
        var pdf = Inspect(Generate(c => c.Text("abc")));
        Assert.DoesNotContain("] TJ", pdf.PageContents()[0]);
    }

    [Fact]
    public void Kerning_does_not_cross_span_boundaries()
    {
        var pdf = Inspect(Generate(c => c.Text(t =>
        {
            t.Span("A");
            t.Span("V").Bold();
        })));

        Assert.DoesNotContain("] TJ", pdf.PageContents()[0]);
    }

    [Fact]
    public void Right_aligned_kerned_text_ends_at_the_right_edge()
    {
        // Line width must not include the kerning of the last glyph with a following glyph.
        var element = new TextElement { Alignment = TextAlignment.Right };
        element.Spans.Add(new TextSpan { Text = "To To" });
        var context = new LayoutContext(new Canvas(new DocumentResources())) { PageNumber = 1 };
        var single = element.Measure(new Size(1000, 1000), context);

        var font = Font(FontWeight.Normal);
        var glyphs = "To To".Select(c => font.GetGlyph(c)).ToArray();
        var units = glyphs.Sum(g => font.GetAdvance(g));
        for (var i = 0; i + 1 < glyphs.Length; i++)
            units += font.Kerning.Get(glyphs[i], glyphs[i + 1]);

        Assert.Equal(units * 12f / font.UnitsPerEm, single.Width, 2);
    }
}
