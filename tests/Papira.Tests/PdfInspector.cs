using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Papira.Tests;

/// <summary>Minimal structural PDF checks used by the tests (independent of the writer's code).</summary>
internal sealed partial class PdfInspector
{
    private readonly byte[] _data;
    private readonly string _text;

    public PdfInspector(byte[] data)
    {
        _data = data;
        _text = Encoding.Latin1.GetString(data);
    }

    public int PageCount => PageRegex().Count(_text);

    /// <summary>Validates header, trailer and that every xref entry points at the right object.</summary>
    public void AssertValidStructure()
    {
        Assert.StartsWith("%PDF-1.7", _text);
        Assert.EndsWith("%%EOF\n", _text);

        var startXref = int.Parse(Regex.Match(_text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", _text[startXref..]);

        var header = Regex.Match(_text[startXref..], @"^xref\n0 (\d+)\n");
        var count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        var entries = startXref + header.Length;

        for (var i = 1; i < count; i++)
        {
            var entry = _text.Substring(entries + i * 20, 20);
            Assert.Matches(@"^\d{10} 00000 n\r\n$", entry);
            var offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i} 0 obj", _text[offset..]);
        }

        Assert.Contains($"/Size {count}", _text);
    }

    /// <summary>All stream contents, inflated when Flate-compressed.</summary>
    public IEnumerable<string> Streams()
    {
        foreach (Match match in StreamRegex().Matches(_text))
        {
            var length = int.Parse(match.Groups["len"].Value, CultureInfo.InvariantCulture);
            var start = match.Index + match.Length;
            var raw = _data.AsSpan(start, length).ToArray();
            if (match.Value.Contains("/Filter/FlateDecode"))
            {
                using var zlib = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                raw = output.ToArray();
            }

            yield return Encoding.Latin1.GetString(raw);
        }
    }

    public string Raw => _text;

    /// <summary>Content streams of the pages, in page order.</summary>
    public IReadOnlyList<string> PageContents() =>
        Streams().Where(s => s.Contains(" Tj") || s.Contains(" TJ") || s.Contains(" re f") || s.Contains(" Do")).ToList();

    /// <summary>
    /// Text of every glyph run (one line per run), decoded through the ToUnicode maps.
    /// Intended for single-font documents: glyph ids of different fonts are not distinguished.
    /// </summary>
    public string ExtractText()
    {
        var map = new Dictionary<string, string>();
        foreach (var cmap in Streams().Where(s => s.Contains("begincmap")))
        {
            var mappings = cmap[cmap.IndexOf("beginbfchar", StringComparison.Ordinal)..];
            foreach (Match m in CMapEntryRegex().Matches(mappings))
            {
                var hex = m.Groups[2].Value;
                var chars = new char[hex.Length / 4];
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = (char)Convert.ToUInt16(hex.Substring(i * 4, 4), 16);
                map[m.Groups[1].Value] = new string(chars);
            }
        }

        var builder = new StringBuilder();
        foreach (var page in PageContents())
        {
            foreach (Match run in GlyphRunRegex().Matches(page))
            {
                // A run is either "<hex> Tj" or a kerned "[<hex> n <hex> ...] TJ".
                foreach (Match segment in HexStringRegex().Matches(run.Value))
                {
                    var hex = segment.Groups[1].Value;
                    for (var i = 0; i + 4 <= hex.Length; i += 4)
                        builder.Append(map.TryGetValue(hex.Substring(i, 4), out var text) ? text : "\uFFFD");
                }

                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"<([0-9A-F]{4})><([0-9A-F]+)>")]
    private static partial Regex CMapEntryRegex();

    [GeneratedRegex(@"<[0-9A-F]*> Tj|\[[^\]]*\] TJ")]
    private static partial Regex GlyphRunRegex();

    [GeneratedRegex(@"<([0-9A-F]*)>")]
    private static partial Regex HexStringRegex();

    [GeneratedRegex(@"/Type/Page/")]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"<</Length (?<len>\d+)[^\n]*>>\nstream\n")]
    private static partial Regex StreamRegex();
}
