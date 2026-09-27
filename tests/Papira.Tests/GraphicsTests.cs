using System.Text.RegularExpressions;
using Papira.Infrastructure;
using Papira.Rendering;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

public class GraphicsTests
{
    private static LayoutContext NewContext() => new(new Canvas(new DocumentResources())) { PageNumber = 1 };

    private static SpacePlan Measure(Action<IContainer> content, float width = 400, float height = 300)
    {
        var slot = new Papira.Elements.Slot();
        content(slot);
        return slot.Measure(new Size(width, height), NewContext());
    }

    // ---- Transforms ------------------------------------------------------------------------------

    [Fact]
    public void Matrix_multiplication_applies_the_first_transform_first()
    {
        var move = Matrix.Translation(10, 0);
        var scale = Matrix.Scaling(2, 2);

        Assert.Equal((40, 0), Matrix.Multiply(move, scale).Apply(10, 0));  // (10+10)*2
        Assert.Equal((30, 0), Matrix.Multiply(scale, move).Apply(10, 0));  // 10*2+10
    }

    [Fact]
    public void Rotation_turns_clockwise_in_layout_coordinates()
    {
        var (x, y) = Matrix.Rotation(90).Apply(10, 0);
        Assert.Equal(0, x, 3);
        Assert.Equal(10, y, 3); // +x becomes +y, which points down on the page
    }

    [Fact]
    public void Quarter_turn_swaps_width_and_height()
    {
        var upright = Measure(c => c.Text("Başlık"));
        var turned = Measure(c => c.RotateRight().Text("Başlık"));

        Assert.Equal(upright.Width, turned.Height, 2);
        Assert.Equal(upright.Height, turned.Width, 2);
    }

    [Fact]
    public void Rotate_keeps_the_layout_size_and_writes_a_rotated_text_matrix()
    {
        var plan = Measure(c => c.Rotate(30).Text("Damga"));
        var upright = Measure(c => c.Text("Damga"));
        Assert.Equal(upright.Width, plan.Width, 2);

        var page = Inspect(Generate(c => c.Rotate(30).Text("Damga"))).PageContents()[0];
        var matrix = Regex.Match(page, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
        Assert.True(matrix.Success);
        Assert.NotEqual(0, float.Parse(matrix.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Scale_scales_the_layout_size()
    {
        var normal = Measure(c => c.Text("ölçek"));
        var scaled = Measure(c => c.Scale(2).Text("ölçek"));

        Assert.Equal(normal.Width * 2, scaled.Width, 1);
        Assert.Equal(normal.Height * 2, scaled.Height, 1);
    }

    // ---- Opacity and corners ---------------------------------------------------------------------

    [Fact]
    public void Opacity_writes_a_graphics_state()
    {
        var pdf = Inspect(Generate(c => c.Opacity(0.35f).Background(Colors.Green).Height(40)));

        Assert.Contains("/ExtGState<</GS1<</ca 0.35/CA 0.35>>>>", pdf.Raw);
        Assert.Contains("q /GS1 gs", pdf.PageContents()[0]);
        Assert.Contains("Q", pdf.PageContents()[0]);
    }

    [Fact]
    public void Full_opacity_does_not_create_a_graphics_state()
    {
        var pdf = Inspect(Generate(c => c.Opacity(1).Background(Colors.Green).Height(40)));
        Assert.DoesNotContain("/ExtGState", pdf.Raw);
    }

    [Fact]
    public void Corner_radius_rounds_the_background_and_clips_the_content()
    {
        var page = Inspect(Generate(c => c.CornerRadius(8).Background(Colors.Blue).Height(40))).PageContents()[0];

        Assert.Contains(" W n", page);           // clipping path
        Assert.True(Regex.Count(page, " c\n| c ") >= 8, "rounded corners are drawn with Bézier curves");
    }

    [Fact]
    public void Square_corners_use_the_rectangle_operator()
    {
        var page = Inspect(Generate(c => c.Background(Colors.Blue).Height(40))).PageContents()[0];
        Assert.Contains(" re f", page);
        Assert.DoesNotContain(" W n", page);
    }

    // ---- Gradients -------------------------------------------------------------------------------

    [Fact]
    public void Linear_gradient_writes_an_axial_shading_pattern()
    {
        var pdf = Inspect(Generate(c => c.BackgroundLinearGradient(90, Colors.Blue, Colors.Red).Height(50)));

        Assert.Contains("/Pattern cs /Sh1 scn", pdf.PageContents()[0]);
        Assert.Contains("/PatternType 2", pdf.Raw);
        Assert.Contains("/ShadingType 2", pdf.Raw);
        Assert.Contains("/FunctionType 2", pdf.Raw);
        Assert.Contains("/C1[0.898 0.224 0.208]", pdf.Raw); // ends in red
    }

    [Fact]
    public void Gradient_with_three_colors_stitches_two_segments()
    {
        var pdf = Inspect(Generate(c => c.BackgroundLinearGradient(0, Colors.Blue, Colors.White, Colors.Red).Height(50)));

        Assert.Contains("/FunctionType 3", pdf.Raw);
        Assert.Contains("/Bounds[0.5 ]", pdf.Raw);
        Assert.Equal(2, Regex.Count(pdf.Raw, "/FunctionType 2"));
    }

    [Fact]
    public void Radial_gradient_writes_a_radial_shading()
    {
        var pdf = Inspect(Generate(c => c.BackgroundRadialGradient(Colors.Yellow, Colors.Orange).Height(50)));
        Assert.Contains("/ShadingType 3", pdf.Raw);
    }

    [Fact]
    public void Gradients_need_at_least_two_colors()
    {
        Assert.Throws<ArgumentException>(() => Generate(c => c.BackgroundLinearGradient(0, Colors.Red)));
    }

    // ---- Vector drawing --------------------------------------------------------------------------

    [Fact]
    public void Canvas_writes_the_color_before_the_path()
    {
        var page = Inspect(Generate(c => c.Height(60).Canvas((canvas, w, h) =>
        {
            canvas.Rectangle(0, 0, w, h);
            canvas.Fill(Color.FromHex("#1E3A8A"));
        }))).PageContents()[0];

        // A path may not be interrupted by other operators before it is painted.
        Assert.Matches(@"0\.118 0\.227 0\.541 rg\n( -?[\d.]+ -?[\d.]+ [ml]){4} h f", page);
    }

    [Fact]
    public void Canvas_draws_curves_lines_and_strokes()
    {
        var page = Inspect(Generate(c => c.Height(60).Canvas((canvas, w, h) =>
        {
            canvas.Circle(w / 2, h / 2, 20);
            canvas.FillAndStroke(Colors.Yellow, Colors.Orange, 2);
            canvas.Line(0, 0, w, h, Colors.Red, 3);
        }))).PageContents()[0];

        Assert.Equal(4, Regex.Count(page, " c")); // a circle is four Bézier curves
        Assert.Contains(" B\n", page);            // fill and stroke
        Assert.Contains(" S\n", page);            // the line
        Assert.Contains("3 w", page);             // its width
    }

    [Fact]
    public void Canvas_element_fills_the_space_it_is_given()
    {
        var plan = Measure(c => c.Canvas((_, _, _) => { }), width: 200, height: 100);
        Assert.Equal(200, plan.Width, 2);
        Assert.Equal(100, plan.Height, 2);
    }

    [Fact]
    public void Graphics_output_stays_deterministic()
    {
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        byte[] Render() => Document.Create(d => d.Page(p => p.Content().Column(col =>
        {
            col.Item().Height(30).CornerRadius(5).BackgroundLinearGradient(45, Colors.Blue, Colors.Red);
            col.Item().Height(30).Opacity(0.5f).Background(Colors.Green);
            col.Item().Height(30).Rotate(15).Text("x");
        })))
            .WithMetadata(new DocumentMetadata { CreationDate = date })
            .GeneratePdf();

        Assert.Equal(Render(), Render());
    }
}
