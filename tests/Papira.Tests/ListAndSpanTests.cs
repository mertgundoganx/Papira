using Papira.Infrastructure;
using Papira.Rendering;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

public class ListAndSpanTests
{
    private static LayoutContext NewContext() => new(new Canvas(new DocumentResources())) { PageNumber = 1 };

    // ---- Lists -----------------------------------------------------------------------------------

    [Fact]
    public void Bulleted_list_puts_a_marker_before_each_item_on_the_same_line()
    {
        var runs = Inspect(Generate(c => c.List(list =>
        {
            list.Item().Text("Bir");
            list.Item().Text("İki");
        }))).TextRuns();

        Assert.Equal(["•", "Bir", "•", "İki"], runs.Select(r => r.Text));
        Assert.True(runs[0].X < runs[1].X, "marker must be left of the content");
        Assert.Equal(runs[0].Y, runs[1].Y, 2); // same baseline
        Assert.True(runs[2].Y < runs[0].Y, "second item must be below the first");
    }

    [Fact]
    public void Numbered_list_counts_from_the_start_number()
    {
        var pdf = Inspect(Generate(c => c.NumberedList(list =>
        {
            list.StartAt(9);
            list.Item().Text("dokuz");
            list.Item().Text("on");
        })));

        Assert.Equal("9.\ndokuz\n10.\non\n", pdf.ExtractText());
    }

    [Fact]
    public void List_markers_can_be_customized()
    {
        var pdf = Inspect(Generate(c => c.NumberedList(list =>
        {
            list.Marker(n => $"({(char)('a' + n - 1)})");
            list.Item().Text("x");
            list.Item().Text("y");
        })));

        Assert.Equal("(a)\nx\n(b)\ny\n", pdf.ExtractText());
    }

    [Fact]
    public void Nested_lists_are_indented()
    {
        var runs = Inspect(Generate(c => c.List(list =>
        {
            list.Item().Column(col =>
            {
                col.Item().Text("Dış");
                col.Item().List(inner => inner.Item().Text("İç"));
            });
        }))).TextRuns();

        var outer = runs.Single(r => r.Text == "Dış");
        var inner = runs.Single(r => r.Text == "İç");
        Assert.True(inner.X > outer.X + 10, "nested items must be indented");
    }

    [Fact]
    public void Marker_of_an_item_split_across_pages_is_drawn_once()
    {
        var text = string.Concat(Enumerable.Repeat("Uzun liste öğesi metni. ", 500));
        var pdf = Inspect(Generate(c => c.List(list => list.Item().Text(text))));

        Assert.True(pdf.PageCount > 1);
        Assert.Single(pdf.TextRuns(), r => r.Text == "•");
    }

    // ---- Row spans -------------------------------------------------------------------------------

    private static void ThreeColumns(TableDescriptor table) => table.ColumnsDefinition(c =>
    {
        c.RelativeColumn();
        c.RelativeColumn();
        c.RelativeColumn();
    });

    [Fact]
    public void Cells_after_a_row_span_skip_the_occupied_column()
    {
        var runs = Inspect(Generate(c => c.Table(t =>
        {
            ThreeColumns(t);
            t.Cell().RowSpan(2).Text("A");
            t.Cell().Text("B");
            t.Cell().Text("C");
            t.Cell().Text("D");
            t.Cell().Text("E");
        }))).TextRuns().ToDictionary(r => r.Text);

        Assert.Equal(runs["B"].X, runs["D"].X, 2); // D moves to column 2, below B
        Assert.Equal(runs["C"].X, runs["E"].X, 2);
        Assert.True(runs["D"].Y < runs["B"].Y);
        Assert.True(runs["A"].X < runs["B"].X);
    }

    [Fact]
    public void Tall_spanning_cell_grows_its_last_row()
    {
        var table = new TableDescriptor();
        ThreeColumns(table);
        table.Cell().RowSpan(2).Height(100);
        table.Cell().Text("B");
        table.Cell().Text("C");
        table.Cell().Text("D");
        table.Cell().Text("E");
        table.Cell().Text("F: next group");

        var plan = table.Element.Measure(new Size(300, 1000), NewContext());

        // The spanning cell forces rows 1 and 2 to 100pt together; the third row adds one line.
        Assert.Equal(SpacePlanKind.Full, plan.Kind);
        Assert.InRange(plan.Height, 110, 120);
    }

    [Fact]
    public void Rows_connected_by_a_span_move_to_the_next_page_together()
    {
        var runs = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(300, 200).Margin(10);
            p.Content().Column(col =>
            {
                col.Item().Height(160).Background(Colors.Grey.Lighten3); // visible, so page 1 has content
                col.Item().Table(t =>
                {
                    ThreeColumns(t);
                    t.Cell().RowSpan(2).Text("A");
                    t.Cell().Text("B");
                    t.Cell().Text("C");
                    t.Cell().Text("D");
                    t.Cell().Text("E");
                });
            });
        })).GeneratePdf()).TextRuns();

        // 20pt remain below the filler: one row (about 14.4pt) would fit, the two-row group doesn't.
        Assert.All(runs, run => Assert.Equal(1, run.Page));
    }

    [Fact]
    public void Row_span_group_taller_than_a_page_fails_with_a_layout_error()
    {
        Assert.Throws<DocumentLayoutException>(() => Document.Create(d => d.Page(p =>
        {
            p.Size(300, 200).Margin(10);
            p.Content().Table(t =>
            {
                ThreeColumns(t);
                t.Cell().RowSpan(2).Height(500);
                t.Cell().Text("B");
            });
        })).GeneratePdf());
    }

    [Fact]
    public void Cell_can_span_rows_and_columns()
    {
        var runs = Inspect(Generate(c => c.Table(t =>
        {
            ThreeColumns(t);
            t.Cell().RowSpan(2).ColumnSpan(2).Text("AB");
            t.Cell().Text("C");
            t.Cell().Text("F");
            t.Cell().Text("G");
        }))).TextRuns().ToDictionary(r => r.Text);

        Assert.Equal(runs["C"].X, runs["F"].X, 2); // F goes to column 3 of row 2
        Assert.True(runs["F"].Y < runs["C"].Y);
        Assert.Equal(runs["AB"].X, runs["G"].X, 2); // G starts the third row
    }
}
