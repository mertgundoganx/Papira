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

    /// <summary>Content streams of the pages, in page order, resolved through each page's /Contents reference.</summary>
    public IReadOnlyList<string> PageContents()
    {
        var streams = ObjectStreams();
        return PageContentRegex().Matches(_text)
            .Select(m => streams[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)])
            .ToList();
    }

    /// <summary>Stream contents by object number, inflated when Flate-compressed.</summary>
    private Dictionary<int, string> ObjectStreams()
    {
        var result = new Dictionary<int, string>();
        foreach (Match match in ObjectStreamRegex().Matches(_text))
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

            result[int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture)] = Encoding.Latin1.GetString(raw);
        }

        return result;
    }

    /// <summary>
    /// Text of every glyph run (one line per run), decoded through the ToUnicode maps. Where the
    /// document says what a piece of drawing stands for (<c>/ActualText</c>, which the scripts that
    /// reorder their syllables need), that is what a reader is given instead of the glyphs.
    /// </summary>
    public string ExtractText()
    {
        var map = ToUnicodeMap();
        var builder = new StringBuilder();

        foreach (var content in PageContents())
        {
            var position = 0;
            while (position < content.Length)
            {
                var span = content.IndexOf("/Span<</ActualText<", position, StringComparison.Ordinal);
                if (span < 0)
                {
                    AppendRuns(builder, map, content[position..]);
                    break;
                }

                AppendRuns(builder, map, content[position..span]);

                var start = span + "/Span<</ActualText<".Length;
                var end = content.IndexOf(">>>BDC", start, StringComparison.Ordinal);
                var close = content.IndexOf("EMC", end, StringComparison.Ordinal);
                var hex = content[start..end].TrimStart('F', 'E').TrimStart('F', 'E');

                // The text the span stands for, written as UTF-16.
                for (var i = 0; i + 4 <= hex.Length; i += 4)
                    builder.Append((char)Convert.ToUInt16(hex.Substring(i, 4), 16));

                builder.Append('\n');
                position = close < 0 ? content.Length : close + 3;
            }
        }

        return builder.ToString();
    }

    private static void AppendRuns(StringBuilder builder, Dictionary<string, string> map, string content)
    {
        foreach (Match run in GlyphRunRegex().Matches(content))
        {
            foreach (Match segment in HexStringRegex().Matches(run.Groups["glyphs"].Value))
            {
                var hex = segment.Groups[1].Value;
                for (var i = 0; i + 4 <= hex.Length; i += 4)
                    builder.Append(map.TryGetValue(hex.Substring(i, 4), out var text) ? text : "\uFFFD");
            }

            builder.Append('\n');
        }
    }

    /// <summary>Glyph runs with their page index and start position (PDF coordinates), in drawing order.</summary>
    public List<(int Page, float X, float Y, string Text)> TextRuns()
    {
        var map = ToUnicodeMap();
        var runs = new List<(int, float, float, string)>();
        var pages = PageContents();

        for (var p = 0; p < pages.Count; p++)
        {
            foreach (Match run in GlyphRunRegex().Matches(pages[p]))
            {
                // A run is either "<hex> Tj" or a kerned "[<hex> n <hex> ...] TJ".
                var builder = new StringBuilder();
                foreach (Match segment in HexStringRegex().Matches(run.Groups["glyphs"].Value))
                {
                    var hex = segment.Groups[1].Value;
                    for (var i = 0; i + 4 <= hex.Length; i += 4)
                        builder.Append(map.TryGetValue(hex.Substring(i, 4), out var text) ? text : "\uFFFD");
                }

                runs.Add((p,
                    float.Parse(run.Groups["x"].Value, CultureInfo.InvariantCulture),
                    float.Parse(run.Groups["y"].Value, CultureInfo.InvariantCulture),
                    builder.ToString()));
            }
        }

        return runs;
    }

    /// <summary>
    /// Every string of the file, decoded: the ones written as text in parentheses and the ones written
    /// as UTF-16 hex. Used to look for names, tooltips and the options of a dropdown.
    /// </summary>
    public List<string> ExtractStrings()
    {
        var strings = new List<string>();
        foreach (Match match in Regex.Matches(_text, @"\((?<literal>(?:\\.|[^()\\])*)\)|<FEFF(?<hex>[0-9A-F]*)>"))
        {
            if (match.Groups["hex"].Success)
            {
                var hex = match.Groups["hex"].Value;
                var chars = new char[hex.Length / 4];
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = (char)Convert.ToUInt16(hex.Substring(i * 4, 4), 16);
                strings.Add(new string(chars));
            }
            else
            {
                strings.Add(Regex.Replace(match.Groups["literal"].Value, @"\\(.)", "$1"));
            }
        }

        return strings;
    }

    /// <summary>The text of the glyph runs of one stream — the appearance of a form field, say.</summary>
    public string TextIn(string stream)
    {
        var map = ToUnicodeMap();
        var builder = new StringBuilder();
        foreach (Match run in GlyphRunRegex().Matches(stream))
        {
            foreach (Match segment in HexStringRegex().Matches(run.Groups["glyphs"].Value))
            {
                var hex = segment.Groups[1].Value;
                for (var i = 0; i + 4 <= hex.Length; i += 4)
                    builder.Append(map.TryGetValue(hex.Substring(i, 4), out var text) ? text : "\uFFFD");
            }
        }

        return builder.ToString();
    }

    private Dictionary<string, string> ToUnicodeMap()
    {
        var map = new Dictionary<string, string>();
        foreach (var cmap in Streams().Where(s => s.Contains("begincmap")))
        {
            var index = cmap.IndexOf("beginbfchar", StringComparison.Ordinal);
            if (index < 0)
                continue;

            foreach (Match m in CMapEntryRegex().Matches(cmap[index..]))
            {
                var hex = m.Groups[2].Value;
                var chars = new char[hex.Length / 4];
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = (char)Convert.ToUInt16(hex.Substring(i * 4, 4), 16);
                map[m.Groups[1].Value] = new string(chars);
            }
        }

        return map;
    }

    [GeneratedRegex(@"<([0-9A-F]{4})><([0-9A-F]+)>")]
    private static partial Regex CMapEntryRegex();

    [GeneratedRegex(@"1 0 0(?:\.2)? 1 (?<x>-?[\d.]+) (?<y>-?[\d.]+) Tm (?<glyphs><[0-9A-F]*> Tj|\[[^\]]*\] TJ)")]
    private static partial Regex GlyphRunRegex();

    [GeneratedRegex(@"<([0-9A-F]*)>")]
    private static partial Regex HexStringRegex();

    [GeneratedRegex(@"/Type/Page/")]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"<</Length (?<len>\d+)[^\n]*>>\nstream\n")]
    private static partial Regex StreamRegex();

    [GeneratedRegex(@"(?<id>\d+) 0 obj\n<</Length (?<len>\d+)[^\n]*>>\nstream\n")]
    private static partial Regex ObjectStreamRegex();

    [GeneratedRegex(@"/Type/Page/[^>]*?/Contents (\d+) 0 R")]
    private static partial Regex PageContentRegex();
}
