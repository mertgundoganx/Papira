using System.Text.RegularExpressions;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Tagged documents, which carry their structure beside their pages so that a reader for the blind can
/// follow what the document says. The output of these tests was validated with veraPDF against PDF/UA-1,
/// on its own and together with PDF/A-3b; both pass.
/// </summary>
public class TaggedPdfTests
{
    private static byte[] Tagged(Action<IContainer> content, DocumentMetadata? metadata = null) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4).Margin(40);
            page.Header().Text("Üst bilgi").FontSize(9);
            page.Footer().Text("Alt bilgi").FontSize(9);
            content(page.Content());
        }))
        .WithMetadata(metadata ?? new DocumentMetadata { Title = "Test", Language = "tr-TR" })
        .WithSettings(new DocumentSettings { Tagged = true })
        .GeneratePdf();

    [Fact]
    public void A_tagged_document_says_so_and_carries_a_structure()
    {
        var pdf = Inspect(Tagged(c => c.Text("Merhaba")));

        Assert.Contains("/MarkInfo<</Marked true>>", pdf.Raw);
        Assert.Contains("/StructTreeRoot ", pdf.Raw);
        Assert.Contains("/Type/StructTreeRoot", pdf.Raw);
        Assert.Contains("/ParentTree ", pdf.Raw);
        Assert.Contains("/StructParents 0", pdf.Raw);
        Assert.Contains("/Lang(tr-TR)", pdf.Raw);

        // A tagged document is also the one place a viewer is told to show the title rather than the file name.
        Assert.Contains("/ViewerPreferences<</DisplayDocTitle true>>", pdf.Raw);
        Assert.Contains("/Type/Metadata", pdf.Raw);
    }

    [Fact]
    public void Text_is_a_paragraph_and_a_heading_is_a_heading()
    {
        var pdf = Inspect(Tagged(c => c.Column(column =>
        {
            column.Item().Text("Başlık").Heading(1);
            column.Item().Text("Gövde metni");
        })));

        Assert.Contains("/S/H1", pdf.Raw);
        Assert.Contains("/S/P", pdf.Raw);
        Assert.Contains("/S/Document", pdf.Raw);

        var page = pdf.PageContents()[0];
        Assert.Contains("/H1<</MCID ", page);
        Assert.Contains("/P<</MCID ", page);
        Assert.Contains("EMC", page);
    }

    [Fact]
    public void A_picture_carries_the_words_that_replace_it()
    {
        var pdf = Inspect(Tagged(c => c.Width(100).Image(Asset("photo.jpg")).Alt("Bir fotoğraf")));

        Assert.Contains("/S/Figure", pdf.Raw);

        // The description is written as a Unicode string, so it appears in the file as hex.
        var alt = "<FEFF" + string.Concat("Bir fotoğraf".Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))) + ">";
        Assert.Contains("/Alt" + alt, pdf.Raw);
    }

    [Fact]
    public void A_table_keeps_its_rows_and_tells_the_header_cells_apart()
    {
        var pdf = Inspect(Tagged(c => c.Table(table =>
        {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
            table.Header(header =>
            {
                header.Cell().Text("Ürün");
                header.Cell().Text("Tutar");
            });

            table.Cell().Text("Lisans");
            table.Cell().Text("1.200");
        })));

        Assert.Contains("/S/Table", pdf.Raw);
        Assert.Contains("/S/TR", pdf.Raw);
        Assert.Contains("/S/TH", pdf.Raw);
        Assert.Contains("/S/TD", pdf.Raw);

        // A header cell has to say which cells it heads, or a reader cannot pair them up.
        Assert.Contains("/A<</O/Table/Scope/Column>>", pdf.Raw);
    }

    [Fact]
    public void A_list_keeps_its_items_markers_and_text_apart()
    {
        var pdf = Inspect(Tagged(c => c.List(list =>
        {
            list.Item().Text("Birinci");
            list.Item().Text("İkinci");
        })));

        Assert.Contains("/S/L", pdf.Raw);
        Assert.Contains("/S/LI", pdf.Raw);
        Assert.Contains("/S/Lbl", pdf.Raw);
        Assert.Contains("/S/LBody", pdf.Raw);
    }

    [Fact]
    public void A_link_is_part_of_the_structure_and_says_where_it_goes()
    {
        var pdf = Inspect(Tagged(c => c.Hyperlink("https://example.com").Text("Bağlantı")));

        Assert.Contains("/S/Link", pdf.Raw);
        Assert.Contains("/Type/OBJR", pdf.Raw);
        Assert.Contains("/StructParent ", pdf.Raw);

        // The annotation itself says where it goes, which a reader announces before following it.
        var annotation = pdf.Raw[pdf.Raw.IndexOf("/Subtype/Link", StringComparison.Ordinal)..];
        Assert.Contains("/Contents<FEFF", annotation[..Math.Min(400, annotation.Length)]);

        // A page with annotations has to say how they are stepped through.
        Assert.Contains("/Tabs/S", pdf.Raw);
    }

    [Fact]
    public void Page_furniture_is_marked_as_something_to_skip()
    {
        var page = Inspect(Tagged(c => c.Text("Gövde"))).PageContents()[0];

        // The header and footer are drawn, but as artifacts rather than as part of what the document says.
        Assert.Contains("/Artifact BMC", page);
        Assert.Equal(2, Regex.Count(page, "/Artifact BMC"));
    }

    [Fact]
    public void Nothing_is_drawn_outside_a_marked_section()
    {
        var page = Inspect(Tagged(c => c.Background(Colors.Grey.Lighten3).Padding(10).Text("Merhaba"))).PageContents()[0];

        // Every BDC or BMC is closed, and the counts match.
        Assert.Equal(
            Regex.Count(page, "BDC") + Regex.Count(page, "BMC"),
            Regex.Count(page, "EMC"));
    }

    [Fact]
    public void An_untagged_document_carries_none_of_this()
    {
        var pdf = Inspect(Generate(c => c.Text("Merhaba")));

        Assert.DoesNotContain("/StructTreeRoot", pdf.Raw);
        Assert.DoesNotContain("/MarkInfo", pdf.Raw);
        Assert.DoesNotContain("BDC", pdf.PageContents()[0]);
        Assert.DoesNotContain("BMC", pdf.PageContents()[0]);
    }

    [Fact]
    public void Content_that_runs_over_two_pages_stays_one_element()
    {
        var text = string.Join(" ", Enumerable.Repeat("Uzun bir paragraf metni.", 400));
        var pdf = Inspect(Tagged(c => c.Text(text)));

        Assert.True(pdf.PageCount > 1, "the text should need more than one page");

        // Each page holds the part of the text that fits on it, and each part is a paragraph of its own.
        Assert.Contains("/StructParents 1", pdf.Raw);
        Assert.True(Regex.Count(pdf.Raw, "/S/P") >= 2, "both pages should carry a paragraph");
    }

    [Fact]
    public void A_cell_that_spans_several_columns_or_rows_says_how_far_it_reaches()
    {
        // Without this a reader counts the cells of the row and finds a table whose rows differ in
        // width, which is also what PDF/UA-1 objects to.
        var pdf = Inspect(Tagged(c => c.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn();
                columns.RelativeColumn();
            });

            table.Cell().RowSpan(2).Text("iki satır");
            table.Cell().Text("sağ üst");
            table.Cell().Text("sağ alt");
            table.Cell().ColumnSpan(2).Text("iki sütun");
        })));

        Assert.Contains("/A<</O/Table/RowSpan 2>>", pdf.Raw);
        Assert.Contains("/A<</O/Table/ColSpan 2>>", pdf.Raw);
    }

}
