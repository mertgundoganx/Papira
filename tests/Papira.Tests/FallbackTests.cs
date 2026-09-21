using Papira.Elements;
using Papira.Fonts;
using Papira.Infrastructure;
using Papira.Rendering;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Per-character font fallback. The test font contains only 漢 and 字, which the default Lato font lacks;
/// no other test uses these characters, so registering it (process-wide) doesn't affect them.
/// </summary>
public class FallbackTests
{
    private const string TestFamily = "Papira Test Fallback";

    static FallbackTests() =>
        FontManager.RegisterFont(Path.Combine(AppContext.BaseDirectory, "Fonts", "PapiraTestFallback.ttf"));

    private static TrueTypeFont TestFont
    {
        get
        {
            Assert.True(FontManager.TryResolveFamily(TestFamily, FontWeight.Normal, false, out var font));
            return font.Font;
        }
    }

    [Fact]
    public void Missing_characters_are_taken_from_a_registered_font()
    {
        var pdf = Inspect(Generate(c => c.Text("A漢字B")));

        // Two fonts are embedded: Lato for A and B, the test font for 漢字.
        Assert.Contains("+Lato-Regular", pdf.Raw);
        Assert.Contains("+PapiraTestFallback", pdf.Raw);

        var maps = pdf.Streams().Where(s => s.Contains("begincmap")).ToList();
        Assert.Equal(2, maps.Count);
        Assert.Contains(maps, map => map.Contains("<6F22>") && map.Contains("<5B57>"));
        Assert.Contains(maps, map => map.Contains("<0041>") && map.Contains("<0042>"));
    }

    [Fact]
    public void Fallback_glyphs_get_the_fallback_font_metrics()
    {
        var element = new TextElement();
        element.Spans.Add(new TextSpan { Text = "漢字" });
        var plan = element.Measure(new Size(1000, 1000), new LayoutContext(new Canvas(new DocumentResources())) { PageNumber = 1 });

        // Each glyph of the test font is exactly one em wide: 2 × 12pt.
        Assert.Equal(24, plan.Width, 3);
        Assert.NotEqual(0, TestFont.GetGlyph('漢'));
    }

    [Fact]
    public void Style_fallbacks_come_before_global_and_registered_fonts()
    {
        var previous = FontManager.FallbackFontFamilies;
        try
        {
            FontManager.FallbackFontFamilies = ["Global One", "Second"];
            var style = TextStyle.Default.FontFamily("Primary", "Second", "First");

            var candidates = FontManager.FallbackCandidates(style).ToList();

            Assert.Equal(["Second", "First", "Global One"], candidates.Take(3));
            Assert.DoesNotContain("Primary", candidates);
            Assert.Contains(TestFamily, candidates); // registered fonts come last
            Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            FontManager.FallbackFontFamilies = previous;
        }
    }

    [Fact]
    public void Unknown_fallback_families_are_skipped()
    {
        Assert.False(FontManager.TryResolveFamily("No Such Family 42", FontWeight.Normal, false, out _));

        var pdf = Inspect(Generate(c => c.Text("漢").FontFamily("Lato", "No Such Family 42")));
        Assert.Contains("+PapiraTestFallback", pdf.Raw);
    }

    [Fact]
    public void Invisible_formatting_characters_are_not_drawn()
    {
        // Zero-width joiner, emoji variation selector and byte order mark would otherwise render as .notdef boxes.
        var pdf = Inspect(Generate(c => c.Text("A\u200DB\uFE0FC\uFEFF")));
        Assert.Equal("ABC\n", pdf.ExtractText());
    }

    [Fact]
    public void Mixed_font_text_width_is_the_sum_of_each_fonts_advances()
    {
        // "T" comes from Lato and "漢" from the fallback font, each with its own metrics.
        var element = new TextElement();
        element.Spans.Add(new TextSpan { Text = "T漢" });
        var plan = element.Measure(new Size(1000, 1000), new LayoutContext(new Canvas(new DocumentResources())) { PageNumber = 1 });

        var lato = FontManager.Resolve("Lato", FontWeight.Normal, false).Font;
        var expected = lato.GetAdvance(lato.GetGlyph('T')) * 12f / lato.UnitsPerEm + 12;
        Assert.Equal(expected, plan.Width, 3);
    }
}
