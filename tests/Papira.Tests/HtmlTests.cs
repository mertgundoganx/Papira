using System.Text.RegularExpressions;
using Papira.Html;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// The HTML subset Papira lays out. The pages these tests produce were compared with what a browser
/// prints from the same markup, and the tagged output was validated against PDF/UA-1 with veraPDF.
/// </summary>
public class HtmlTests
{
    private static PdfInspector Html(string html, Action<HtmlOptions>? options = null) =>
        Inspect(Generate(c => c.Html(html, options)));

    private static string Text(string html, Action<HtmlOptions>? options = null) =>
        Html(html, options).ExtractText().Replace("\n", string.Empty, StringComparison.Ordinal);

    // ---- the parser ----

    [Fact]
    public void Tags_attributes_and_text_are_read()
    {
        var root = HtmlParser.Parse("<div id=\"a\" class='b c' hidden><p>Merhaba</p></div>");
        var div = root.Children[0];

        Assert.Equal("div", div.Tag);
        Assert.Equal("a", div.Attribute("id"));
        Assert.Equal(["b", "c"], div.Classes);
        Assert.Equal(string.Empty, div.Attribute("hidden"));
        Assert.Equal("p", div.Children[0].Tag);
        Assert.Equal("Merhaba", div.Children[0].Children[0].Text);
    }

    [Fact]
    public void Unquoted_values_and_upper_case_names_are_understood()
    {
        var root = HtmlParser.Parse("<IMG SRC=logo.png WIDTH=120>");
        var image = root.Children[0];

        Assert.Equal("img", image.Tag);
        Assert.Equal("logo.png", image.Attribute("src"));
        Assert.Equal("120", image.Attribute("width"));
    }

    [Fact]
    public void Paragraphs_and_list_items_close_themselves()
    {
        var root = HtmlParser.Parse("<p>bir<p>iki<ul><li>a<li>b</ul>");

        Assert.Equal(["p", "p", "ul"], root.Children.Select(c => c.Tag));
        Assert.Equal(2, root.Children[2].Children.Count(c => c.Tag == "li"));
    }

    [Fact]
    public void A_close_tag_that_matches_nothing_is_dropped()
    {
        var root = HtmlParser.Parse("<div>bir</span>iki</div></p>");

        Assert.Single(root.Children);
        Assert.Equal("biriki", string.Concat(root.Children[0].Children.Select(c => c.Text)));
    }

    [Theory]
    [InlineData("&amp;&lt;&gt;", "&<>")]
    [InlineData("&#65;&#x42;", "AB")]
    [InlineData("bir&nbsp;iki", "bir iki")]
    [InlineData("&mdash;&hellip;", "—…")]
    [InlineData("100 & 200", "100 & 200")]
    [InlineData("&notanentity;", "&notanentity;")]
    public void Character_references_are_resolved(string html, string expected) =>
        Assert.Equal(expected, HtmlParser.Decode(html));

    [Fact]
    public void Comments_declarations_and_scripts_say_nothing()
    {
        var text = Text("<!doctype html><!-- gizli --><script>var x = 1 < 2;</script><p>Görünür</p>");

        Assert.Equal("Görünür", text);
    }

    [Fact]
    public void A_stray_angle_bracket_is_text()
    {
        Assert.Equal("5 < 6", Text("<p>5 < 6</p>"));
    }

    [Fact]
    public void Markup_that_is_nested_without_end_is_refused()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 200));

        var exception = Assert.Throws<InvalidDataException>(() => HtmlParser.Parse(html));
        Assert.Contains("nested", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("<html><body></body></html>")]
    [InlineData("<p></p><div></div>")]
    public void Markup_with_nothing_in_it_draws_nothing(string html)
    {
        var pdf = Html(html);

        Assert.Equal(1, pdf.PageCount);
        Assert.Equal(string.Empty, pdf.ExtractText().Trim());
    }

    // ---- text ----

    [Fact]
    public void Whitespace_is_collapsed_the_way_a_browser_collapses_it()
    {
        Assert.Equal("bir iki üç", Text("<p>bir   iki\n\n     üç</p>"));
    }

    [Fact]
    public void A_break_starts_a_new_line()
    {
        var runs = Html("<p>bir<br>iki</p>").TextRuns();

        Assert.Equal(["bir", "iki"], runs.Select(run => run.Text));
        Assert.True(runs[1].Y < runs[0].Y, "the second line sits below the first");
    }

    [Fact]
    public void Bold_italic_and_the_rest_style_the_words_they_hold()
    {
        var pdf = Html("<p>düz <b>kalın</b> <i>eğik</i> <u>altı çizili</u></p>");

        // Bold and italic are different fonts, so the document embeds more than one.
        Assert.True(Regex.Count(pdf.Raw, "/Type/Font/Subtype/Type0") >= 3);
        Assert.Contains(" re f", pdf.PageContents()[0]); // the line of the underline
    }

    [Fact]
    public void Headings_become_headings_and_are_larger_than_the_text()
    {
        var pdf = Html("<h1>Başlık</h1><p>Gövde</p>");
        var sizes = Regex.Matches(pdf.PageContents()[0], @"/F\d+ ([\d.]+) Tf")
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(24, sizes[0], 1);   // 2em of the 12pt Papira starts from
        Assert.Equal(12, sizes[1], 1);
    }

    [Fact]
    public void Preformatted_text_keeps_its_spaces_and_line_breaks()
    {
        var runs = Html("<pre>bir   iki\nüç</pre>").TextRuns();

        Assert.Equal(["bir   iki", "üç"], runs.Select(run => run.Text));
    }

    [Fact]
    public void Text_that_states_no_size_keeps_the_style_of_the_document_around_it()
    {
        // The markup only says "a paragraph", so the page decides what that looks like.
        var pdf = Inspect(Generate(c => c.DefaultTextStyle(s => s.FontSize(17)).Html("<p>Metin</p>")));

        Assert.Contains("17 Tf", pdf.PageContents()[0]);
    }

    [Fact]
    public void The_size_relative_sizes_are_measured_from_can_be_chosen()
    {
        var pdf = Html("<p style=\"font-size: 2em\">Büyük</p>", options => options.FontSize(10));

        Assert.Contains("20 Tf", pdf.PageContents()[0]);
    }

    // ---- styles ----

    [Fact]
    public void A_style_attribute_is_applied()
    {
        var pdf = Html("<p style=\"color: #cc0000; font-size: 20pt\">Kırmızı</p>");

        Assert.Contains("0.8 0 0 rg", pdf.PageContents()[0]);
        Assert.Contains("20 Tf", pdf.PageContents()[0]);
    }

    [Fact]
    public void Rules_of_a_style_element_are_applied_by_tag_class_and_id()
    {
        var pdf = Html("""
            <style>
              p { color: #0000ff }
              .warn { color: #ff8800 }
              #only { color: #008800 }
            </style>
            <p>mavi</p><p class="warn">turuncu</p><p id="only" class="warn">yeşil</p>
            """);

        var content = pdf.PageContents()[0];
        Assert.Contains("0 0 1 rg", content);
        Assert.Contains("1 0.533 0 rg", content);
        Assert.Contains("0 0.533 0 rg", content);
    }

    [Fact]
    public void Descendant_and_child_selectors_are_matched()
    {
        var pdf = Html("""
            <style>
              .box p { color: #ff0000 }
              div > span { color: #00ff00 }
            </style>
            <div class="box"><p>kırmızı <span>yeşil</span></p></div>
            """);

        var content = pdf.PageContents()[0];
        Assert.Contains("1 0 0 rg", content);

        // The span is not a direct child of the div, so the child selector must not match it.
        Assert.DoesNotContain("0 1 0 rg", content);
    }

    [Fact]
    public void A_style_sheet_can_be_passed_beside_the_markup()
    {
        var pdf = Html("<p>Mavi</p>", options => options.StyleSheet("p { color: #0000ff }"));

        Assert.Contains("0 0 1 rg", pdf.PageContents()[0]);
    }

    [Fact]
    public void The_more_specific_rule_wins()
    {
        var pdf = Html("""
            <style>
              p.a { color: #ff0000 }
              p { color: #0000ff }
            </style>
            <p class="a">kırmızı</p>
            """);

        Assert.Contains("1 0 0 rg", pdf.PageContents()[0]);
        Assert.DoesNotContain("0 0 1 rg", pdf.PageContents()[0]);
    }

    [Fact]
    public void Backgrounds_borders_and_padding_are_drawn()
    {
        var pdf = Html("<div style=\"background: #eeeeee; border: 2pt solid #333333; padding: 10pt\">Kutu</div>");
        var content = pdf.PageContents()[0];

        Assert.Contains("0.933 0.933 0.933 rg", content);
        Assert.Contains("0.2 0.2 0.2 rg", content);
        Assert.Contains(" re f", content);
    }

    [Fact]
    public void Hidden_elements_say_nothing()
    {
        Assert.Equal("Görünür", Text("<p>Görünür</p><p style=\"display:none\">Gizli</p><p hidden>Gizli</p>"));
    }

    [Fact]
    public void Text_is_aligned_as_the_style_says()
    {
        var left = Html("<p>Metin</p>").TextRuns()[0].X;
        var right = Html("<p style=\"text-align: right\">Metin</p>").TextRuns()[0].X;
        var centre = Html("<p style=\"text-align: center\">Metin</p>").TextRuns()[0].X;

        Assert.True(right > centre && centre > left, $"left {left}, centre {centre}, right {right}");
    }

    [Fact]
    public void A_page_break_moves_what_follows_to_the_next_page()
    {
        var pdf = Html("<p>bir</p><div style=\"page-break-before: always\"><p>iki</p></div>");

        Assert.Equal(2, pdf.PageCount);
    }

    [Fact]
    public void A_long_document_runs_over_as_many_pages_as_it_needs()
    {
        var paragraphs = string.Concat(Enumerable.Repeat(
            "<p>Papira belgeleri sayfa sayfa yerleştirir ve metin sığmadığında bir sonraki sayfaya taşar. " +
            "Bu paragraf bunu göstermek için yeterince uzundur.</p>", 40));

        var pdf = Html(paragraphs);

        Assert.True(pdf.PageCount >= 3, $"expected several pages, got {pdf.PageCount}");
    }

    // ---- links ----

    [Fact]
    public void A_link_in_the_middle_of_a_paragraph_is_clickable()
    {
        var pdf = Html("<p>Bir <a href=\"https://example.com\">bağlantı</a> var.</p>");

        Assert.Contains("/Subtype/Link", pdf.Raw);
        Assert.Contains("/URI(https://example.com)", pdf.Raw);

        // The clickable area covers the linked words only, not the whole paragraph.
        var rect = Regex.Match(pdf.Raw, @"/Subtype/Link/F 4/Border\[0 0 0\]/Rect\[(?<l>[\d.]+) [\d.]+ (?<r>[\d.]+) ");
        var width = float.Parse(rect.Groups["r"].Value, System.Globalization.CultureInfo.InvariantCulture) -
            float.Parse(rect.Groups["l"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(width, 20, 70);
    }

    [Fact]
    public void A_link_to_an_id_jumps_inside_the_document()
    {
        var pdf = Html("<p><a href=\"#son\">sona git</a></p><h2 id=\"son\">Son</h2>");

        Assert.Contains("/Dest[", pdf.Raw);
        Assert.DoesNotContain("/URI", pdf.Raw);
    }

    [Fact]
    public void A_link_around_a_picture_is_clickable_as_a_whole()
    {
        var pdf = Html($"<a href=\"https://example.com\"><img src=\"{DataUri()}\" alt=\"kare\"></a>");

        Assert.Contains("/Subtype/Link", pdf.Raw);
        Assert.Contains("/Subtype/Image", pdf.Raw);
    }

    // ---- lists ----

    [Fact]
    public void A_bulleted_list_gets_bullets_and_a_numbered_one_numbers()
    {
        Assert.Contains("•", Text("<ul><li>bir</li><li>iki</li></ul>"));
        Assert.Contains("2.", Text("<ol><li>bir</li><li>iki</li></ol>"));
    }

    [Fact]
    public void A_numbered_list_can_start_elsewhere_and_count_with_letters()
    {
        var text = Text("<ol type=\"a\" start=\"3\"><li>üç</li></ol>");

        Assert.Contains("c.", text);
    }

    [Fact]
    public void Lists_can_hold_lists()
    {
        var text = Text("<ul><li>dış<ul><li>iç</li></ul></li></ul>");

        Assert.Contains("dış", text);
        Assert.Contains("iç", text);
    }

    // ---- tables ----

    [Fact]
    public void A_table_keeps_its_columns_its_header_and_its_spans()
    {
        var pdf = Html("""
            <table border="1">
              <tr><th rowspan="2">Dönem</th><th colspan="2">Tutar</th></tr>
              <tr><th>Gelir</th><th>Gider</th></tr>
              <tr><td>2026</td><td>130</td><td>70</td></tr>
            </table>
            """);

        var runs = pdf.TextRuns().ToDictionary(run => run.Text, run => run.X);

        // The cell that spans two rows keeps the first column to itself, so the headers of the second
        // row sit in the second and third columns, above the figures they head.
        Assert.True(runs["Gelir"] > runs["Dönem"]);
        Assert.True(runs["Gider"] > runs["Gelir"]);
        Assert.True(runs["Gelir"] > runs["2026"]);
        Assert.True(runs["130"] > runs["2026"]);
        Assert.True(runs["70"] > runs["130"]);
        Assert.True(runs["Gider"] > runs["130"]);
    }

    [Fact]
    public void A_short_row_does_not_pull_the_next_row_up()
    {
        var pdf = Html("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td></tr><tr><td>d</td><td>e</td></tr></table>");
        var runs = pdf.TextRuns().ToDictionary(run => run.Text);

        Assert.Equal(runs["a"].X, runs["c"].X, 0);
        Assert.Equal(runs["a"].X, runs["d"].X, 0);
        Assert.Equal(runs["b"].X, runs["e"].X, 0);
        Assert.True(runs["d"].Y < runs["c"].Y);
    }

    [Fact]
    public void The_widths_of_the_first_row_become_the_columns()
    {
        var pdf = Html("<table><tr><td width=\"300\">geniş</td><td>dar</td></tr></table>");
        var runs = pdf.TextRuns().ToDictionary(run => run.Text);

        // 300 pixels are 225 points, measured from the left margin, plus the padding of the cell.
        Assert.Equal(40 + 225 + 2, runs["dar"].X, 3);
    }

    [Fact]
    public void A_row_can_be_styled_and_its_cells_inherit_it()
    {
        var pdf = Html("""
            <style>tr.total { background: #ff0000; font-weight: bold }</style>
            <table><tr class="total"><td>Toplam</td></tr></table>
            """);

        Assert.Contains("1 0 0 rg", pdf.PageContents()[0]);
        Assert.Contains("Lato-Bold", pdf.Raw);
    }

    // ---- pictures ----

    [Fact]
    public void A_picture_given_as_a_data_uri_is_drawn()
    {
        var pdf = Html($"<img src=\"{DataUri()}\" alt=\"kare\" width=\"60\">");

        Assert.Contains("/Subtype/Image", pdf.Raw);
    }

    [Fact]
    public void A_picture_is_looked_for_in_the_folder_that_was_given()
    {
        var pdf = Html("<img src=\"circle-rgba.png\" width=\"40\">",
            options => options.BaseDirectory(Path.Combine(AppContext.BaseDirectory, "Assets")));

        Assert.Contains("/Subtype/Image", pdf.Raw);
    }

    [Fact]
    public void A_picture_that_cannot_be_read_says_what_to_do()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Generate(c => c.Html("<img src=\"https://example.com/logo.png\">")));

        Assert.Contains("AllowRemoteImages", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_can_be_handed_over_ready_made()
    {
        var pdf = Html("<img src=\"logo\" width=\"50\" alt=\"logo\">",
            options => options.Resource("logo", Image.FromFile(Asset("circle-rgba.png"))));

        Assert.Contains("/Subtype/Image", pdf.Raw);
    }

    [Fact]
    public void A_vector_drawing_is_drawn_as_vectors()
    {
        var pdf = Html("<img src=\"drawing.svg\" width=\"40\">", options => options
            .Resource("drawing.svg", SvgImage.FromString("<svg viewBox='0 0 10 10'><rect width='10' height='10' fill='red'/></svg>")));

        Assert.Contains("1 0 0 rg", pdf.PageContents()[0]);
        Assert.DoesNotContain("/Subtype/Image", pdf.Raw);
    }

    // ---- tagged output ----

    [Fact]
    public void Tagged_markup_carries_its_meaning_into_the_structure()
    {
        var pdf = Inspect(Document.Create(document => document.Page(page =>
            {
                page.Size(PageSizes.A4).Margin(40);
                page.Content().Html("""
                    <h1>Başlık</h1>
                    <p>Bir <a href="https://example.com">bağlantı</a>.</p>
                    <ul><li>madde</li></ul>
                    <table><tr><th>Ay</th></tr><tr><td>Eylül</td></tr></table>
                    """);
            }))
            .WithMetadata(new DocumentMetadata { Title = "HTML", Language = "tr-TR" })
            .WithSettings(new DocumentSettings { Tagged = true })
            .GeneratePdf());

        foreach (var role in (string[])["/S/H1", "/S/P", "/S/Link", "/S/L", "/S/LI", "/S/Table", "/S/TR", "/S/TH", "/S/TD"])
            Assert.Contains(role, pdf.Raw);
    }

    /// <summary>A one pixel PNG, small enough to write into the markup itself.</summary>
    private static string DataUri()
    {
        var png = Convert.ToBase64String(File.ReadAllBytes(Asset("circle-rgba.png")));
        return "data:image/png;base64," + png;
    }
}
