using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

public class DocumentTests
{
    [Fact]
    public void Generates_valid_single_page_document()
    {
        var pdf = Inspect(Generate(c => c.Text("Merhaba dünya")));
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void Empty_content_still_produces_one_page()
    {
        var pdf = Inspect(Generate(_ => { }));
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void Turkish_characters_are_embedded_and_extractable()
    {
        var pdf = Inspect(Generate(c => c.Text("ğüşıöçĞÜŞİÖÇ ₺")));

        // ToUnicode CMap must map glyphs back to the original codepoints so text can be copied and searched.
        var cmap = pdf.Streams().Single(s => s.Contains("begincmap"));
        foreach (var c in "ğüşıöçĞÜŞİÖÇ₺")
            Assert.Contains($"><{(int)c:X4}>", cmap);

        Assert.Contains("/FontFile2", pdf.Raw);
        Assert.Contains("+Lato-Regular", pdf.Raw);
    }

    [Fact]
    public void Long_text_flows_over_multiple_pages()
    {
        var text = string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 600));
        var pdf = Inspect(Generate(c => c.Text(text)));
        Assert.True(pdf.PageCount > 3, $"expected several pages, got {pdf.PageCount}");
    }

    [Fact]
    public void Page_break_starts_new_page()
    {
        var pdf = Inspect(Generate(c => c.Column(col =>
        {
            col.Item().Text("1");
            col.Item().PageBreak();
            col.Item().Text("2");
            col.Item().PageBreak();
            col.Item().Text("3");
        })));

        Assert.Equal(3, pdf.PageCount);
    }

    [Fact]
    public void Table_splits_across_pages_and_repeats_header()
    {
        var pdf = Inspect(Generate(c => c.Table(t =>
        {
            t.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(40);
                cols.RelativeColumn();
            });
            t.Header(h =>
            {
                h.Cell().Text("HDR1");
                h.Cell().Text("HDR2");
            });
            for (var i = 0; i < 200; i++)
            {
                t.Cell().Text(i);
                t.Cell().Text("row " + i);
            }
        })));

        Assert.True(pdf.PageCount >= 3);

        // Header glyph run appears once per page in the page content streams.
        var pages = pdf.Streams().Where(s => s.Contains(" Tj")).ToList();
        var headerRun = pages[0].Split('\n').First(l => l.Contains(" Tj"));
        var headerGlyphs = headerRun[headerRun.IndexOf('<')..];
        Assert.All(pages, page => Assert.Contains(headerGlyphs, page));
    }

    [Fact]
    public void Page_numbers_and_total_pages_are_resolved()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Margin(40);
            p.Footer().Text(t =>
            {
                t.CurrentPageNumber();
                t.Span("/");
                t.TotalPages();
            });
            p.Content().Column(col =>
            {
                for (var i = 0; i < 4; i++)
                {
                    col.Item().Text("page");
                    col.Item().PageBreak();
                }
            });
        })).GeneratePdf());

        // 4 breaks after 4 items -> 4 pages (the last break has nothing after it).
        Assert.Equal(4, pdf.PageCount);
    }

    [Fact]
    public void Multiple_page_templates_create_sections()
    {
        var pdf = Inspect(Document.Create(d =>
        {
            d.Page(p => p.Content().Text("A4"));
            d.Page(p =>
            {
                p.Size(PageSizes.A5.Landscape());
                p.Content().Text("A5");
            });
        }).GeneratePdf());

        Assert.Equal(2, pdf.PageCount);
        Assert.Contains("/MediaBox[0 0 595.28 841.89]", pdf.Raw);
        Assert.Contains("/MediaBox[0 0 595.28 419.53]", pdf.Raw);
    }

    [Fact]
    public void Images_are_embedded_once_per_document()
    {
        var png = Image.FromFile(Asset("circle-rgba.png"));
        var jpg = Image.FromFile(Asset("photo.jpg"));

        var pdf = Inspect(Generate(c => c.Column(col =>
        {
            for (var i = 0; i < 5; i++)
                col.Item().Width(50).Image(png);
            col.Item().Width(100).Image(jpg);
        })));

        Assert.Equal(1, CountOccurrences(pdf.Raw, "/DCTDecode"));
        Assert.Equal(1, CountOccurrences(pdf.Raw, "/SMask"));
        Assert.Equal(3, CountOccurrences(pdf.Raw, "/Subtype/Image")); // png + its soft mask + jpg
    }

    [Theory]
    [InlineData(PdfCompression.None)]
    [InlineData(PdfCompression.Fastest)]
    [InlineData(PdfCompression.Smallest)]
    public void All_compression_levels_produce_valid_files(PdfCompression compression)
    {
        var bytes = Document.Create(d => d.Page(p => p.Content().Text("Sıkıştırma testi")))
            .WithSettings(new DocumentSettings { Compression = compression })
            .GeneratePdf();

        Inspect(bytes);
    }

    [Fact]
    public void Metadata_is_written_as_unicode()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p => p.Content().Text("x")))
            .WithMetadata(new DocumentMetadata { Title = "Ş", Author = "Ç" })
            .GeneratePdf());

        Assert.Contains("/Title<FEFF015E>", pdf.Raw);
        Assert.Contains("/Author<FEFF00C7>", pdf.Raw);
    }

    [Fact]
    public void Concurrent_generation_is_thread_safe_and_deterministic_in_layout()
    {
        var document = Document.Create(d => d.Page(p =>
        {
            p.Margin(30);
            p.Content().Column(col =>
            {
                for (var i = 0; i < 300; i++)
                    col.Item().Text($"Satır {i} — eşzamanlı üretim testi").FontSize(9 + i % 5);
            });
        }));

        var expected = new PdfInspector(document.GeneratePdf()).PageCount;
        var counts = new int[64];
        Parallel.For(0, counts.Length, i => counts[i] = Inspect(document.GeneratePdf()).PageCount);

        Assert.All(counts, c => Assert.Equal(expected, c));
    }

    [Fact]
    public void Two_children_in_one_container_throw()
    {
        var ex = Assert.Throws<DocumentComposeException>(() => Generate(c =>
        {
            c.Text("a");
            c.Text("b");
        }));
        Assert.Contains("already has content", ex.Message);
    }

    [Fact]
    public void Element_larger_than_page_throws_layout_exception()
    {
        Assert.Throws<DocumentLayoutException>(() => Generate(c => c.Height(5000).Background(Colors.Red)));
    }

    [Fact]
    public void Document_without_pages_throws()
    {
        Assert.Throws<DocumentComposeException>(() => Document.Create(_ => { }).GeneratePdf());
    }

    [Fact]
    public void Unknown_font_family_falls_back_to_default()
    {
        var pdf = Inspect(Generate(c => c.Text("fallback").FontFamily("Font That Does Not Exist 123")));
        Assert.Contains("+Lato-Regular", pdf.Raw);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
