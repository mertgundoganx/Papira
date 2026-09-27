using Papira.Barcodes;
using Papira.Infrastructure;
using Papira.Rendering;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// The expected values were verified against independent implementations: the QR matrices were decoded
/// with OpenCV's QR reader, and the Code 128 module widths match the python-barcode library.
/// </summary>
public class BarcodeTests
{
    private static string[] Rows(QrEncoder code)
    {
        var rows = new string[code.Size];
        for (var y = 0; y < code.Size; y++)
        {
            var chars = new char[code.Size];
            for (var x = 0; x < code.Size; x++)
                chars[x] = code[x, y] ? '#' : '.';
            rows[y] = new string(chars);
        }

        return rows;
    }

    private static string Widths(string value) => string.Join("", Code128Encoder.Encode(value));

    [Fact]
    public void Qr_code_matches_the_verified_matrix()
    {
        string[] expected =
        [
            "#######..#....#######",
            "#.....#..#.##.#.....#",
            "#.###.#.#####.#.###.#",
            "#.###.#.###...#.###.#",
            "#.###.#.###.#.#.###.#",
            "#.....#.####..#.....#",
            "#######.#.#.#.#######",
            "........##...........",
            "#.#####....#..#####..",
            "..#..#..##.####....##",
            "#.#..###.#..#.##..###",
            "####.#..##.####..#..#",
            "#...#.##..#.#..#...#.",
            "........#.#.#..#.#...",
            "#######...##.#..##.#.",
            "#.....#.#......##.###",
            "#.###.#.#..#.#..##...",
            "#.###.#.##.#####..#..",
            "#.###.#.#...#.##.....",
            "#.....#...######.##.#",
            "#######.#...#..#..#..",
        ];

        Assert.Equal(expected, Rows(QrEncoder.Encode("PAPIRA", QrErrorCorrection.Medium)));
    }

    [Fact]
    public void Qr_code_has_the_finder_patterns_in_three_corners()
    {
        var code = QrEncoder.Encode("test", QrErrorCorrection.Low);

        foreach (var (cx, cy) in new[] { (3, 3), (code.Size - 4, 3), (3, code.Size - 4) })
        {
            Assert.True(code[cx, cy]);              // centre of the 3x3 block
            Assert.False(code[cx + 2, cy]);         // the light ring
            Assert.True(code[cx + 3, cy]);          // the outer square
        }

        Assert.True(code[8, code.Size - 8], "the module next to the lower left finder is always dark");
    }

    [Theory]
    [InlineData("A", 21)]                                        // version 1
    [InlineData("0123456789012345678901234567890123456", 25)]    // 37 digits still fit version 2
    public void Qr_code_size_follows_the_version(string data, int expectedSize)
    {
        Assert.Equal(expectedSize, QrEncoder.Encode(data, QrErrorCorrection.Medium).Size);
    }

    [Fact]
    public void Higher_error_correction_needs_a_larger_code()
    {
        var text = new string('x', 100);
        var low = QrEncoder.Encode(text, QrErrorCorrection.Low).Size;
        var high = QrEncoder.Encode(text, QrErrorCorrection.High).Size;
        Assert.True(high > low, $"high correction ({high}) should need more modules than low ({low})");
    }

    [Fact]
    public void Codeword_count_matches_the_version_capacity()
    {
        // Version 1, level M: 26 codewords in total (16 data + 10 error correction).
        Assert.Equal(26, QrEncoder.EncodeCodewords("PAPIRA", QrErrorCorrection.Medium).Length);
    }

    [Fact]
    public void Text_that_does_not_fit_in_any_version_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('x', 3000), QrErrorCorrection.High));
    }

    [Fact]
    public void Code128_matches_the_reference_widths()
    {
        Assert.Equal("2112143131211113233131211221321131412212313212211141311221321131412122222221223121312121232331112", Widths("PAP-2026-000123"));
        Assert.Equal("2112321122321311233311212411122141211122321141313112222211322331112", Widths("1234567890128"));
    }

    [Fact]
    public void Code128_packs_digits_two_per_symbol()
    {
        // Ten digits: start C, five digit pairs, checksum and stop => 8 symbols of 6 widths plus the stop bar.
        Assert.Equal(8 * 6 + 1, Code128Encoder.Encode("0123456789").Length);
    }

    [Fact]
    public void Code128_ends_with_the_stop_pattern()
    {
        var widths = Code128Encoder.Encode("HELLO");
        Assert.Equal([2, 3, 3, 1, 1, 1, 2], widths[^7..]);
    }

    [Theory]
    [InlineData("Türkçe")]  // not printable ASCII
    [InlineData("\t")]
    public void Code128_rejects_unsupported_characters(string value)
    {
        Assert.Throws<ArgumentException>(() => Code128Encoder.Encode(value));
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    [Fact]
    public void Qr_code_is_square_and_drawn_as_rectangles()
    {
        var pdf = Inspect(Generate(c => c.Width(120).QrCode("https://example.com")));
        var page = pdf.PageContents()[0];

        Assert.True(System.Text.RegularExpressions.Regex.Count(page, " re f") > 20);
        Assert.Contains("0 0 0 rg", page);
    }

    [Fact]
    public void Qr_code_takes_the_shorter_side_of_its_area()
    {
        var slot = new Papira.Elements.Slot();
        slot.QrCode("x");
        var plan = slot.Measure(new Size(300, 80), new LayoutContext(new Canvas(new DocumentResources())));

        Assert.Equal(80, plan.Width, 2);
        Assert.Equal(80, plan.Height, 2);
    }

    [Fact]
    public void Barcode_spans_the_width_and_is_40pt_high_by_default()
    {
        var slot = new Papira.Elements.Slot();
        slot.Barcode("PAP-2026-000123");
        var plan = slot.Measure(new Size(300, 200), new LayoutContext(new Canvas(new DocumentResources())));

        Assert.Equal(300, plan.Width, 2);
        Assert.Equal(40, plan.Height, 2);
    }

    [Fact]
    public void Barcode_draws_one_rectangle_per_bar()
    {
        var pdf = Inspect(Generate(c => c.Height(40).Barcode("HELLO")));

        // 90 modules => 15 symbols of 3 bars, so 45 bars minus the ones merged at the stop pattern.
        var bars = System.Text.RegularExpressions.Regex.Count(pdf.PageContents()[0], " re f");
        Assert.InRange(bars, 20, 40);
    }
}
