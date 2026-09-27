using System.Text.RegularExpressions;
using Papira.Fonts;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// OpenType fonts with Compact Font Format outlines (.otf). The subsets Papira writes were read back with
/// fontTools for 127 faces of this machine and 6,888 glyphs: every outline came out identical to the
/// original. The font here is a subset of Source Sans 3 (SIL Open Font License), kept next to its licence.
/// </summary>
public class OpenTypeFontTests
{
    private const string Family = "Source Sans 3";

    static OpenTypeFontTests() =>
        FontManager.RegisterFont(Path.Combine(AppContext.BaseDirectory, "Fonts", "SourceSans3-subset.otf"));

    private static TrueTypeFont Font
    {
        get
        {
            Assert.True(FontManager.TryResolveFamily(Family, FontWeight.Normal, false, out var font), $"{Family} must be registered");
            return font.Font;
        }
    }

    [Fact]
    public void A_font_with_compact_outlines_is_recognised_as_one()
    {
        var font = Font;

        Assert.True(font.IsCff);
        Assert.True(font.TryTable("CFF ", out _, out var length));
        Assert.True(length > 0);
        Assert.False(font.TryTable("glyf", out _, out _));
        Assert.NotEqual(0, font.GetGlyph('P'));
    }

    [Fact]
    public void The_document_embeds_it_as_a_cid_keyed_font()
    {
        var pdf = Inspect(Generate(c => c.Text("Papira PDF").FontFamily(Family)));

        Assert.Contains("/Subtype/CIDFontType0", pdf.Raw);
        Assert.Contains("/FontFile3 ", pdf.Raw);
        Assert.Contains("/Subtype/CIDFontType0C", pdf.Raw);

        // The glyph identifiers are the glyph numbers, so no mapping table is needed.
        Assert.DoesNotContain("/CIDToGIDMap", pdf.Raw);
        Assert.Contains("/Encoding/Identity-H", pdf.Raw);
    }

    [Fact]
    public void The_text_stays_extractable()
    {
        var pdf = Inspect(Generate(c => c.Text("Papira 2026 — ĞÜŞİÖÇ").FontFamily(Family)));

        Assert.Contains("Papira 2026 — ĞÜŞİÖÇ", pdf.ExtractText());
    }

    [Fact]
    public void Only_the_glyphs_the_document_uses_are_embedded()
    {
        var subset = FontSubsetter.Subset(Font, [Font.GetGlyph('P'), Font.GetGlyph('a')], "ABCDEF+Test");

        // A CFF font begins with its version and the size of its header.
        Assert.Equal(1, subset[0]);
        Assert.True(subset.Length < 4000, $"a two-glyph subset should be small, got {subset.Length} bytes");
        Assert.Contains("ABCDEF+Test", System.Text.Encoding.ASCII.GetString(subset));
    }

    [Fact]
    public void The_subset_grows_with_the_text_but_stays_far_below_the_whole_font()
    {
        var few = FontSubsetter.Subset(Font, [Font.GetGlyph('P')], "ABCDEF+Test").Length;

        var many = new List<ushort>();
        for (var c = 'a'; c <= 'z'; c++)
            many.Add(Font.GetGlyph(c));

        var more = FontSubsetter.Subset(Font, many, "ABCDEF+Test").Length;

        Assert.True(more > few, "more glyphs should take more room");
        Assert.True(more < Font.Data.Length / 2, $"the subset ({more}) should be far below the font ({Font.Data.Length})");
    }

    [Fact]
    public void Widths_come_from_the_font_metrics()
    {
        var font = Font;
        var narrow = font.GetAdvance(font.GetGlyph('i'));
        var wide = font.GetAdvance(font.GetGlyph('W'));

        Assert.True(wide > narrow * 2, $"W ({wide}) should be much wider than i ({narrow})");

        // Those widths are what the document writes for the glyphs it uses.
        var pdf = Inspect(Generate(c => c.Text("iW").FontFamily(Family)));
        var widths = Regex.Match(pdf.Raw, @"/W\[(.+?)\]>>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains(Math.Round(wide * 1000.0 / font.UnitsPerEm).ToString(System.Globalization.CultureInfo.InvariantCulture), widths);
    }

    [Fact]
    public void A_compact_font_is_drawn_like_any_other()
    {
        var cff = Inspect(Generate(c => c.Text("Papira").FontFamily(Family))).PageContents()[0];
        var trueType = Inspect(Generate(c => c.Text("Papira"))).PageContents()[0];

        // The same operators, with one glyph per character in both: six characters, two bytes each.
        Assert.Contains("Tf", cff);
        Assert.Equal(24, Regex.Matches(cff, "<([0-9A-F]+)>").Sum(match => match.Groups[1].Value.Length));
        Assert.Equal(Regex.Count(trueType, "T[jJ]"), Regex.Count(cff, "T[jJ]"));
    }

    [Fact]
    public void Kerning_and_shaping_work_the_same_way()
    {
        // Source Sans has a kerning pair for "AV"; the adjustment shows up as a TJ array.
        var pdf = Inspect(Generate(c => c.Text("AV").FontFamily(Family)));

        Assert.Contains("TJ", pdf.PageContents()[0]);
    }
}
