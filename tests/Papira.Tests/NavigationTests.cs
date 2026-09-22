using System.Globalization;
using System.Text.RegularExpressions;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

public class NavigationTests
{
    private static List<int> PageObjectIds(PdfInspector pdf) =>
        Regex.Matches(pdf.Raw, @"(\d+) 0 obj\n<</Type/Page/").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    private static List<float[]> LinkRects(PdfInspector pdf) =>
        Regex.Matches(pdf.Raw, @"/Subtype/Link/F 4/Border\[0 0 0\]/Rect\[([^\]]+)\]")
            .Select(m => m.Groups[1].Value.Split(' ').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray())
            .ToList();

    private static string LongText => string.Concat(Enumerable.Repeat("Bağlantılı uzun metin. ", 400));

    [Fact]
    public void Hyperlink_creates_a_uri_link_annotation_over_the_content()
    {
        var pdf = Inspect(Generate(c => c.Hyperlink("https://example.com/ürün?q=a b").Text("Sipariş")));

        Assert.Contains("/Annots[", pdf.Raw);
        Assert.Contains("/S/URI/URI(https://example.com/%C3%BCr%C3%BCn?q=a%20b)", pdf.Raw);

        // A4 page with 40pt margins: the link starts at the top-left of the content area and is one line high.
        var rect = Assert.Single(LinkRects(pdf));
        Assert.Equal(40, rect[0], 2);
        Assert.Equal(841.89f - 40, rect[3], 2);
        Assert.InRange(rect[3] - rect[1], 10, 20);
    }

    [Fact]
    public void Link_split_across_pages_is_clickable_on_every_page()
    {
        var pdf = Inspect(Generate(c => c.Hyperlink("mailto:info@example.com").Text(LongText)));

        Assert.True(pdf.PageCount > 1);
        Assert.Equal(pdf.PageCount, LinkRects(pdf).Count);
        Assert.Equal(pdf.PageCount, Regex.Count(pdf.Raw, "/Annots\\["));
    }

    [Fact]
    public void Section_link_jumps_to_a_later_page()
    {
        var pdf = Inspect(Generate(c => c.Column(col =>
        {
            col.Item().SectionLink("totals").Text("Toplamlara git");
            col.Item().PageBreak();
            col.Item().Text("ara sayfa");
            col.Item().PageBreak();
            col.Item().Section("totals").Text("Toplamlar");
        })));

        var pages = PageObjectIds(pdf);
        Assert.Equal(3, pages.Count);
        Assert.Matches($@"/Subtype/Link/F 4/Border\[0 0 0\]/Rect\[[^\]]+\]/Dest\[{pages[2]} 0 R/XYZ 40 801.89 null\]", pdf.Raw);
    }

    [Fact]
    public void Links_to_unknown_sections_are_dropped()
    {
        var pdf = Inspect(Generate(c => c.SectionLink("missing").Text("nowhere")));
        Assert.DoesNotContain("/Annots", pdf.Raw);
        Assert.DoesNotContain("/Subtype/Link", pdf.Raw);
    }

    [Fact]
    public void Bookmarks_build_a_nested_outline()
    {
        var pdf = Inspect(Generate(c => c.Column(col =>
        {
            col.Item().Bookmark("Özet").Text("Özet");
            col.Item().Bookmark("Kalemler", level: 1).Text("Kalemler");
            col.Item().Bookmark("Detay", level: 5).Text("Detay"); // clamped to level 2
            col.Item().Bookmark("Ekler").Text("Ekler");
        })));

        Assert.Contains("/PageMode/UseOutlines", pdf.Raw);
        Assert.Contains("/Type/Outlines", pdf.Raw);
        Assert.Equal(4, Regex.Count(pdf.Raw, "/Title<FEFF"));
        Assert.Contains("/Title<FEFF00D6007A00650074>", pdf.Raw); // "Özet"

        // Root: 4 entries in total, 2 top-level ("Özet" with 2 descendants, "Ekler").
        Assert.Matches(@"<</Type/Outlines/First \d+ 0 R/Last \d+ 0 R/Count 4>>", pdf.Raw);
        Assert.Matches(@"/Title<FEFF00D6007A00650074>/Parent \d+ 0 R/Next \d+ 0 R/Dest\[[^\]]+\]/First \d+ 0 R/Last \d+ 0 R/Count 2>>", pdf.Raw);
    }

    [Fact]
    public void Bookmarks_are_not_duplicated_by_the_second_layout_pass()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Footer().Text(t => t.TotalPages());
            p.Content().Column(col =>
            {
                col.Item().Bookmark("Bir").Text("1");
                col.Item().Bookmark("İki").Text("2");
            });
        })).GeneratePdf());

        Assert.Equal(2, Regex.Count(pdf.Raw, "/Title<FEFF"));
    }

    [Fact]
    public void Documents_without_bookmarks_have_no_outline()
    {
        var pdf = Inspect(Generate(c => c.Text("x")));
        Assert.DoesNotContain("/Outlines", pdf.Raw);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    public void Invalid_or_unsafe_links_are_rejected(string url)
    {
        Assert.Throws<ArgumentException>(() => Generate(c => c.Hyperlink(url).Text("x")));
    }

    [Fact]
    public void Uri_special_characters_are_escaped()
    {
        var pdf = Inspect(Generate(c => c.Hyperlink("https://example.com/a(b)").Text("x")));
        Assert.Contains(@"/URI(https://example.com/a\(b\))", pdf.Raw);
    }
}
