using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>Layout, robustness and output issues found in review; each test reproduces the original failure.</summary>
public class RegressionTests
{
    private static byte[] LatoRegular()
    {
        using var stream = typeof(FontManager).Assembly.GetManifestResourceStream("Papira.Fonts.Lato-Regular.ttf")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static IEnumerable<int> WordNumbers(string text) =>
        Regex.Matches(text, @"w(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

    // ---- Layout ----------------------------------------------------------------------------------

    [Fact]
    public void Text_in_row_keeps_every_word_when_column_width_changes_between_pages()
    {
        // The auto item finishes on page 1, so the relative item gets wider on later pages.
        var words = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"w{i}"));
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(400, 300).Margin(10);
            p.Content().Row(row =>
            {
                row.AutoItem().Text("LABEL:");
                row.RelativeItem().Text(words);
            });
        })).GeneratePdf());

        Assert.True(pdf.PageCount > 1);
        Assert.Equal(Enumerable.Range(0, 400), WordNumbers(pdf.ExtractText()));
    }

    [Fact]
    public void Text_split_across_pages_keeps_every_word_once()
    {
        var words = string.Join(' ', Enumerable.Range(0, 3000).Select(i => $"w{i}"));
        var pdf = Inspect(Generate(c => c.Text(words)));

        Assert.True(pdf.PageCount > 2);
        Assert.Equal(Enumerable.Range(0, 3000), WordNumbers(pdf.ExtractText()));
    }

    [Fact]
    public void Split_table_row_keeps_borders_of_finished_cells()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(400, 300).Margin(10);
            p.Content().Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.RelativeColumn();
                });
                t.Cell().Border(1).Padding(4).Text("short");
                t.Cell().Border(1).Padding(4).Text(string.Concat(Enumerable.Repeat("long text ", 600)));
            });
        })).GeneratePdf());

        var pages = pdf.PageContents();
        Assert.True(pages.Count > 1);

        // Two bordered cells => 8 border rectangles on every page the row spans.
        Assert.All(pages, page => Assert.True(Regex.Count(page, " re f") >= 8, "a continuation page is missing cell borders"));
    }

    [Fact]
    public void Image_in_finished_row_item_is_not_repeated_on_continuation_pages()
    {
        var image = Image.FromFile(Asset("circle-rgba.png"));
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(400, 300).Margin(10);
            p.Content().Row(row =>
            {
                row.ConstantItem(60).Image(image);
                row.ConstantItem(60).LineHorizontal(3);
                row.RelativeItem().Text(string.Concat(Enumerable.Repeat("long text ", 600)));
            });
        })).GeneratePdf());

        var pages = pdf.PageContents();
        Assert.True(pages.Count > 1);
        Assert.Equal(1, pages.Sum(page => Regex.Count(page, " Do Q")));
        Assert.All(pages.Skip(1), page => Assert.DoesNotContain(" re f", page));
    }

    [Fact]
    public void Split_row_keeps_border_placed_inside_alignment()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(400, 300).Margin(10);
            p.Content().Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.RelativeColumn();
                });
                t.Cell().AlignMiddle().Border(1).Text("short");
                t.Cell().Border(1).Text(string.Concat(Enumerable.Repeat("long text ", 600)));
            });
        })).GeneratePdf());

        Assert.All(pdf.PageContents(), page => Assert.True(Regex.Count(page, " re f") >= 8));
    }

    [Fact]
    public void EnsureSpace_keeps_working_after_a_page_that_needed_the_full_height()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(300, 160).Margin(10); // body is 140pt, less than the default EnsureSpace of 150pt
            p.Content().Column(col =>
            {
                col.Item().EnsureSpace().Text("H1");
                col.Item().Height(100).Background(Colors.Grey.Lighten2);
                col.Item().EnsureSpace(30).Text("H2");
                col.Item().Height(30).Background(Colors.Grey.Lighten2);
            });
        })).GeneratePdf());

        var pages = pdf.PageContents();
        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain(" Tj", pages[0][pages[0].IndexOf("re f", StringComparison.Ordinal)..]); // H2 not orphaned at the bottom of page 1
        Assert.Equal("H1\nH2\n", pdf.ExtractText());
    }

    [Fact]
    public void Table_with_header_and_no_rows_still_renders_header()
    {
        var pdf = Inspect(Generate(c => c.Column(col =>
        {
            col.Item().Text("before");
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cols => cols.RelativeColumn());
                t.Header(h => h.Cell().Text("HEADER"));
            });
            col.Item().Text("after");
        })));

        Assert.Equal("before\nHEADER\nafter\n", pdf.ExtractText());
    }

    [Fact]
    public void Image_that_would_shrink_to_nothing_moves_to_next_page()
    {
        var image = Image.FromFile(Asset("circle-rgba.png"));
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(300, 300).Margin(10);
            p.Content().Column(col =>
            {
                col.Item().Height(280).Background(Colors.Grey.Lighten2);
                col.Item().Image(image).FitHeight();
            });
        })).GeneratePdf());

        Assert.Equal(2, pdf.PageCount);
        Assert.DoesNotContain("q 0 0 0 0 ", pdf.Raw);
        Assert.Contains(" Do Q", pdf.PageContents()[1]);
    }

    [Fact]
    public void Fixed_size_box_without_content_draws_its_background_and_border()
    {
        var pdf = Inspect(Generate(c => c.Height(20).Background(Colors.Red).Border(1)));
        Assert.Contains("0.898 0.224 0.208 rg", pdf.PageContents()[0]);
        Assert.Equal(5, Regex.Count(pdf.PageContents()[0], " re f")); // background + 4 border edges
    }

    [Fact]
    public void EnsureSpace_larger_than_page_does_not_fail_layout()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(300, 160).Margin(10);
            p.Content().Column(col =>
            {
                col.Item().EnsureSpace(500).Text("Heading");
                col.Item().Height(100).EnsureSpace(150).Text("Boxed");
            });
        })).GeneratePdf());

        Assert.Contains("Heading", pdf.ExtractText());
        Assert.Contains("Boxed", pdf.ExtractText());
    }

    [Fact]
    public void EnsureSpace_moves_heading_to_next_page()
    {
        var pdf = Inspect(Document.Create(d => d.Page(p =>
        {
            p.Size(300, 300).Margin(10);
            p.Content().Column(col =>
            {
                col.Item().Height(240).Background(Colors.Grey.Lighten2);
                col.Item().EnsureSpace(100).Text("Heading");
            });
        })).GeneratePdf());

        Assert.Equal(2, pdf.PageCount);
        Assert.DoesNotContain(" Tj", pdf.PageContents()[0]);
    }

    // ---- PDF output ------------------------------------------------------------------------------

    [Fact]
    public void Characters_missing_from_font_do_not_get_a_unicode_mapping()
    {
        var pdf = Inspect(Generate(c => c.Text("A中文B")));
        var cmap = pdf.Streams().Single(s => s.Contains("begincmap"));
        var mappings = cmap[cmap.IndexOf("beginbfchar", StringComparison.Ordinal)..];
        Assert.DoesNotContain("<0000>", mappings);
        Assert.Equal("A\uFFFD\uFFFDB\n", pdf.ExtractText());
    }

    [Fact]
    public void Output_is_deterministic_for_identical_input()
    {
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        byte[] Render() => Document.Create(d => d.Page(p => p.Content().Text("Aynı girdi, aynı çıktı")))
            .WithMetadata(new DocumentMetadata { CreationDate = date })
            .GeneratePdf();

        Assert.Equal(Render(), Render());
    }

    [Fact]
    public void Errors_in_parallel_output_stage_keep_their_type()
    {
        var png = BuildPng(2, 2, colorType: 6, bitDepth: 8, idat: [1, 2, 3]); // corrupt zlib stream
        var image = Image.FromBytes(png);

        var ex = Record.Exception(() => Document.Create(d => d.Page(p => p.Content().Column(col =>
            {
                col.Item().Width(50).Image(image);
                col.Item().Text("more work items so the parallel path is used");
            })))
            .WithSettings(new DocumentSettings { MaxDegreeOfParallelism = 4 })
            .GeneratePdf());

        Assert.NotNull(ex);
        Assert.IsNotType<AggregateException>(ex);
    }

    // ---- Untrusted input ---------------------------------------------------------------------------

    [Fact]
    public void Png_decompression_bomb_is_bounded()
    {
        // 1x1 RGBA image whose IDAT inflates to 64 MB.
        var bomb = BuildPng(1, 1, colorType: 6, bitDepth: 8, raw: new byte[64 * 1024 * 1024]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var encoded = Image.FromBytes(bomb).Encoded;

        Assert.Equal(1, encoded.Width);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024, "decoder inflated far more than the image needs");
    }

    [Theory]
    [InlineData(50_000, 50_000, 6, 8)] // too many pixels
    [InlineData(10, 10, 6, 0)]         // invalid bit depth
    [InlineData(10, 10, 5, 8)]         // invalid color type
    [InlineData(10, 10, 3, 8)]         // indexed without palette
    [InlineData(0, 10, 2, 8)]          // zero width
    public void Invalid_png_headers_are_rejected(int width, int height, int colorType, int bitDepth)
    {
        var png = BuildPng(width, height, colorType, bitDepth, idat: [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Assert.Throws<InvalidDataException>(() => Image.FromBytes(png));
    }

    [Fact]
    public void Unsupported_jpeg_variants_are_rejected_up_front()
    {
        // SOF3 (lossless) frame header.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC3, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9];
        Assert.Throws<NotSupportedException>(() => Image.FromBytes(jpeg));
    }

    [Fact]
    public void Garbage_font_data_is_rejected_with_invalid_data_exception()
    {
        var random = new Random(42);
        var garbage = new byte[4096];
        random.NextBytes(garbage);
        garbage[0] = 0; garbage[1] = 1; garbage[2] = 0; garbage[3] = 0; // TrueType signature

        Assert.Throws<InvalidDataException>(() => FontManager.RegisterFontWithCustomName("Garbage", garbage));
        Assert.Throws<InvalidDataException>(() => FontManager.RegisterFontWithCustomName("Truncated", LatoRegular()[..2000]));
    }

    [Fact]
    public void Font_collection_with_absurd_face_count_is_rejected()
    {
        var ttc = new byte[64];
        "ttcf"u8.CopyTo(ttc);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(8), 0x7FFFFFFF);
        Assert.Throws<InvalidDataException>(() => FontManager.RegisterFont(ttc));
    }

    [Fact]
    public void Corrupt_loca_does_not_cause_huge_allocations()
    {
        var font = LatoRegular();
        var (loca, _) = FindTable(font, "loca");
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(loca + 4 * 36), 0x7FFFFFF0); // glyph 36 ('A' in Lato) ends far outside 'glyf'

        FontManager.RegisterFontWithCustomName("CorruptLoca", font);
        var pdf = Inspect(Generate(c => c.Text("ABC").FontFamily("CorruptLoca")));
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void Registering_fonts_while_rendering_is_thread_safe()
    {
        var font = LatoRegular();
        var errors = 0;

        // Dedicated threads and fixed iteration counts: the test must not depend on thread-pool timing,
        // which is unreliable on machines with few cores.
        var threads = new List<Thread>
        {
            new(() =>
            {
                for (var i = 0; i < 100; i++)
                    FontManager.RegisterFontWithCustomName("Race", font);
            }),
        };

        for (var t = 0; t < 4; t++)
        {
            threads.Add(new Thread(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    try
                    {
                        Generate(c => c.Text("race").FontFamily("Race"));
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }));
        }

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        Assert.Equal(0, errors);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static (int Offset, int Length) FindTable(byte[] font, string tag)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == tag)
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12)));
        }

        throw new InvalidOperationException($"Table {tag} not found.");
    }

    /// <summary>Builds a PNG; either <paramref name="raw"/> (compressed here) or a ready <paramref name="idat"/>.</summary>
    private static byte[] BuildPng(int width, int height, int colorType, int bitDepth, byte[]? raw = null, byte[]? idat = null)
    {
        if (raw != null)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(raw);
            idat = compressed.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", idat!);
        WriteChunk(png, "IEND", []);
        return png.ToArray();

        static void WriteChunk(Stream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
            stream.Write(data);
            stream.Write(new byte[4]); // CRC is not verified by the decoder
        }
    }
}
