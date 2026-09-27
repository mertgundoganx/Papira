using System.Collections.Concurrent;
using System.IO.Compression;
using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>How a font draws a glyph in colour, if it does.</summary>
internal enum ColorGlyphKind : byte
{
    /// <summary>A plain outline, drawn in the colour of the text.</summary>
    None,

    /// <summary>Layers of outlines, each with a colour of its own (the COLR and CPAL tables).</summary>
    Layers,

    /// <summary>A picture, embedded as PNG (the sbix or CBDT tables).</summary>
    Bitmap,

    /// <summary>A drawing, embedded as SVG (the SVG table).</summary>
    Drawing,
}

/// <summary>Where a bitmap glyph sits relative to the text origin, in font units.</summary>
internal readonly record struct BitmapGlyph(Image Image, float Left, float Top, float Width, float Height);

/// <summary>
/// The colour glyphs of a font. Emoji fonts store them in one of four ways, and Papira draws them all:
/// as layered outlines (COLR/CPAL, used by Windows), as pictures (sbix on macOS, CBDT on Android and
/// Linux) or as drawings (the SVG table, used by the current Noto Color Emoji).
/// </summary>
internal sealed class ColorGlyphs
{
    private const int MaxLayers = 256;

    public static readonly ColorGlyphs None = new();

    private readonly TrueTypeFont? _font;
    private readonly Dictionary<ushort, (int First, int Count)> _layerRanges = [];
    private readonly (ushort Glyph, int Palette)[] _layers = [];
    private readonly Color[] _palette = [];
    private readonly List<(int First, int Last, int Offset, int Length)> _strikes = [];
    private readonly List<(int First, int Last, int Offset, int Length)> _documents = [];
    private readonly ConcurrentDictionary<int, Image?> _bitmaps = new();
    private readonly ConcurrentDictionary<int, Svg.SvgDocument?> _drawings = new();
    private readonly Func<ushort, BitmapGlyph?>? _bitmapReader;

    private ColorGlyphs()
    {
    }

    private ColorGlyphs(
        TrueTypeFont font,
        Dictionary<ushort, (int First, int Count)> layerRanges,
        (ushort Glyph, int Palette)[] layers,
        Color[] palette,
        Func<ushort, BitmapGlyph?>? bitmapReader,
        List<(int First, int Last, int Offset, int Length)> documents)
    {
        _font = font;
        _layerRanges = layerRanges;
        _layers = layers;
        _palette = palette;
        _bitmapReader = bitmapReader;
        _documents = documents;
    }

    public bool IsEmpty => _layerRanges.Count == 0 && _bitmapReader == null && _documents.Count == 0;

    public ColorGlyphKind KindOf(ushort glyph)
    {
        if (_layerRanges.ContainsKey(glyph))
            return ColorGlyphKind.Layers;

        if (_documents.Count > 0 && Document(glyph) >= 0)
            return ColorGlyphKind.Drawing;

        // Asking for the picture is what tells whether there is one; the answer is kept.
        return _bitmapReader?.Invoke(glyph) != null ? ColorGlyphKind.Bitmap : ColorGlyphKind.None;
    }

    /// <summary>The layers of a colour glyph, each with the palette colour it is drawn in.</summary>
    public IEnumerable<(ushort Glyph, Color? Color)> Layers(ushort glyph)
    {
        if (!_layerRanges.TryGetValue(glyph, out var range))
            yield break;

        for (var i = 0; i < range.Count; i++)
        {
            var (layerGlyph, palette) = _layers[range.First + i];

            // Palette entry 0xFFFF means the layer takes the colour of the text.
            yield return (layerGlyph, palette == 0xFFFF || palette >= _palette.Length ? (Color?)null : _palette[palette]);
        }
    }

    public BitmapGlyph? Bitmap(ushort glyph) => _bitmapReader?.Invoke(glyph);

    /// <summary>The drawing of a glyph from the SVG table, parsed once and kept.</summary>
    public Svg.SvgDocument? Drawing(ushort glyph)
    {
        var index = Document(glyph);
        if (index < 0)
            return null;

        return _drawings.GetOrAdd((index << 16) | glyph, _ =>
        {
            try
            {
                var (_, _, offset, length) = _documents[index];
                var data = Decompress(_font!.Data.AsSpan(offset, length).ToArray());
                return Svg.SvgParser.Parse(data, $"glyph{glyph}");
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                return null;
            }
        });
    }

    private int Document(ushort glyph)
    {
        for (var i = 0; i < _documents.Count; i++)
        {
            if (glyph >= _documents[i].First && glyph <= _documents[i].Last)
                return i;
        }

        return -1;
    }

    /// <summary>An SVG document of a font may be stored gzipped.</summary>
    private static byte[] Decompress(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B)
            return data;

        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 4);
        gzip.CopyTo(output, 64 * 1024);
        return output.ToArray();
    }

    // ---- Reading -----------------------------------------------------------------------------------

    public static ColorGlyphs Load(TrueTypeFont font)
    {
        try
        {
            var layerRanges = new Dictionary<ushort, (int, int)>();
            (ushort, int)[] layers = [];
            Color[] palette = [];
            if (font.TryTable("COLR", out var colr, out _) && font.TryTable("CPAL", out var cpal, out _))
            {
                (layerRanges, layers) = ReadColr(font.Data, colr);
                palette = ReadPalette(font.Data, cpal);
            }

            var bitmaps = ReadSbix(font) ?? ReadCbdt(font);

            var documents = new List<(int, int, int, int)>();
            if (font.TryTable("SVG ", out var svg, out _))
                documents = ReadSvgDocuments(font.Data, svg);

            var glyphs = new ColorGlyphs(font, layerRanges, layers, palette, bitmaps, documents);
            return glyphs.IsEmpty ? None : glyphs;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            // A font with broken colour tables is still usable in black and white.
            return None;
        }
    }

    /// <summary>COLR version 0: a list of layers per base glyph. Version 1 adds paints Papira does not draw.</summary>
    private static (Dictionary<ushort, (int, int)> Ranges, (ushort, int)[] Layers) ReadColr(byte[] data, int colr)
    {
        var baseCount = U16(data, colr + 2);
        var baseOffset = colr + (int)U32(data, colr + 4);
        var layerOffset = colr + (int)U32(data, colr + 8);
        var layerCount = U16(data, colr + 12);

        var layers = new (ushort, int)[layerCount];
        for (var i = 0; i < layerCount; i++)
            layers[i] = (U16(data, layerOffset + i * 4), U16(data, layerOffset + i * 4 + 2));

        var ranges = new Dictionary<ushort, (int, int)>(baseCount);
        for (var i = 0; i < baseCount; i++)
        {
            var record = baseOffset + i * 6;
            var first = U16(data, record + 2);
            var count = Math.Min((int)U16(data, record + 4), MaxLayers);
            if (first + count <= layerCount && count > 0)
                ranges[U16(data, record)] = (first, count);
        }

        return (ranges, layers);
    }

    private static Color[] ReadPalette(byte[] data, int cpal)
    {
        var entries = U16(data, cpal + 2);
        var palettes = U16(data, cpal + 4);
        var colors = U16(data, cpal + 6);
        var records = cpal + (int)U32(data, cpal + 8);

        // Only the first palette is used; a document has no way to ask for another one.
        var first = palettes > 0 ? U16(data, cpal + 12) : 0;
        var palette = new Color[Math.Min(entries, Math.Max(colors - first, 0))];
        for (var i = 0; i < palette.Length; i++)
        {
            var entry = records + (first + i) * 4;

            // The entries are blue, green, red, alpha; transparency is not part of a PDF colour.
            palette[i] = new Color(data[entry + 2], data[entry + 1], data[entry]);
        }

        return palette;
    }

    /// <summary>The sbix table of Apple's fonts: one PNG per glyph and size.</summary>
    private static Func<ushort, BitmapGlyph?>? ReadSbix(TrueTypeFont font)
    {
        if (!font.TryTable("sbix", out var sbix, out var length))
            return null;

        var data = font.Data;
        var strikeCount = (int)U32(data, sbix + 4);
        var strikes = new List<(int Offset, int Ppem)>(strikeCount);
        for (var i = 0; i < strikeCount && i < 64; i++)
        {
            var strike = sbix + (int)U32(data, sbix + 8 + i * 4);
            if (strike > sbix && strike < sbix + length)
                strikes.Add((strike, U16(data, strike)));
        }

        if (strikes.Count == 0)
            return null;

        // The largest strike gives the best picture at any size.
        strikes.Sort((left, right) => right.Ppem.CompareTo(left.Ppem));
        var cache = new ConcurrentDictionary<ushort, BitmapGlyph?>();

        return glyph => cache.GetOrAdd(glyph, key =>
        {
            foreach (var (strike, ppem) in strikes)
            {
                var record = strike + 4 + key * 4;
                var start = (int)U32(data, record);
                var end = (int)U32(data, record + 4);
                if (end - start <= 8)
                    continue;

                // Each record is a graphic type with an offset, then the picture itself.
                if (U32(data, strike + start + 4) != Tag("png "))
                    continue;

                var image = ReadImage(data, strike + start + 8, end - start - 8);
                if (image == null)
                    continue;

                var scale = (float)font.UnitsPerEm / ppem;
                var originX = (short)U16(data, strike + start) * scale;
                var originY = (short)U16(data, strike + start + 2) * scale;
                return new BitmapGlyph(image, originX, -(image.Height * scale + originY), image.Width * scale, image.Height * scale);
            }

            return null;
        });
    }

    /// <summary>The CBLC and CBDT tables: the same idea as sbix, with the metrics kept separately.</summary>
    private static Func<ushort, BitmapGlyph?>? ReadCbdt(TrueTypeFont font)
    {
        if (!font.TryTable("CBLC", out var cblc, out _) || !font.TryTable("CBDT", out var cbdt, out var cbdtLength))
            return null;

        var data = font.Data;
        var sizeCount = (int)U32(data, cblc + 4);
        var sizes = new List<(int Array, int Tables, int First, int Last, int Ppem)>(sizeCount);
        for (var i = 0; i < sizeCount && i < 64; i++)
        {
            var record = cblc + 8 + i * 48;
            sizes.Add((
                cblc + (int)U32(data, record),
                Math.Min((int)U32(data, record + 8), 4096),
                U16(data, record + 40),
                U16(data, record + 42),
                data[record + 44]));
        }

        if (sizes.Count == 0)
            return null;

        // The largest strike gives the best picture at any size.
        sizes.Sort((left, right) => right.Ppem.CompareTo(left.Ppem));
        var cache = new ConcurrentDictionary<ushort, BitmapGlyph?>();

        return glyph => cache.GetOrAdd(glyph, key =>
        {
            foreach (var (array, tables, first, last, ppem) in sizes)
            {
                if (key < first || key > last || ppem == 0)
                    continue;

                if (!TryFindBitmap(data, array, tables, key, out var offset, out var length, out var format))
                    continue;

                // Formats 17, 18 and 19 hold a PNG; what comes before it differs in size.
                var header = format switch { 17 => 5, 18 => 8, 19 => 0, _ => -1 };
                if (header < 0 || length <= header + 4)
                    continue;

                var start = cbdt + offset + header + 4;
                if (start < cbdt || start + length - header - 4 > cbdt + cbdtLength)
                    continue;

                var image = ReadImage(data, start, length - header - 4);
                if (image == null)
                    continue;

                var scale = (float)font.UnitsPerEm / ppem;
                var bearingX = format == 19 ? 0 : (sbyte)data[cbdt + offset + 2] * scale;
                var bearingY = format == 19 ? image.Height * scale : (sbyte)data[cbdt + offset + 3] * scale;
                return new BitmapGlyph(image, bearingX, -bearingY, image.Width * scale, image.Height * scale);
            }

            return null;
        });
    }

    /// <summary>Finds a glyph in the index subtables the CBLC table points at.</summary>
    private static bool TryFindBitmap(byte[] data, int array, int tables, ushort glyph, out int offset, out int length, out int format)
    {
        offset = length = format = 0;
        for (var i = 0; i < tables; i++)
        {
            var record = array + i * 8;
            var first = U16(data, record);
            var last = U16(data, record + 2);
            if (glyph < first || glyph > last)
                continue;

            var subtable = array + (int)U32(data, record + 4);
            var indexFormat = U16(data, subtable);
            format = U16(data, subtable + 2);
            var images = (int)U32(data, subtable + 4);

            switch (indexFormat)
            {
                case 1:
                {
                    var start = (int)U32(data, subtable + 8 + (glyph - first) * 4);
                    var end = (int)U32(data, subtable + 8 + (glyph - first + 1) * 4);
                    offset = images + start;
                    length = end - start;
                    return length > 0;
                }

                case 2:
                {
                    length = (int)U32(data, subtable + 8);
                    offset = images + (glyph - first) * length;
                    return length > 0;
                }

                case 3:
                {
                    var start = U16(data, subtable + 8 + (glyph - first) * 2);
                    var end = U16(data, subtable + 8 + (glyph - first + 1) * 2);
                    offset = images + start;
                    length = end - start;
                    return length > 0;
                }

                default:
                    return false;
            }
        }

        return false;
    }

    private static Image? ReadImage(byte[] data, int offset, int length)
    {
        if (length <= 0 || offset < 0 || offset + length > data.Length)
            return null;

        try
        {
            return Image.FromBytes(data.AsSpan(offset, length).ToArray());
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<(int First, int Last, int Offset, int Length)> ReadSvgDocuments(byte[] data, int svg)
    {
        var list = svg + (int)U32(data, svg + 2);
        var count = U16(data, list);
        var documents = new List<(int, int, int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            var record = list + 2 + i * 12;
            documents.Add((
                U16(data, record),
                U16(data, record + 2),
                list + (int)U32(data, record + 4),
                (int)U32(data, record + 8)));
        }

        return documents;
    }
}
