using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// The layouts a template is written in: flexible boxes, grids, elements placed against their container,
/// sizes given as a share of the space around them, and the pictures and boxes that stand in a line of
/// text. Every page these tests produce was compared with what Chrome prints from the same markup; the
/// numbers here are the ones both engines agree on.
/// </summary>
public class HtmlLayoutTests
{
    /// <summary>A4 with a 40 pt margin, which is what the test documents use.</summary>
    private const float Content = 595.28f - 80;

    private static PdfInspector Html(string html, Action<HtmlOptions>? options = null) =>
        Inspect(Generate(c => c.Html(html, settings =>
        {
            settings.BaseDirectory(Path.Combine(AppContext.BaseDirectory, "Assets"));
            options?.Invoke(settings);
        })));

    /// <summary>The text drawn on the first page, with the place each run starts at.</summary>
    private static List<(float X, float Y, string Text)> Runs(PdfInspector pdf) =>
        [.. pdf.TextRuns().Where(run => run.Page == 0).Select(run => (run.X, run.Y, run.Text))];

    private static (float X, float Y) Place(PdfInspector pdf, string text)
    {
        var run = Runs(pdf).Find(r => r.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(run.Text != null, $"'{text}' was not drawn. Drawn: {string.Join(" | ", Runs(pdf).Select(r => r.Text))}");
        return (run.X, run.Y);
    }

    /// <summary>Every picture drawn on the first page, as the box it was drawn in.</summary>
    private static List<(float Width, float Height, float X, float Y)> Pictures(PdfInspector pdf)
    {
        var boxes = new List<(float, float, float, float)>();
        foreach (Match match in Regex.Matches(
            pdf.PageContents()[0],
            @"q (?<a>-?[\d.]+) (?<b>-?[\d.]+) (?<c>-?[\d.]+) (?<d>-?[\d.]+) (?<e>-?[\d.]+) (?<f>-?[\d.]+) cm /\w+ Do Q"))
        {
            float Value(string name) => float.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
            boxes.Add((Value("a"), Math.Abs(Value("d")), Value("e"), Value("f")));
        }

        return boxes;
    }

    // ---- flexible boxes ----

    [Fact]
    public void The_items_of_a_flexible_box_stand_side_by_side()
    {
        var pdf = Html("<div style='display:flex'><div>bir</div><div>iki</div><div>uc</div></div>");

        var (_, first) = Place(pdf, "bir");
        var (second, _) = Place(pdf, "iki");
        var (third, _) = Place(pdf, "uc");

        Assert.Equal(first, Place(pdf, "iki").Y, 1);
        Assert.Equal(first, Place(pdf, "uc").Y, 1);
        Assert.True(second < third, "the items follow one another from left to right");
    }

    [Fact]
    public void Space_between_pushes_the_items_to_the_edges()
    {
        var pdf = Html("<div style='display:flex;justify-content:space-between'><div>bir</div><div>son</div></div>");

        var (left, _) = Place(pdf, "bir");
        var (right, _) = Place(pdf, "son");

        Assert.Equal(40, left, 1);
        Assert.True(right > 40 + (Content * 0.8f), $"the last item is pushed to the right edge, but starts at {right}");
    }

    [Fact]
    public void Items_that_grow_share_what_is_left_over()
    {
        var pdf = Html(
            "<div style='display:flex'>" +
            "<div style='flex:1'>bir</div><div style='flex:2'>iki</div></div>");

        // The first item takes a third of the line, so the second one starts there.
        Assert.Equal(40 + (Content / 3), Place(pdf, "iki").X, 1);
    }

    [Fact]
    public void An_item_may_be_given_a_size_to_start_from()
    {
        var pdf = Html(
            "<div style='display:flex'>" +
            "<div style='flex:0 0 120pt'>bir</div><div>iki</div></div>");

        Assert.Equal(160, Place(pdf, "iki").X, 1);
    }

    [Fact]
    public void A_line_that_is_full_wraps_to_the_next()
    {
        var pdf = Html(
            "<div style='display:flex;flex-wrap:wrap'>" +
            "<div style='width:300pt'>bir</div><div style='width:300pt'>iki</div></div>");

        var first = Place(pdf, "bir");
        var second = Place(pdf, "iki");

        Assert.Equal(first.X, second.X, 1);
        Assert.True(second.Y < first.Y - 5, "the second item is on a line of its own");
    }

    [Fact]
    public void Items_can_be_lined_up_on_their_text()
    {
        var pdf = Html(
            "<div style='display:flex;align-items:baseline'>" +
            "<div style='font-size:24pt'>buyuk</div><div style='font-size:10pt'>kucuk</div></div>");

        Assert.Equal(Place(pdf, "buyuk").Y, Place(pdf, "kucuk").Y, 1);
    }

    [Fact]
    public void Items_can_be_centred_across_the_line()
    {
        var pdf = Html(
            "<div style='display:flex;align-items:center'>" +
            "<div style='font-size:24pt'>buyuk</div><div style='font-size:10pt'>kucuk</div></div>");

        var tall = Place(pdf, "buyuk").Y;
        var small = Place(pdf, "kucuk").Y;

        Assert.True(small > tall, "the smaller item sits in the middle of the line, above the baseline of the taller one");
    }

    [Fact]
    public void An_item_is_never_wider_than_it_says_it_may_be()
    {
        var pdf = Html(
            "<div style='display:flex'>" +
            "<div style='width:100%;max-width:84pt'>dar</div><div>sonraki</div></div>");

        // The first item asks for the whole line and is held to 84 points, so the next one starts there.
        Assert.Equal(124, Place(pdf, "sonraki").X, 1);
    }

    [Fact]
    public void A_margin_told_to_take_what_is_left_pushes_an_item_across()
    {
        var pdf = Html(
            "<div style='display:flex'>" +
            "<div>solda</div><div style='margin-left:auto'>sagda</div></div>");

        Assert.Equal(40, Place(pdf, "solda").X, 1);
        Assert.True(Place(pdf, "sagda").X > 40 + (Content * 0.8f), "the second item is pushed to the right edge");
    }

    [Fact]
    public void The_gap_stands_between_the_items()
    {
        var tight = Html("<div style='display:flex'><div>bir</div><div>iki</div></div>");
        var loose = Html("<div style='display:flex;gap:30pt'><div>bir</div><div>iki</div></div>");

        Assert.Equal(Place(tight, "iki").X + 30, Place(loose, "iki").X, 1);
    }

    [Fact]
    public void A_column_of_items_stacks_them_and_still_breaks_over_pages()
    {
        var items = string.Concat(Enumerable.Range(0, 60).Select(i => $"<div>satir {i}</div>"));
        var pdf = Html($"<div style='display:flex;flex-direction:column'>{items}</div>");

        Assert.True(pdf.PageCount > 1, "a column of blocks goes on over the pages it needs");
        Assert.Equal(Place(pdf, "satir 0").X, Place(pdf, "satir 1").X, 1);
        Assert.True(Place(pdf, "satir 1").Y < Place(pdf, "satir 0").Y, "the items follow one another downwards");
    }

    [Fact]
    public void A_column_told_how_tall_it_is_shares_out_the_space_left_over()
    {
        var pdf = Html(
            "<div style='display:flex;flex-direction:column;height:300pt;justify-content:space-between'>" +
            "<div>ust</div><div>alt</div></div>");

        var top = Place(pdf, "ust").Y;
        var bottom = Place(pdf, "alt").Y;

        Assert.True(top - bottom > 250, $"the two items stand at the ends of 300 points, but are {top - bottom} apart");
    }

    // ---- grids ----

    [Fact]
    public void A_grid_puts_its_children_in_its_columns()
    {
        var pdf = Html(
            "<div style='display:grid;grid-template-columns:repeat(3, 1fr)'>" +
            "<div>a</div><div>b</div><div>c</div><div>d</div></div>");

        Assert.Equal(40, Place(pdf, "a").X, 1);
        Assert.Equal(40 + (Content / 3), Place(pdf, "b").X, 1);
        Assert.Equal(40 + (2 * Content / 3), Place(pdf, "c").X, 1);

        // The fourth child begins the next row, under the first.
        Assert.Equal(40, Place(pdf, "d").X, 1);
        Assert.True(Place(pdf, "d").Y < Place(pdf, "a").Y);
    }

    [Fact]
    public void A_grid_column_may_have_a_width_of_its_own()
    {
        var pdf = Html(
            "<div style='display:grid;grid-template-columns:150pt 1fr'>" +
            "<div>etiket</div><div>deger</div></div>");

        Assert.Equal(190, Place(pdf, "deger").X, 1);
    }

    [Fact]
    public void The_gap_of_a_grid_stands_between_its_columns()
    {
        var pdf = Html(
            "<div style='display:grid;grid-template-columns:100pt 1fr;gap:20pt'>" +
            "<div>etiket</div><div>deger</div></div>");

        Assert.Equal(160, Place(pdf, "deger").X, 1);
    }

    // ---- elements placed against their container ----

    [Fact]
    public void An_element_can_be_placed_against_its_container()
    {
        var pdf = Html(
            "<div style='position:relative;width:400pt;height:200pt'>" +
            "<div style='position:absolute;left:50%;top:25%'>orta</div></div>");

        var against = Html(
            "<div style='position:relative;width:400pt;height:200pt'>" +
            "<div style='position:absolute;left:0;top:0'>orta</div></div>");

        Assert.Equal(240, Place(pdf, "orta").X, 1);

        // A quarter of the way down a box 200 points tall is 50 points below its top edge.
        Assert.Equal(Place(against, "orta").Y - 50, Place(pdf, "orta").Y, 1);
    }

    [Fact]
    public void Placing_an_element_takes_it_out_of_the_flow()
    {
        var placed = Html(
            "<div style='position:relative;height:100pt'>" +
            "<div style='position:absolute;top:0'>ustte</div></div><p>sonra</p>");

        var plain = Html("<div style='position:relative;height:100pt'></div><p>sonra</p>");

        Assert.Equal(Place(plain, "sonra").Y, Place(placed, "sonra").Y, 1);
    }

    [Fact]
    public void An_element_can_be_held_against_the_far_edges()
    {
        var pdf = Html(
            "<div style='position:relative;width:400pt;height:200pt'>" +
            "<div style='position:absolute;right:0;bottom:0'>kose</div></div>");

        var (x, y) = Place(pdf, "kose");

        Assert.True(x > 40 + 300, $"the element is held to the right edge of the box, but starts at {x}");
        Assert.True(y < 841.89f - 40 - 180, "the element is held to the bottom edge of the box");
    }

    [Fact]
    public void A_negative_offset_puts_an_element_outside_its_container()
    {
        var pdf = Html(
            "<p>once</p><div style='position:relative;width:100pt;height:100pt'>" +
            "<div style='position:absolute;top:-20pt;left:0'>yukarida</div></div>");

        var inside = Html(
            "<p>once</p><div style='position:relative;width:100pt;height:100pt'>" +
            "<div style='position:absolute;top:0;left:0'>yukarida</div></div>");

        Assert.Equal(Place(inside, "yukarida").Y + 20, Place(pdf, "yukarida").Y, 1);
    }

    [Fact]
    public void Anything_placed_against_its_container_leaves_the_line_it_was_written_in()
    {
        // An element that flows with the text becomes a block of its own once it is placed against its
        // container, so it is drawn where its offsets put it and not among the words.
        var pdf = Html(
            "<div style='position:relative;width:300pt'>" +
            "<p>akis</p><span style='position:absolute;left:200pt;top:0'>kose</span></div>");

        Assert.Equal(240, Place(pdf, "kose").X, 1);
        Assert.Equal(40, Place(pdf, "akis").X, 1);
    }

    [Fact]
    public void An_element_placed_against_one_side_is_as_wide_as_what_it_holds()
    {
        var pdf = Html(
            "<div style='position:relative;width:400pt;height:60pt'>" +
            "<span style='display:flex;align-items:center;position:absolute;right:0;top:0;" +
            "padding:0 8pt;height:20pt;background:#eeeeee'>Sayfa 1 / 1</span></div>");

        // The label hugs its words in the corner instead of spanning the whole box.
        var (x, _) = Place(pdf, "Sayfa");
        Assert.True(x > 40 + 300, $"the label is held to the right edge, but its text starts at {x}");
    }

    [Fact]
    public void An_element_held_to_both_sides_spans_the_distance_between_them()
    {
        var pdf = Html(
            "<div style='position:relative;width:400pt;height:100pt'>" +
            "<div style='position:absolute;left:20pt;right:20pt;text-align:right'>sagda</div></div>");

        // The element is 360 points wide, so right-aligned text ends at its right edge.
        Assert.True(Place(pdf, "sagda").X > 40 + 300, "the element spans the box, so its text can be pushed right");
    }

    // ---- sizes ----

    [Fact]
    public void A_width_in_per_cent_follows_the_space_around_it()
    {
        var pdf = Html("<div style='width:50%;text-align:right'>sag</div>");

        // Right-aligned text in a box half the width ends halfway across.
        Assert.True(Place(pdf, "sag").X < 40 + (Content / 2), "the box is half as wide as the page");
        Assert.True(Place(pdf, "sag").X > 40 + (Content / 4), "the box is as wide as half the page, not narrower");
    }

    [Fact]
    public void Border_box_counts_the_padding_inside_the_width()
    {
        var border = Html("<div style='box-sizing:border-box;width:200pt;padding:20pt;text-align:right'>sag</div>");
        var content = Html("<div style='width:200pt;padding:20pt;text-align:right'>sag</div>");

        // Counted inside, the padding takes room from the text; counted outside, it is added to the box.
        Assert.Equal(Place(border, "sag").X + 40, Place(content, "sag").X, 1);
    }

    [Fact]
    public void A_width_is_held_inside_the_largest_width()
    {
        var pdf = Html("<div style='width:2000pt;max-width:100%;text-align:right'>sag</div>");

        Assert.Equal(1, pdf.PageCount);
        Assert.True(Place(pdf, "sag").X < 40 + Content, "the box is no wider than the page it is on");
        Assert.True(Place(pdf, "sag").X > 40 + Content - 40, "the box fills the width it is allowed");
    }

    [Fact]
    public void A_smallest_width_holds_the_box_open()
    {
        var pdf = Html("<div style='min-width:300pt;text-align:right'>sag</div>");

        Assert.True(Place(pdf, "sag").X > 40 + 250, "the box is at least 300 points wide");
    }

    [Fact]
    public void Calc_adds_up_what_it_is_given()
    {
        var pdf = Html(
            "<div style='position:relative;width:400pt;height:100pt'>" +
            "<div style='position:absolute;left:calc(50% + 10pt)'>orta</div></div>");

        Assert.Equal(250, Place(pdf, "orta").X, 1);
    }

    [Fact]
    public void A_length_may_be_a_share_of_the_page()
    {
        // A tenth of an A4 page is 84.19 points, and a box of that height stands where one is told to.
        var share = Html("<div style='height:10vh'>ust</div><p>sonra</p>");
        var points = Html("<div style='height:84.19pt'>ust</div><p>sonra</p>");

        Assert.Equal(Place(points, "sonra").Y, Place(share, "sonra").Y, 1);
        Assert.True(Place(share, "sonra").Y < Place(share, "ust").Y - 84, "the box is as tall as it was told");
    }

    [Fact]
    public void A_zoom_draws_the_same_markup_larger()
    {
        var lines = string.Concat(Enumerable.Range(0, 40).Select(i => $"<p>satir {i}</p>"));
        var plain = Html(lines);
        var zoomed = Html(lines, options => options.Zoom(2));

        // Everything is drawn twice the size, so half as much of it fits on a page.
        Assert.Contains("2 0 0 2 ", zoomed.PageContents()[0], StringComparison.Ordinal);
        Assert.True(zoomed.PageCount > plain.PageCount,
            $"magnified markup needs more pages, but {zoomed.PageCount} is no more than {plain.PageCount}");
    }

    // ---- pictures ----

    [Fact]
    public void A_picture_is_drawn_at_its_own_size()
    {
        var pdf = Html("<img src='circle-rgba.png'>");

        // 256 pixels at 96 to the inch are 192 points.
        var picture = Assert.Single(Pictures(pdf));
        Assert.Equal(192, picture.Width, 1);
        Assert.Equal(192, picture.Height, 1);
    }

    [Fact]
    public void A_picture_too_wide_for_the_page_is_shrunk_to_it()
    {
        var pdf = Html("<img src='circle-rgba.png' style='width:900pt'>");

        var picture = Assert.Single(Pictures(pdf));
        Assert.Equal(Content, picture.Width, 1);
    }

    [Fact]
    public void A_picture_told_both_its_sizes_fills_exactly_that_box()
    {
        var pdf = Html("<img src='circle-rgba.png' width='120' height='40'>");

        var picture = Assert.Single(Pictures(pdf));
        Assert.Equal(90, picture.Width, 1);
        Assert.Equal(30, picture.Height, 1);
    }

    [Fact]
    public void A_picture_can_be_fitted_inside_its_box()
    {
        var pdf = Html("<img src='circle-rgba.png' width='120' height='40' style='object-fit:contain'>");

        // Kept square, the picture is as large as the shorter side of the box allows.
        var picture = Assert.Single(Pictures(pdf));
        Assert.Equal(30, picture.Width, 1);
        Assert.Equal(30, picture.Height, 1);
    }

    [Fact]
    public void A_picture_stands_in_the_line_of_text_around_it()
    {
        var pdf = Html("<p>once <img src='circle-rgba.png' width='12' height='12'> sonra</p>");

        Assert.Equal(Place(pdf, "once").Y, Place(pdf, "sonra").Y, 1);
        Assert.True(Place(pdf, "sonra").X > Place(pdf, "once").X + 9, "the picture stands between the two words");
    }

    // ---- boxes in a line of text ----

    [Fact]
    public void A_box_in_a_line_of_text_keeps_the_size_it_was_given()
    {
        var pdf = Html("<p>etiket <span style='display:inline-block;width:100pt'>dar</span> son</p>");

        var tag = Place(pdf, "dar");
        var after = Place(pdf, "son");

        Assert.Equal(tag.Y, after.Y, 1);
        Assert.True(after.X >= tag.X + 100, $"the box is 100 points wide, but the next word starts {after.X - tag.X} after it");
    }

    [Fact]
    public void Boxes_in_a_line_wrap_when_the_line_is_full()
    {
        var pdf = Html(
            "<p>" + string.Concat(Enumerable.Range(0, 4).Select(i =>
                $"<span style='display:inline-block;width:200pt'>kutu{i}</span>")) + "</p>");

        Assert.Equal(Place(pdf, "kutu0").Y, Place(pdf, "kutu1").Y, 1);
        Assert.True(Place(pdf, "kutu2").Y < Place(pdf, "kutu0").Y - 5, "the third box starts a line of its own");
    }

    [Fact]
    public void The_space_between_a_word_and_a_box_is_drawn()
    {
        var spaced = Html("<p>etiket <span style='display:inline-block'>kutu</span></p>");
        var tight = Html("<p>etiket<span style='display:inline-block'>kutu</span></p>");

        Assert.True(Place(spaced, "kutu").X > Place(tight, "kutu").X + 1,
            "the space written between the word and the box stands between them");
    }

    // ---- what a rule may say ----

    [Fact]
    public void A_rule_can_name_the_last_child()
    {
        var pdf = Html(
            "<style>li:last-child { font-weight: bold }</style>" +
            "<ul><li>bir</li><li>son</li></ul>");

        // Only the last item is drawn with the bold face, so two fonts are embedded.
        Assert.Equal(2, Regex.Count(pdf.Raw, "/FontFile2"));
    }

    [Fact]
    public void A_rule_can_name_every_second_child()
    {
        var pdf = Html(
            "<style>tr:nth-child(even) td { background: #ff0000 }</style>" +
            "<table><tr><td>bir</td></tr><tr><td>iki</td></tr><tr><td>uc</td></tr><tr><td>dort</td></tr></table>");

        Assert.Equal(2, Regex.Count(pdf.PageContents()[0], "1 0 0 rg"));
    }

    [Fact]
    public void A_rule_can_leave_out_what_it_names()
    {
        var pdf = Html(
            "<style>p:not(.plain) { color: #ff0000 }</style>" +
            "<p class='plain'>duz</p><p>renkli</p>");

        Assert.Equal(1, Regex.Count(pdf.PageContents()[0], "1 0 0 rg"));
    }

    [Fact]
    public void A_rule_can_give_an_element_something_after_it()
    {
        var pdf = Html("<style>.note::after { content: ' (dipnot)' }</style><p class='note'>metin</p>");

        Assert.Contains("(dipnot)", pdf.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_can_give_an_element_something_before_it()
    {
        var pdf = Html("<style>.pre::before { content: 'onek ' }</style><p class='pre'>metin</p>");

        Assert.Contains("onek", pdf.ExtractText(), StringComparison.Ordinal);
    }

    // ---- tables ----

    [Fact]
    public void The_columns_of_a_table_are_as_wide_as_what_they_hold()
    {
        var pdf = Html("<table><tr><td>a</td><td>cok daha uzun bir hucre</td></tr></table>");

        // The first column is only as wide as its letter, so the second one starts near the left margin.
        Assert.True(Place(pdf, "cok daha uzun bir hucre").X < 40 + 60,
            $"the second column starts at {Place(pdf, "cok daha uzun bir hucre").X}, as if the first were half the table");
    }

    [Fact]
    public void A_table_told_how_wide_it_is_fills_that_width()
    {
        var pdf = Html("<table style='width:100%'><tr><td>a</td><td>uzun hucre</td></tr></table>");

        Assert.True(Place(pdf, "uzun hucre").X > 40 + 60, "the columns are stretched to fill the table");
    }

    [Fact]
    public void A_group_of_rows_can_be_made_the_head_of_a_table()
    {
        var rows = string.Concat(Enumerable.Range(0, 80).Select(i => $"<tr><td>satir {i}</td></tr>"));
        var pdf = Html(
            "<table><tbody style='display:table-header-group'><tr><td>BASLIK</td></tr></tbody>" +
            $"<tbody>{rows}</tbody></table>");

        Assert.True(pdf.PageCount > 1);
        Assert.Equal(pdf.PageCount, Regex.Count(pdf.ExtractText(), "BASLIK"));
    }

    // ---- drawings and fonts ----

    [Fact]
    public void A_drawing_written_in_the_markup_is_drawn()
    {
        var pdf = Html(
            "<svg width='100' height='50' viewBox='0 0 100 50'>" +
            "<rect x='0' y='0' width='100' height='50' fill='#ff0000'/></svg>");

        Assert.Contains("1 0 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_drawing_keeps_the_capital_letters_of_its_own_language()
    {
        var pdf = Html(
            "<svg width='100' height='50' viewBox='0 0 200 100'>" +
            "<rect width='200' height='100' fill='#00ff00'/></svg>");

        // The view box is only understood where its name was not folded to lower case. The drawing is
        // 200 units wide in a box 100 pixels across, so it is drawn 75 points wide — and the rectangle
        // that fills it reaches the right edge of that box.
        var content = pdf.PageContents()[0];
        Assert.Contains("0 1 0 rg", content, StringComparison.Ordinal);

        var right = Regex.Matches(content, @"(?<x>[\d.]+) [\d.]+ l")
            .Select(match => float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture))
            .Max();

        Assert.Equal(115, right, 1);
    }

    [Fact]
    public void The_text_can_ask_the_font_for_a_feature()
    {
        // The glyphs of a subset are numbered as they are used, so two runs of ten figures carry the same
        // numbers whichever figures they are; what tells them apart is the outlines that were embedded.
        var plain = Generate(c => c.Html("<p>0123456789</p>"));
        var oldstyle = Generate(c => c.Html("<p style='font-variant-numeric:oldstyle-nums'>0123456789</p>"));
        var settings = Generate(c => c.Html("<p style='font-feature-settings:\"onum\"'>0123456789</p>"));

        Assert.NotEqual(Convert.ToBase64String(plain), Convert.ToBase64String(oldstyle));
        Assert.Equal(Convert.ToBase64String(oldstyle), Convert.ToBase64String(settings));
    }

    [Fact]
    public void A_feature_turned_off_is_not_asked_for()
    {
        var plain = Generate(c => c.Html("<p>0123456789</p>"));
        var off = Generate(c => c.Html("<p style='font-feature-settings:\"onum\" 0'>0123456789</p>"));

        Assert.Equal(Convert.ToBase64String(plain), Convert.ToBase64String(off));
    }

    // ---- what the text looks like ----

    [Fact]
    public void A_rule_can_ask_for_capital_letters()
    {
        var pdf = Html("<p style='text-transform:uppercase'>rapor</p>");

        Assert.Contains("RAPOR", pdf.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Capital_letters_follow_the_language_of_the_markup()
    {
        // The capital of a Turkish "i" is "İ", and of an English one "I".
        var turkish = Html("<div lang='tr'><p style='text-transform:uppercase'>tarih</p></div>");
        var english = Html("<div lang='en'><p style='text-transform:uppercase'>tarih</p></div>");

        Assert.Contains("TARİH", turkish.ExtractText(), StringComparison.Ordinal);
        Assert.Contains("TARIH", english.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_letter_of_every_word_can_be_made_a_capital()
    {
        var pdf = Html("<p style='text-transform:capitalize'>iki kelime</p>");

        Assert.Contains("Iki Kelime", pdf.ExtractText(), StringComparison.Ordinal);
    }

    // ---- boxes that carry on over a page ----

    [Fact]
    public void The_padding_of_a_box_that_breaks_is_not_drawn_again_at_the_fold()
    {
        var lines = string.Concat(Enumerable.Range(0, 80).Select(i => $"<p>satir {i}</p>"));
        var pdf = Html($"<div style='padding:60pt'>{lines}</div>");

        Assert.True(pdf.PageCount > 1);

        // The padding stands at the start of the box and at its end, so the second page begins at the
        // top of the page rather than 60 points below it.
        var first = pdf.TextRuns().First(run => run.Page == 1);
        Assert.True(first.Y > 841.89f - 40 - 30, $"the second page starts at {841.89f - first.Y} from the top of the page");
    }

    [Fact]
    public void A_border_takes_room_of_its_own()
    {
        var plain = Html("<div style='padding:10pt'>bir</div>");
        var bordered = Html("<div style='padding:10pt;border:4pt solid #000'>bir</div>");

        Assert.Equal(Place(plain, "bir").X + 4, Place(bordered, "bir").X, 1);
        Assert.Equal(Place(plain, "bir").Y - 4, Place(bordered, "bir").Y, 1);
    }

    // ---- what a page does with content that does not fit ----

    [Fact]
    public void A_box_wider_than_the_space_overflows_it()
    {
        // Without box-sizing the padding is added to the width, so the box is wider than what holds it.
        // A browser lets it hang over the edge; it must not stop the document being written.
        var pdf = Html("<div style='display:flex; width:100%; padding:8pt'>tasan kutu</div>");

        Assert.Equal(1, pdf.PageCount);
        Assert.Equal(48, Place(pdf, "tasan").X, 1);
    }

    [Fact]
    public void A_box_taller_than_the_page_carries_on_over_it()
    {
        var pdf = Html("<div style='height:1500pt;background:#eeeeee'>uzun kutu</div><p>sonra</p>");

        Assert.True(pdf.PageCount >= 2, $"the box spans more than one page, but the document has {pdf.PageCount}");
        Assert.Contains("sonra", pdf.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Boxes_as_tall_as_a_page_leave_no_page_empty()
    {
        // Four boxes, each a little over half a page: a browser cuts them where the page ends rather
        // than moving a whole box to the next page and leaving the rest of this one empty.
        var boxes = string.Concat(Enumerable.Range(0, 4).Select(i => $"<div style='height:430pt'>kutu {i}</div>"));
        var pdf = Html(boxes);

        Assert.Equal(3, pdf.PageCount);
        for (var i = 0; i < 4; i++)
            Assert.Contains($"kutu {i}", pdf.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_box_built_with_the_fluent_api_moves_to_the_next_page_as_a_whole()
    {
        // What markup asks for and what someone writing C# asks for are not the same: a box given a
        // height in the fluent API is meant to stay whole.
        var pdf = Inspect(Generate(c => c.Column(column =>
        {
            column.Item().Height(500).Background(Colors.Grey.Lighten2);
            column.Item().Height(500).Background(Colors.Grey.Lighten3);
        })));

        Assert.Equal(2, pdf.PageCount);
    }

    // ---- style sheets the document links to ----

    [Fact]
    public void A_style_sheet_the_document_links_to_is_read()
    {
        var pdf = Html(
            "<link rel=\"stylesheet\" href=\"data:text/css,p{color:%23ff0000}\">" +
            "<p>kirmizi</p>");

        Assert.Contains("1 0 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_style_sheet_beside_the_markup_is_read()
    {
        var folder = Directory.CreateTempSubdirectory("papira");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "site.css"), "p { color: #ff0000 }");
            var pdf = Inspect(Generate(c => c.Html(
                "<link rel='stylesheet' href='site.css'><p>kirmizi</p>",
                o => o.BaseDirectory(folder.FullName))));

            Assert.Contains("1 0 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_style_sheet_that_cannot_be_read_leaves_the_document_whole()
    {
        var pdf = Html("<link rel='stylesheet' href='yok.css'><link rel='stylesheet' href='https://example.invalid/b.css'><p>metin</p>");

        Assert.Equal("metin\n", pdf.ExtractText());
    }

    [Fact]
    public void Rules_meant_for_a_screen_are_not_given_to_a_page()
    {
        var pdf = Html(
            "<style>@media screen { p { color: #00ff00 } .inner { color: #00ff00 } }" +
            "@media print { p { color: #ff0000 } }</style>" +
            "<p>yazdirma</p><p class='inner'>icerideki</p>");

        // The rules inside a block meant for a screen are not applied either, block and all.
        Assert.DoesNotContain("0 1 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
        Assert.Contains("1 0 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_may_ask_how_wide_the_page_is()
    {
        var wide = Html("<style>@media (min-width: 400px) { p { color: #ff0000 } }</style><p>metin</p>");
        var narrow = Html("<style>@media (min-width: 2000px) { p { color: #ff0000 } }</style><p>metin</p>");

        Assert.Contains("1 0 0 rg", wide.PageContents()[0], StringComparison.Ordinal);
        Assert.DoesNotContain("1 0 0 rg", narrow.PageContents()[0], StringComparison.Ordinal);
    }

    // ---- shadows ----

    [Fact]
    public void A_shadow_with_nothing_to_blur_is_a_shape_like_any_other()
    {
        var pdf = Html("<div style='width:100pt;height:50pt;background:#fff;box-shadow:6pt 6pt 0 #ff0000'>golge</div>");

        // Drawn as a plain filled path in the colour it was given, moved by the offset.
        Assert.Contains("1 0 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
        Assert.DoesNotContain("/SMask", pdf.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blurred_shadow_is_laid_down_through_a_picture_of_its_softness()
    {
        var pdf = Html("<div style='width:100pt;height:50pt;background:#fff;box-shadow:0 2pt 6pt rgba(0,0,0,0.3)'>golge</div>");

        // The colour stays a colour; only the soft edge is a picture, as a browser writes one.
        Assert.Contains("/SMask<</Type/Mask/S/Luminosity", pdf.Raw, StringComparison.Ordinal);
        Assert.Contains("/ColorSpace/DeviceGray", pdf.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shadow_cast_inside_the_box_is_left_out()
    {
        var inset = Html("<div style='width:100pt;height:50pt;box-shadow:inset 0 2pt 6pt #000'>golge</div>");
        var none = Html("<div style='width:100pt;height:50pt'>golge</div>");

        Assert.DoesNotContain("/SMask", inset.Raw, StringComparison.Ordinal);
        Assert.Equal(none.PageContents()[0].Length, inset.PageContents()[0].Length);
    }

    [Fact]
    public void A_box_may_cast_several_shadows()
    {
        var pdf = Html(
            "<div style='width:100pt;height:50pt;background:#fff;" +
            "box-shadow:4pt 4pt 0 #ff0000, -4pt -4pt 0 #0000ff'>golge</div>");

        Assert.Contains("1 0 0 rg", pdf.PageContents()[0], StringComparison.Ordinal);
        Assert.Contains("0 0 1 rg", pdf.PageContents()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void How_much_of_a_shadow_shows_through_is_read_from_its_colour()
    {
        var solid = Html("<div style='width:100pt;height:50pt;box-shadow:6pt 6pt 0 rgb(0,0,0)'>golge</div>");
        var faint = Html("<div style='width:100pt;height:50pt;box-shadow:6pt 6pt 0 rgba(0,0,0,0.2)'>golge</div>");

        // A shadow that only partly shows through is drawn in a group of its own, with how much it shows.
        Assert.Contains("/ExtGState", faint.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/ExtGState", solid.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fluent_api_can_cast_a_shadow_too()
    {
        var pdf = Inspect(Generate(c => c
            .Shadow(0, 2, 6, Colors.Black, opacity: 0.3f)
            .CornerRadius(6)
            .Background(Colors.White)
            .Width(120).Height(60)));

        Assert.Contains("/SMask<</Type/Mask/S/Luminosity", pdf.Raw, StringComparison.Ordinal);
    }

    // ---- what a reader for the blind is told ----

    [Fact]
    public void A_picture_with_an_empty_description_is_marked_as_decoration()
    {
        // A picture whose description is empty says it is decoration: it is not a figure of the document
        // but an artifact of the page, which a reader for the blind passes over.
        Assert.Contains("/Figure", Tagged("<img src='circle-rgba.png' alt='daire'>").Raw, StringComparison.Ordinal);

        var decorative = Tagged("<img src='circle-rgba.png' alt=''>");
        Assert.DoesNotContain("/Figure", decorative.Raw, StringComparison.Ordinal);
        Assert.Contains("/Artifact", decorative.PageContents()[0], StringComparison.Ordinal);
    }

    /// <summary>The markup laid out as a tagged document, which is what a reader for the blind reads.</summary>
    private static PdfInspector Tagged(string html) => Inspect(Document.Create(d => d.Page(p =>
    {
        p.Size(PageSizes.A4).Margin(40);
        p.Content().Html(html, o => o.BaseDirectory(Path.Combine(AppContext.BaseDirectory, "Assets")));
    })).WithSettings(new DocumentSettings { Tagged = true }).GeneratePdf());

    // ---- the page number ----

    [Fact]
    public void A_footer_written_as_markup_can_say_which_page_it_is_on()
    {
        var pages = string.Concat(Enumerable.Range(0, 60).Select(i => $"<p>satir {i}</p>"));
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A4).Margin(40);
            p.Content().Html(pages);
            p.Footer().Html("<div>Sayfa <span class='pageNumber'></span> / <span class='totalPages'></span></div>");
        })).GeneratePdf());

        // Each piece of the footer is drawn where it belongs, so the text comes back as separate runs.
        var text = Regex.Replace(pdf.ExtractText(), @"\s+", " ");

        Assert.True(pdf.PageCount > 1);
        Assert.Contains($"Sayfa 1 / {pdf.PageCount} ", text, StringComparison.Ordinal);
        Assert.Contains($"Sayfa 2 / {pdf.PageCount} ", text, StringComparison.Ordinal);
    }

    // ---- pictures from the network ----

    [Fact]
    public void A_picture_is_only_fetched_over_the_network_once_it_may_be()
    {
        // Nothing is fetched until the document says it may be, and the picture is simply left out.
        var pdf = Html("<p>once</p><img src='https://example.invalid/logo.png'><p>sonra</p>");

        Assert.Empty(Pictures(pdf));
        Assert.Equal("once\nsonra\n", pdf.ExtractText());
    }

    [Fact]
    public void A_picture_whose_address_leads_nowhere_leaves_the_document_whole()
    {
        // The address of a picture is often data, and data is often wrong.
        var pdf = Html("<p>once</p><img src='yok.jpg'><img src=''><img src='../disarida.png'><p>sonra</p>");

        Assert.Empty(Pictures(pdf));
        Assert.Equal("once\nsonra\n", pdf.ExtractText());
    }

    [Fact]
    public void A_picture_that_does_not_arrive_leaves_the_document_whole()
    {
        var pdf = Html(
            "<p>once</p><img src='http://127.0.0.1:1/yok.png'><p>sonra</p>",
            options => options.AllowRemoteImages(TimeSpan.FromSeconds(2)));

        Assert.Empty(Pictures(pdf));
        Assert.Contains("once", pdf.ExtractText(), StringComparison.Ordinal);
        Assert.Contains("sonra", pdf.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_is_fetched_over_the_network_and_drawn()
    {
        using var server = new Server(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "circle-rgba.png")));
        if (!server.Started)
            return; // the machine does not allow listening on a port

        var pdf = Html(
            $"<img src='{server.Address}logo.png' width='60'>",
            options => options.AllowRemoteImages(TimeSpan.FromSeconds(5), allowPrivateNetworks: true));

        var picture = Assert.Single(Pictures(pdf));
        Assert.Equal(45, picture.Width, 1);
    }

    [Fact]
    public void A_picture_on_the_machine_itself_is_refused_unless_it_is_allowed()
    {
        using var server = new Server(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "circle-rgba.png")));
        if (!server.Started)
            return;

        var pdf = Html(
            $"<img src='{server.Address}logo.png' width='60'>",
            options => options.AllowRemoteImages(TimeSpan.FromSeconds(5)));

        Assert.Empty(Pictures(pdf));
    }

    [Fact]
    public void A_style_sheet_is_fetched_over_the_network_once_it_may_be()
    {
        using var server = new Server(System.Text.Encoding.UTF8.GetBytes("p { color: #ff0000 }"), "text/css");
        if (!server.Started)
            return; // the machine does not allow listening on a port

        var refused = Html($"<link rel='stylesheet' href='{server.Address}site.css'><p>metin</p>");
        var allowed = Html(
            $"<link rel='stylesheet' href='{server.Address}site.css'><p>metin</p>",
            options => options.AllowRemoteStyleSheets(TimeSpan.FromSeconds(5), allowPrivateNetworks: true));

        Assert.DoesNotContain("1 0 0 rg", refused.PageContents()[0], StringComparison.Ordinal);
        Assert.Contains("1 0 0 rg", allowed.PageContents()[0], StringComparison.Ordinal);
    }

    /// <summary>A web server on the machine itself, which the tests fetch a picture or a sheet from.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener? _listener;

        public Server(byte[] payload, string mediaType = "image/png")
        {
            for (var port = 8731; port < 8741 && _listener == null; port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    _listener = listener;
                    Address = $"http://127.0.0.1:{port}/";
                }
                catch (Exception exception) when (exception is HttpListenerException or PlatformNotSupportedException)
                {
                    // That port is taken or listening is not allowed; try the next one.
                }
            }

            if (_listener == null)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync().ConfigureAwait(false);
                        context.Response.ContentType = mediaType;
                        context.Response.ContentLength64 = payload.Length;
                        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                        context.Response.Close();
                    }
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // The server was shut down while waiting for a request.
                }
            });
        }

        public bool Started => _listener != null;

        public string Address { get; } = string.Empty;

        public void Dispose() => _listener?.Close();
    }
}
