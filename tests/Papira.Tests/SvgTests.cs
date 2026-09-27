using Papira.Elements;
using Papira.Infrastructure;
using Papira.Rendering;
using Papira.Svg;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// The SVG subset Papira draws. The drawing these tests are built from was rendered side by side with the
/// SVG rasterizer of macOS: the two images differ only by anti-aliasing (largest channel difference 16/255).
/// </summary>
public class SvgTests
{
    private static SvgPath ParsePath(string data)
    {
        var path = new SvgPath();
        SvgPathParser.Parse(data, path, 10_000);
        return path;
    }

    private static string Ops(SvgPath path) => string.Concat(path.Commands.Select(command => command.Op));

    private static string Markup(string body, string attributes = @"width=""100"" height=""100"" viewBox=""0 0 100 100""") =>
        $@"<svg xmlns=""http://www.w3.org/2000/svg"" {attributes}>{body}</svg>";

    private static SvgDocument Parse(string body) => SvgImage.FromString(Markup(body)).Document;

    private static SvgShapeNode FirstShape(SvgNode node)
    {
        if (node is SvgShapeNode shape)
            return shape;

        foreach (var child in ((SvgGroupNode)node).Children)
        {
            if (Find(child) is { } found)
                return found;
        }

        throw new InvalidOperationException("the drawing has no shapes");

        static SvgShapeNode? Find(SvgNode node) => node switch
        {
            SvgShapeNode shape => shape,
            SvgGroupNode group => group.Children.Select(Find).FirstOrDefault(found => found != null),
            _ => null,
        };
    }

    /// <summary>Draws markup into a 100x100 point box and returns the page content stream.</summary>
    private static string Draw(string body, string? attributes = null)
    {
        var image = SvgImage.FromString(attributes == null ? Markup(body) : Markup(body, attributes));
        return Inspect(Generate(c => c.Width(100).Height(100).Svg(image))).PageContents()[0];
    }

    // ---- Path data -------------------------------------------------------------------------------

    [Fact]
    public void Path_commands_may_be_absolute_or_relative()
    {
        var path = ParsePath("M10 10 l10 0 H50 V40 Z");

        Assert.Equal("mlllh", Ops(path));
        Assert.Equal([(10f, 10f), (20f, 10f), (50f, 10f), (50f, 40f)], path.Commands.Take(4).Select(c => (c.X, c.Y)));
    }

    [Fact]
    public void Numbers_need_no_separator_when_the_sign_or_the_point_ends_them()
    {
        var path = ParsePath("M1.5.5L-1-2l1e2 1E-2");

        Assert.Equal([(1.5f, 0.5f), (-1f, -2f), (99f, -1.99f)], path.Commands.Select(c => (c.X, c.Y)));
    }

    [Fact]
    public void Coordinates_after_a_move_are_implicit_lines()
    {
        Assert.Equal("mll", Ops(ParsePath("M0 0 10 10 20 0")));
        Assert.Equal("mll", Ops(ParsePath("m0 0 10 10 10-10")));
    }

    [Fact]
    public void Smooth_curves_mirror_the_previous_control_point()
    {
        var curve = ParsePath("M0 0C10 0 20 10 30 10S50 20 60 20").Commands[2];

        Assert.Equal((40f, 10f), (curve.X1, curve.Y1));
        Assert.Equal((50f, 20f), (curve.X2, curve.Y2));
        Assert.Equal((60f, 20f), (curve.X, curve.Y));
    }

    [Fact]
    public void Smooth_curves_without_a_previous_curve_start_at_the_current_point()
    {
        var curve = ParsePath("M10 10S50 20 60 20").Commands[1];

        Assert.Equal((10f, 10f), (curve.X1, curve.Y1));
    }

    [Fact]
    public void Quadratic_curves_become_cubic()
    {
        var curve = ParsePath("M0 0Q30 0 30 30").Commands[1];

        Assert.Equal('c', curve.Op);
        Assert.Equal((20f, 0f), (curve.X1, curve.Y1));
        Assert.Equal((30f, 10f), (curve.X2, curve.Y2));
    }

    [Theory]
    // A half circle between the same two points, on one side or the other.
    [InlineData(1, 0f, 50f)]
    [InlineData(0, 50f, 100f)]
    public void Arcs_follow_the_sweep_flag(int sweep, float expectedMinY, float expectedMaxY)
    {
        var bounds = ParsePath($"M0 50A50 50 0 0 {sweep} 100 50").Bounds();

        Assert.Equal(0, bounds.MinX, 2);
        Assert.Equal(100, bounds.MaxX, 2);
        Assert.Equal(expectedMinY, bounds.MinY, 2);
        Assert.Equal(expectedMaxY, bounds.MaxY, 2);
    }

    [Fact]
    public void Arcs_with_a_zero_radius_are_straight_lines()
    {
        Assert.Equal("ml", Ops(ParsePath("M0 0A0 0 0 0 1 50 50")));
    }

    [Fact]
    public void Arc_radii_too_small_for_the_endpoints_are_scaled_up()
    {
        // A radius of 10 cannot span 100 points, so it grows to 50 and the arc stays a half circle.
        var bounds = ParsePath("M0 50A10 10 0 0 1 100 50").Bounds();

        Assert.Equal(0, bounds.MinY, 1);
        Assert.Equal(50, bounds.MaxY, 1);
    }

    [Fact]
    public void Curve_bounds_are_solved_rather_than_taken_from_the_control_points()
    {
        // The control points reach y = 100, but the curve itself only reaches 75.
        var bounds = ParsePath("M0 0C0 100 100 100 100 0").Bounds();

        Assert.Equal(75, bounds.MaxY, 2);
    }

    [Fact]
    public void An_unsupported_command_ends_the_path()
    {
        Assert.Equal("ml", Ops(ParsePath("M0 0 L10 10 X20 20 L30 30")));
    }

    [Fact]
    public void A_path_longer_than_the_limit_is_cut_off()
    {
        var path = new SvgPath();
        SvgPathParser.Parse(string.Concat(Enumerable.Repeat("L1 1", 50)), path, 10);

        Assert.True(path.Count <= 10, $"expected at most 10 commands, got {path.Count}");
    }

    // ---- Shapes ----------------------------------------------------------------------------------

    [Fact]
    public void Shapes_have_the_bounds_their_attributes_describe()
    {
        Assert.Equal((10f, 20f, 40f, 50f), Bounds(@"<rect x=""10"" y=""20"" width=""30"" height=""30""/>"));
        Assert.Equal((10f, 20f, 50f, 40f), Bounds(@"<ellipse cx=""30"" cy=""30"" rx=""20"" ry=""10""/>"));
        Assert.Equal((5f, 5f, 25f, 25f), Bounds(@"<circle cx=""15"" cy=""15"" r=""10""/>"));
        Assert.Equal((1f, 2f, 3f, 4f), Bounds(@"<line x1=""1"" y1=""2"" x2=""3"" y2=""4"" stroke=""red""/>"));
        Assert.Equal((0f, 0f, 20f, 10f), Bounds(@"<polygon points=""0,0 20,0 20,10 0,10""/>"));

        static (float, float, float, float) Bounds(string body)
        {
            var bounds = FirstShape(Parse(body).Root).Path.Bounds();
            return (bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        }
    }

    [Fact]
    public void A_rectangle_with_one_radius_is_rounded_on_both_axes()
    {
        var rounded = FirstShape(Parse(@"<rect width=""40"" height=""40"" rx=""8""/>").Root).Path;
        var square = FirstShape(Parse(@"<rect width=""40"" height=""40""/>").Root).Path;

        Assert.Contains('c', Ops(rounded));
        Assert.DoesNotContain('c', Ops(square));
    }

    [Fact]
    public void A_polygon_closes_and_a_polyline_does_not()
    {
        Assert.Equal("mllh", Ops(FirstShape(Parse(@"<polygon points=""0,0 10,0 10,10""/>").Root).Path));
        Assert.Equal("mll", Ops(FirstShape(Parse(@"<polyline points=""0,0 10,0 10,10"" fill=""red""/>").Root).Path));
    }

    // ---- Values ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC)]
    [InlineData("#aabbcc", 0xAA, 0xBB, 0xCC)]
    [InlineData("#aabbcc80", 0xAA, 0xBB, 0xCC)]     // the alpha of a hex color is dropped
    [InlineData("rgb(1, 2, 3)", 1, 2, 3)]
    [InlineData("rgb(100%, 0%, 50%)", 255, 0, 128)]
    [InlineData("rgba(9, 8, 7, 0.5)", 9, 8, 7)]
    [InlineData("hsl(120, 100%, 50%)", 0, 255, 0)]
    [InlineData("rebeccapurple", 0x66, 0x33, 0x99)]
    [InlineData("  White ", 255, 255, 255)]
    public void Colors_are_read_in_every_notation_svg_allows(string text, int r, int g, int b)
    {
        Assert.True(SvgValues.TryColor(text, out var color));
        Assert.Equal(new Color((byte)r, (byte)g, (byte)b), color);
    }

    [Theory]
    [InlineData("nosuchcolor")]
    [InlineData("#12345")]
    [InlineData("")]
    public void Unknown_colors_are_rejected(string text) => Assert.False(SvgValues.TryColor(text, out _));

    [Theory]
    [InlineData("42", 42)]
    [InlineData("10mm", 37.7953f)]
    [InlineData("1in", 96)]
    [InlineData("12pt", 16)]
    [InlineData("50%", 100)]        // of the 200 unit basis
    [InlineData("2em", 32)]         // of the 16 unit default font size
    public void Lengths_are_converted_to_user_units(string text, float expected) =>
        Assert.Equal(expected, SvgValues.Length(text, 200), 3);

    [Fact]
    public void Transform_lists_apply_from_left_to_right()
    {
        // scale runs inside translate, so the point is scaled first and then moved.
        var matrix = SvgValues.Transform("translate(10 20) scale(2)");
        Assert.Equal((12f, 22f), matrix.Apply(1, 1));

        // A rotation with a centre turns around that point.
        var rotated = SvgValues.Transform("rotate(90 10 10)").Apply(20, 10);
        Assert.Equal(10, rotated.X, 3);
        Assert.Equal(20, rotated.Y, 3);

        Assert.Equal((7f, 11f), SvgValues.Transform("matrix(1 0 0 1 5 9)").Apply(2, 2));
        Assert.Equal((3f, 2f), SvgValues.Transform("skewX(45)").Apply(1, 2));
    }

    [Fact]
    public void A_dash_pattern_with_an_odd_length_repeats()
    {
        // An odd pattern repeats to become even, as CSS prescribes.
        Assert.Equal<float[]>([5, 3, 5, 5, 3, 5], SvgValues.Dashes("5 3 5", 100)!);
        Assert.Null(SvgValues.Dashes("none", 100));
        Assert.Null(SvgValues.Dashes("0 0", 100));
    }

    // ---- Styles ----------------------------------------------------------------------------------

    [Fact]
    public void Presentation_attributes_are_inherited_from_the_group()
    {
        var shape = FirstShape(Parse(@"<g fill=""#ff0000""><rect width=""10"" height=""10""/></g>").Root);

        Assert.Equal(new Color(255, 0, 0), shape.Style.Fill.Color);
    }

    [Fact]
    public void A_style_sheet_beats_a_presentation_attribute_and_the_style_attribute_beats_both()
    {
        var sheet = @"<style>.a { fill: #00ff00 } #b { fill: #0000ff }</style>";

        Assert.Equal(new Color(0, 255, 0), Fill($@"{sheet}<rect class=""a"" fill=""#ff0000"" width=""1"" height=""1""/>"));
        Assert.Equal(new Color(0, 0, 255), Fill($@"{sheet}<rect id=""b"" class=""a"" width=""1"" height=""1""/>"));
        Assert.Equal(new Color(255, 0, 255), Fill($@"{sheet}<rect id=""b"" style=""fill: #ff00ff"" width=""1"" height=""1""/>"));

        static Color Fill(string body) => FirstShape(Parse(body).Root).Style.Fill.Color;
    }

    [Fact]
    public void Important_declarations_are_read_without_their_marker()
    {
        var shape = FirstShape(Parse(@"<rect style=""fill: #123456 !important"" width=""1"" height=""1""/>").Root);

        Assert.Equal(new Color(0x12, 0x34, 0x56), shape.Style.Fill.Color);
    }

    [Fact]
    public void CurrentColor_follows_the_color_property()
    {
        var shape = FirstShape(Parse(@"<g color=""#00ff00""><rect fill=""currentColor"" width=""1"" height=""1""/></g>").Root);

        Assert.Equal(new Color(0, 255, 0), shape.Style.Fill.Color);
    }

    [Fact]
    public void Shapes_that_paint_nothing_are_left_out()
    {
        Assert.Empty(Parse(@"<rect width=""10"" height=""10"" fill=""none""/>").Root.Children);
        Assert.Empty(Parse(@"<rect width=""10"" height=""10"" display=""none""/>").Root.Children);
        Assert.Empty(Parse(@"<rect width=""10"" height=""10"" visibility=""hidden""/>").Root.Children);
        Assert.Empty(Parse(@"<text x=""0"" y=""10"" display=""none"">not drawn</text>").Root.Children);
        Assert.Empty(Parse(@"<text x=""0"" y=""10""> </text>").Root.Children);
    }

    // ---- Gradients -------------------------------------------------------------------------------

    [Fact]
    public void A_gradient_is_resolved_through_its_reference()
    {
        var gradient = Gradient(@"
            <defs>
              <linearGradient id=""base""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>
              <linearGradient id=""used"" href=""#base"" x1=""0"" y1=""0"" x2=""0"" y2=""1""/>
            </defs>
            <rect width=""10"" height=""10"" fill=""url(#used)""/>");

        Assert.False(gradient.Radial);
        Assert.Equal(0, gradient.X1, 3);
        Assert.Equal(1, gradient.Y1, 3);
        Assert.Equal([new Color(255, 0, 0), new Color(0, 0, 255)], gradient.Stops.Select(stop => stop.Color));
    }

    [Fact]
    public void Gradient_stops_span_the_whole_range_and_never_repeat_an_offset()
    {
        // PDF stitches the segments together, which needs offsets that strictly increase from 0 to 1.
        var gradient = Gradient(@"
            <linearGradient id=""g"">
              <stop offset=""0.25"" stop-color=""#ff0000""/>
              <stop offset=""0.25"" stop-color=""#00ff00""/>
              <stop offset=""0.75"" stop-color=""#0000ff""/>
            </linearGradient>
            <rect width=""10"" height=""10"" fill=""url(#g)""/>");

        Assert.Equal(0, gradient.Stops[0].Offset, 4);
        Assert.Equal(1, gradient.Stops[^1].Offset, 4);
        for (var i = 1; i < gradient.Stops.Length; i++)
            Assert.True(gradient.Stops[i].Offset > gradient.Stops[i - 1].Offset, "offsets must increase");
    }

    [Fact]
    public void A_radial_gradient_runs_from_its_focus_to_the_outer_circle()
    {
        var gradient = Gradient(@"
            <radialGradient id=""g"" cx=""0.4"" cy=""0.6"" r=""0.5"" fx=""0.1"" fy=""0.2"">
              <stop offset=""0"" stop-color=""#ffffff""/><stop offset=""1"" stop-color=""#000000""/>
            </radialGradient>
            <rect width=""10"" height=""10"" fill=""url(#g)""/>");

        Assert.True(gradient.Radial);
        Assert.Equal((0.1f, 0.2f, 0f), (gradient.X0, gradient.Y0, gradient.Radius0));
        Assert.Equal((0.4f, 0.6f, 0.5f), (gradient.X1, gradient.Y1, gradient.Radius1));
    }

    [Fact]
    public void A_missing_paint_server_falls_back_to_the_color_beside_it()
    {
        var shape = FirstShape(Parse(@"<rect width=""10"" height=""10"" fill=""url(#gone) #00ff00""/>").Root);

        Assert.Equal(new Color(0, 255, 0), shape.Style.Fill.Color);
    }

    private static SvgGradient Gradient(string body)
    {
        var fill = FirstShape(Parse(body).Root).Style.Fill;
        Assert.Equal(SvgPaintKind.Gradient, fill.Kind);
        return fill.Gradient!;
    }

    // ---- The document ----------------------------------------------------------------------------

    [Theory]
    [InlineData(@"width=""200"" height=""100""", 200, 100)]
    [InlineData(@"viewBox=""0 0 40 20""", 40, 20)]
    [InlineData(@"width=""20mm"" height=""10mm""", 75.5906f, 37.7953f)]
    [InlineData("", 300, 150)]      // the CSS default for a replaced element
    public void The_intrinsic_size_comes_from_the_attributes_or_the_view_box(string attributes, float width, float height)
    {
        var image = SvgImage.FromString(Markup("", attributes));

        Assert.Equal(width, image.Width, 3);
        Assert.Equal(height, image.Height, 3);
        Assert.Equal(height / width, image.AspectRatio, 4);
    }

    [Fact]
    public void A_nested_viewport_clips_and_scales_its_contents()
    {
        var page = Draw(@"<svg x=""10"" y=""10"" width=""40"" height=""40"" viewBox=""0 0 10 10""><rect width=""10"" height=""10"" fill=""#ff0000""/></svg>");

        Assert.Contains(" W n", page);      // the viewport clip
        Assert.Contains("1 0 0 rg", page);
    }

    [Fact]
    public void Use_draws_the_element_it_points_at()
    {
        var page = Draw(@"<defs><rect id=""r"" width=""10"" height=""10"" fill=""#ff0000""/></defs><use href=""#r"" x=""20"" y=""30""/>");

        Assert.Contains("1 0 0 rg", page);
    }

    [Fact]
    public void Use_pointing_at_a_symbol_scales_it_into_the_given_box()
    {
        var document = Parse(@"<defs><symbol id=""s"" viewBox=""0 0 10 10""><rect width=""10"" height=""10"" fill=""#ff0000""/></symbol></defs><use href=""#s"" width=""50"" height=""50""/>");

        Assert.NotEmpty(document.Root.Children);
        Assert.Contains("1 0 0 rg", Draw(@"<defs><symbol id=""s"" viewBox=""0 0 10 10""><rect width=""10"" height=""10"" fill=""#ff0000""/></symbol></defs><use href=""#s"" width=""50"" height=""50""/>"));
    }

    [Fact]
    public void References_that_point_at_each_other_end_instead_of_looping()
    {
        var document = Parse(@"
            <g id=""a""><use href=""#b""/></g>
            <g id=""b""><use href=""#a""/><rect width=""5"" height=""5"" fill=""#ff0000""/></g>");

        Assert.True(document.ShapeCount < 1000, $"a cycle must not explode, got {document.ShapeCount} nodes");
    }

    [Fact]
    public void Only_the_first_alternative_of_a_switch_is_drawn()
    {
        var page = Draw(@"<switch><rect width=""10"" height=""10"" fill=""#ff0000""/><rect width=""10"" height=""10"" fill=""#00ff00""/></switch>");

        Assert.Contains("1 0 0 rg", page);
        Assert.DoesNotContain("0 1 0 rg", page);
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    [Fact]
    public void A_filled_shape_writes_its_color_and_a_fill_operator()
    {
        var page = Draw(@"<rect x=""10"" y=""10"" width=""80"" height=""80"" fill=""#ff0000""/>");

        Assert.Contains("1 0 0 rg", page);
        Assert.Contains(" f\n", page);
    }

    [Fact]
    public void The_even_odd_rule_uses_the_starred_operator()
    {
        Assert.Contains(" f*", Draw(@"<path d=""M0 0H50V50H0Z M10 10H40V40H10Z"" fill=""#ff0000"" fill-rule=""evenodd""/>"));
        Assert.DoesNotContain(" f*", Draw(@"<path d=""M0 0H50V50H0Z"" fill=""#ff0000""/>"));
    }

    [Fact]
    public void Stroke_widths_are_scaled_with_the_drawing()
    {
        // A 100 unit wide drawing in a 100 point box keeps its width; in a 50 point box it halves.
        const string Line = @"<line x1=""0"" y1=""0"" x2=""100"" y2=""100"" stroke=""#000000"" stroke-width=""4""/>";
        var image = SvgImage.FromString(Markup(Line));

        Assert.Matches(@"(?m)^4 w$", Inspect(Generate(c => c.Width(100).Svg(image))).PageContents()[0]);
        Assert.Matches(@"(?m)^2 w$", Inspect(Generate(c => c.Width(50).Svg(SvgImage.FromString(Markup(Line))))).PageContents()[0]);
    }

    [Fact]
    public void Dashes_are_written_and_cleared_again_for_the_next_shape()
    {
        var page = Draw(@"
            <path d=""M0 10H100"" stroke=""#000000"" stroke-dasharray=""6 3""/>
            <path d=""M0 20H100"" stroke=""#000000""/>");

        Assert.Contains("[6 3] 0 d", page);
        Assert.Contains("[] 0 d", page);
        Assert.True(page.IndexOf("[6 3] 0 d", StringComparison.Ordinal) < page.IndexOf("[] 0 d", StringComparison.Ordinal));
    }

    [Fact]
    public void Line_caps_and_joins_are_written_once()
    {
        var page = Draw(@"
            <path d=""M0 10H100"" stroke=""#000000"" stroke-linecap=""round"" stroke-linejoin=""round""/>
            <path d=""M0 20H100"" stroke=""#000000"" stroke-linecap=""round"" stroke-linejoin=""round""/>");

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(page, @"1 J"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(page, @"1 j"));
    }

    [Fact]
    public void A_clip_path_clips_the_shapes_inside_it()
    {
        var page = Draw(@"
            <defs><clipPath id=""c""><circle cx=""50"" cy=""50"" r=""30""/></clipPath></defs>
            <g clip-path=""url(#c)""><rect width=""100"" height=""100"" fill=""#ff0000""/></g>");

        Assert.Contains(" W n", page);
        Assert.Contains("q ", page);
        Assert.Contains("Q", page);
    }

    [Fact]
    public void Opacity_becomes_a_graphics_state()
    {
        var page = Draw(@"<rect width=""100"" height=""100"" fill=""#ff0000"" opacity=""0.5""/>");

        Assert.Matches(@"/GS\d+ gs", page);
    }

    [Fact]
    public void A_gradient_fill_uses_a_shading_pattern()
    {
        var pdf = Inspect(Generate(c => c.Width(100).Svg(SvgImage.FromString(Markup(@"
            <linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>
            <rect width=""100"" height=""100"" fill=""url(#g)""/>")))));

        Assert.Contains("/Pattern cs", pdf.PageContents()[0]);
        Assert.Contains("/PatternType 2", pdf.Raw);
        Assert.Contains("/ShadingType 2", pdf.Raw);
    }

    [Fact]
    public void Drawings_are_scaled_into_the_space_they_are_given()
    {
        var image = SvgImage.FromString(Markup("", @"viewBox=""0 0 200 100"""));
        var context = new LayoutContext(new Canvas(new DocumentResources()));

        Assert.Equal(new Size(300, 150), Measure(image, ImageScaling.FitWidth, new Size(300, 400), context));
        Assert.Equal(new Size(200, 100), Measure(image, ImageScaling.FitHeight, new Size(300, 100), context));
        Assert.Equal(new Size(200, 100), Measure(image, ImageScaling.FitArea, new Size(300, 100), context));

        static Size Measure(SvgImage image, ImageScaling scaling, Size available, LayoutContext context)
        {
            var slot = new Slot();
            var descriptor = slot.Svg(image);
            _ = scaling switch
            {
                ImageScaling.FitHeight => descriptor.FitHeight(),
                ImageScaling.FitArea => descriptor.FitArea(),
                _ => descriptor.FitWidth(),
            };

            var plan = slot.Measure(available, context);
            return new Size(plan.Width, plan.Height);
        }
    }

    // ---- Hostile input ---------------------------------------------------------------------------

    [Theory]
    [InlineData("<html><body/></html>")]                       // not an SVG document
    [InlineData(@"<svg xmlns=""http://www.w3.org/2000/svg""><g></svg>")] // not well formed
    public void Markup_that_is_not_a_drawing_is_rejected(string markup) =>
        Assert.Throws<InvalidDataException>(() => SvgImage.FromString(markup));

    [Fact]
    public void An_empty_document_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => SvgImage.FromBytes([]));

    [Fact]
    public void Elements_nested_beyond_the_limit_are_rejected()
    {
        var deep = string.Concat(Enumerable.Repeat("<g>", 200)) + string.Concat(Enumerable.Repeat("</g>", 200));

        var exception = Assert.Throws<InvalidDataException>(() => SvgImage.FromString(Markup(deep)));
        Assert.Contains("nests deeper", exception.Message);
    }

    [Fact]
    public void A_document_type_declaration_is_skipped_rather_than_resolved()
    {
        // Exported files often carry the SVG 1.1 doctype; no entity of it is ever fetched.
        var image = SvgImage.FromString(
            @"<?xml version=""1.0""?><!DOCTYPE svg PUBLIC ""-//W3C//DTD SVG 1.1//EN"" ""http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd"">" +
            Markup(@"<rect width=""10"" height=""10"" fill=""#ff0000""/>"));

        Assert.Equal(1, image.ShapeCount);
    }

    // ---- Text, masks and patterns ----------------------------------------------------------------

    [Fact]
    public void Text_is_drawn_with_the_font_it_asks_for()
    {
        var pdf = Inspect(Generate(c => c.Width(200).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 200 60""><text x=""10"" y=""40"" font-size=""20"" fill=""#003366"">Merhaba</text></svg>"))));

        Assert.Equal("Merhaba", pdf.ExtractText().Trim());
        Assert.Contains("0 0.2 0.4 rg", pdf.PageContents()[0]);
    }

    [Fact]
    public void The_spans_of_a_piece_of_text_follow_one_another()
    {
        var pdf = Inspect(Generate(c => c.Width(300).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 300 60""><text x=""10"" y=""40"">bir <tspan font-weight=""bold"">iki</tspan> üç</text></svg>"))));

        // The spans are drawn in order, on one line, with the spaces between them kept.
        Assert.Equal("bir iki üç", pdf.ExtractText().Replace("\n", string.Empty, StringComparison.Ordinal));

        var runs = pdf.TextRuns();
        Assert.True(runs.Count >= 3);
        Assert.All(runs, run => Assert.Equal(runs[0].Y, run.Y, 1));
        for (var i = 1; i < runs.Count; i++)
            Assert.True(runs[i].X > runs[i - 1].X, "each span starts after the one before it");
    }

    [Fact]
    public void A_span_is_written_where_it_says_it_is()
    {
        var pdf = Inspect(Generate(c => c.Width(200).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 200 100""><text x=""10"" y=""20"">üst</text><text x=""10"" y=""80"">alt</text></svg>"))));

        var runs = pdf.TextRuns();
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Y > runs[1].Y, "the second line sits lower on the page");
    }

    [Theory]
    [InlineData("start", 0)]
    [InlineData("middle", -1)]
    [InlineData("end", -2)]
    public void Text_is_placed_by_what_it_is_anchored_at(string anchor, int order)
    {
        var pdf = Inspect(Generate(c => c.Width(200).Svg(SvgImage.FromString(
            $@"<svg viewBox=""0 0 200 60""><text x=""100"" y=""40"" text-anchor=""{anchor}"">metin</text></svg>"))));

        var x = pdf.TextRuns()[0].X;
        var start = 40 + 100;   // the left margin of the test page plus the position in the drawing

        switch (order)
        {
            case 0:
                Assert.True(Math.Abs(x - start) < 1, $"start: {x}");
                break;
            case -1:
                Assert.True(x < start - 5 && x > start - 40, $"middle: {x}");
                break;
            default:
                Assert.True(x < start - 20, $"end: {x}");
                break;
        }
    }

    [Fact]
    public void A_mask_hides_what_it_is_dark_over()
    {
        var pdf = Inspect(Generate(c => c.Width(200).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 100 100"">
                <defs><mask id=""m""><rect x=""0"" y=""0"" width=""50"" height=""100"" fill=""white""/></mask></defs>
                <rect width=""100"" height=""100"" fill=""#ff0000"" mask=""url(#m)""/>
              </svg>"))));

        // The mask is a form of its own, and the graphics state that applies it names it.
        Assert.Contains("/SMask<</Type/Mask/S/Luminosity", pdf.Raw);
        Assert.Contains("/Group<</Type/Group/S/Transparency", pdf.Raw);
        Assert.Matches(@"/GK1 gs", pdf.PageContents()[0]);
    }

    [Fact]
    public void A_pattern_fills_a_shape_with_a_drawing_that_repeats()
    {
        var pdf = Inspect(Generate(c => c.Width(200).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 100 100"">
                <defs><pattern id=""p"" width=""10"" height=""10"" patternUnits=""userSpaceOnUse"">
                  <rect width=""5"" height=""5"" fill=""#0000ff""/></pattern></defs>
                <rect width=""100"" height=""100"" fill=""url(#p)""/>
              </svg>"))));

        Assert.Contains("/PatternType 1", pdf.Raw);
        Assert.Contains("/XStep 10", pdf.Raw);
        Assert.Contains("/Pattern cs /Pt1 scn", pdf.PageContents()[0]);

        // The tile is clipped to its own box, so half a stroke on the edge cannot spill over.
        var tile = pdf.Streams().Single(stream => stream.Contains("0 0 1 rg"));
        Assert.Contains(" W n", tile);
    }

    [Fact]
    public void A_pattern_measured_in_the_box_of_the_shape_is_scaled_to_it()
    {
        var pdf = Inspect(Generate(c => c.Width(200).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 100 50"">
                <defs><pattern id=""p"" width=""0.25"" height=""0.5""><rect width=""4"" height=""4"" fill=""#00aa00""/></pattern></defs>
                <rect x=""0"" y=""0"" width=""100"" height=""50"" fill=""url(#p)""/>
              </svg>"))));

        // A quarter of the width and half the height of a 100 by 50 box, in the points the page uses.
        var step = System.Text.RegularExpressions.Regex.Match(pdf.Raw, @"/XStep ([\d.]+)/YStep ([\d.]+)");
        Assert.True(step.Success);
        Assert.Equal(25, float.Parse(step.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
        Assert.Equal(25, float.Parse(step.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
    }

    [Fact]
    public void Letters_that_are_spaced_apart_are_not_joined_into_ligatures()
    {
        // A browser does the same: the ligature would undo the spacing that was asked for.
        var spaced = Inspect(Generate(c => c.Width(300).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 300 40""><text x=""0"" y=""30"" letter-spacing=""3"">final</text></svg>"))));
        var plain = Inspect(Generate(c => c.Width(300).Svg(SvgImage.FromString(
            @"<svg viewBox=""0 0 300 40""><text x=""0"" y=""30"">final</text></svg>"))));

        Assert.Equal("final", spaced.ExtractText().Trim());
        Assert.Equal("final", plain.ExtractText().Trim());

        // Five glyphs when they are spaced apart, four when the font may join fi.
        Assert.True(Glyphs(spaced) > Glyphs(plain), $"{Glyphs(spaced)} vs {Glyphs(plain)}");

        static int Glyphs(PdfInspector pdf) =>
            System.Text.RegularExpressions.Regex.Matches(pdf.PageContents()[0], "<([0-9A-F]+)> Tj")
                .Sum(match => match.Groups[1].Value.Length / 4);
    }
}
