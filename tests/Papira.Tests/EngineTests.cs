using Papira.Elements;
using Papira.Fonts;
using Papira.Images;
using Papira.Infrastructure;
using Papira.Pdf;
using Papira.Rendering;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

public class EngineTests
{
    private static LayoutContext NewContext() => new(new Canvas(new DocumentResources())) { PageNumber = 1 };

    private static TrueTypeFont Lato => FontManager.Resolve("Lato", FontWeight.Normal, false).Font;

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-2.25, "-2.25")]
    [InlineData(595.2756, "595.276")]
    [InlineData(0.0004, "0")]
    [InlineData(1e9, "1000000000")]
    public void Real_numbers_are_formatted_without_exponent(double value, string expected)
    {
        using var buffer = new ByteBuffer();
        buffer.Real(value);
        Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(buffer.Span));
    }

    [Fact]
    public void Bundled_font_maps_turkish_characters()
    {
        foreach (var c in "ğüşıöçĞÜŞİÖÇ₺€")
            Assert.NotEqual(0, Lato.GetGlyph(c));
        Assert.Equal(2000, Lato.UnitsPerEm);
    }

    [Fact]
    public void Subset_font_is_parseable_and_keeps_glyph_ids()
    {
        var font = Lato;
        var used = "Papira ğü".Select(c => font.GetGlyph(c)).ToArray();
        var subset = FontSubsetter.Subset(font, used);

        Assert.True(subset.Length < font.Data.Length / 4, "subset should be much smaller than the font");

        var parsed = TrueTypeFont.Load(AddEmptyCmap(subset));
        Assert.Equal(font.GlyphCount, parsed.GlyphCount);
        foreach (var glyph in used)
            Assert.Equal(font.GetAdvance(glyph), parsed.GetAdvance(glyph));
    }

    [Fact]
    public void Text_wraps_at_word_boundaries()
    {
        var element = new TextElement();
        element.Spans.Add(new TextSpan { Text = "aaa bbb ccc ddd" });
        var context = NewContext();

        var single = element.Measure(new Size(1000, 1000), context);
        var narrow = element.Measure(new Size(single.Width * 0.6f, 1000), context);

        Assert.Equal(SpacePlanKind.Full, narrow.Kind);
        Assert.True(narrow.Height > single.Height * 1.9f, "text should wrap onto at least two lines");
        Assert.True(narrow.Width <= single.Width * 0.6f + 0.01f);
    }

    [Fact]
    public void Text_that_does_not_fit_is_partial_then_continues()
    {
        var element = new TextElement();
        element.Spans.Add(new TextSpan { Text = "line1\nline2\nline3\nline4" });
        var context = NewContext();
        using var buffer = new ByteBuffer();
        context.Canvas.BeginPage(buffer, 800);

        var full = element.Measure(new Size(500, 1000), context);
        var lineHeight = full.Height / 4;

        var plan = element.Measure(new Size(500, lineHeight * 2.5f), context);
        Assert.Equal(SpacePlanKind.Partial, plan.Kind);
        Assert.Equal(lineHeight * 2, plan.Height, 3);

        element.Draw(new Size(500, lineHeight * 2.5f), context);
        var rest = element.Measure(new Size(500, 1000), context);
        Assert.Equal(SpacePlanKind.Full, rest.Kind);
        Assert.Equal(lineHeight * 2, rest.Height, 3);

        element.Draw(new Size(500, 1000), context);
        Assert.Equal(SpacePlanKind.Empty, element.Measure(new Size(500, 1000), context).Kind);
    }

    [Fact]
    public void Text_too_tall_for_one_line_wraps_to_next_page()
    {
        var element = new TextElement();
        element.Spans.Add(new TextSpan { Text = "x", Style = TextStyle.Default.FontSize(40) });
        Assert.Equal(SpacePlanKind.Wrap, element.Measure(new Size(500, 10), NewContext()).Kind);
    }

    [Fact]
    public void Row_distributes_relative_and_constant_widths()
    {
        var row = new RowDescriptor();
        row.ConstantItem(100).Height(10);
        row.RelativeItem(1).Height(10);
        row.RelativeItem(3).Height(10);

        var plan = row.Element.Measure(new Size(500, 100), NewContext());
        Assert.Equal(SpacePlanKind.Full, plan.Kind);
        Assert.Equal(500, plan.Width, 3);
        Assert.Equal(10, plan.Height, 3);
    }

    [Fact]
    public void Column_reports_partial_when_items_overflow()
    {
        var column = new ColumnDescriptor();
        column.Spacing(5);
        for (var i = 0; i < 10; i++)
            column.Item().Height(20);

        var plan = column.Element.Measure(new Size(100, 60), NewContext());
        Assert.Equal(SpacePlanKind.Partial, plan.Kind);
        Assert.Equal(20 + 5 + 20, plan.Height, 3); // third item (at 50..70) does not fit
    }

    [Theory]
    [InlineData("circle-rgba.png", true)]
    [InlineData("gradient-rgb.png", false)]
    [InlineData("checker-palette.png", true)]
    [InlineData("photo.jpg", false)]
    public void Images_are_encoded(string file, bool hasSoftMask)
    {
        var image = Image.FromFile(Asset(file));
        var encoded = image.Encoded;

        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width, encoded.Width);
        Assert.NotEmpty(encoded.Data);
        Assert.Equal(hasSoftMask, encoded.SoftMask != null);
    }

    [Fact]
    public void Opaque_png_data_is_passed_through_without_recompression()
    {
        var bytes = File.ReadAllBytes(Asset("gradient-rgb.png"));
        var info = PngDecoder.ReadInfo(bytes);
        var encoded = Image.FromBytes(bytes).Encoded;
        Assert.Equal(info.Idat, encoded.Data);
    }

    [Fact]
    public void Unsupported_image_format_throws()
    {
        Assert.Throws<NotSupportedException>(() => Image.FromBytes([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
    }

    [Fact]
    public void Color_parses_hex()
    {
        Assert.Equal(new Color(0x1E, 0x3A, 0x8A), Color.FromHex("#1E3A8A"));
        Assert.Equal(new Color(255, 0, 170), Color.FromHex("f0a"));
        Assert.Throws<FormatException>(() => Color.FromHex("#12"));
    }

    /// <summary>
    /// PDF subsets omit the cmap table (glyphs are addressed by id); add a trivial one so our parser,
    /// which requires a cmap, can load the subset for verification.
    /// </summary>
    private static byte[] AddEmptyCmap(byte[] font)
    {
        // format 4 subtable with the single mandatory 0xFFFF segment
        byte[] cmap =
        [
            0, 0, 0, 1, 0, 3, 0, 1, 0, 0, 0, 12,
            0, 4, 0, 24, 0, 0, 0, 2, 0, 2, 0, 0, 0, 0,
            0xFF, 0xFF, 0, 0, 0xFF, 0xFF, 0, 1, 0, 0,
        ];

        var tables = new List<(string Tag, byte[] Data)>();
        var numTables = (font[4] << 8) | font[5];
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(font, record, 4);
            var offset = (font[record + 8] << 24) | (font[record + 9] << 16) | (font[record + 10] << 8) | font[record + 11];
            var length = (font[record + 12] << 24) | (font[record + 13] << 16) | (font[record + 14] << 8) | font[record + 15];
            tables.Add((tag, font.AsSpan(offset, length).ToArray()));
        }

        tables.Add(("cmap", cmap));
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        using var output = new MemoryStream();
        var header = new byte[12 + tables.Count * 16];
        header[1] = 1;
        header[4] = (byte)(tables.Count >> 8);
        header[5] = (byte)tables.Count;
        var dataOffset = header.Length;
        for (var i = 0; i < tables.Count; i++)
        {
            var record = 12 + i * 16;
            System.Text.Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(header, record);
            WriteU32(header, record + 8, dataOffset);
            WriteU32(header, record + 12, tables[i].Data.Length);
            dataOffset += (tables[i].Data.Length + 3) & ~3;
        }

        output.Write(header);
        foreach (var (_, data) in tables)
        {
            output.Write(data);
            output.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
        }

        return output.ToArray();

        static void WriteU32(byte[] target, int at, int value)
        {
            target[at] = (byte)(value >> 24);
            target[at + 1] = (byte)(value >> 16);
            target[at + 2] = (byte)(value >> 8);
            target[at + 3] = (byte)value;
        }
    }
}
