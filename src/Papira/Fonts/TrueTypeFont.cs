using System.Buffers.Binary;
using System.Text;

namespace Papira.Fonts;

/// <summary>Style metadata of a font face, cheap to read without parsing glyph data.</summary>
internal sealed record FontFaceInfo(string Family, int Weight, bool Italic, bool HasTrueTypeOutlines);

/// <summary>
/// Parsed TrueType font face (also a single face of a .ttc collection).
/// Not modified after construction and therefore safe to share between threads and documents.
/// <see cref="Data"/> is owned by the font; callers must not modify it.
/// </summary>
internal sealed class TrueTypeFont
{
    private const uint TagTtcf = 0x74746366; // 'ttcf'
    private const uint TagOtto = 0x4F54544F; // 'OTTO'

    private readonly Dictionary<uint, (int Offset, int Length)> _tables;
    private readonly ushort[] _advances;
    private readonly ushort[] _bmpGlyphs;
    private readonly Dictionary<int, ushort>? _supplementaryGlyphs;

    public byte[] Data { get; }
    public FontFaceInfo Info { get; }
    public string PostScriptName { get; }
    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public bool LongLocaFormat { get; }

    public short Ascender { get; }
    public short Descender { get; }
    public short LineGap { get; }
    public short CapHeight { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public float ItalicAngle { get; }
    public bool IsFixedPitch { get; }
    public short UnderlinePosition { get; }
    public short UnderlineThickness { get; }
    public short StrikeoutPosition { get; }
    public short StrikeoutSize { get; }

    private TrueTypeFont(byte[] data, int faceOffset)
    {
        Data = data;
        _tables = ReadTableDirectory(data, faceOffset);

        var head = Table("head", minLength: 54);
        UnitsPerEm = U16(data, head + 18);
        if (UnitsPerEm is < 16 or > 16384)
            throw new InvalidDataException($"Invalid font: unitsPerEm {UnitsPerEm} is out of range.");
        XMin = I16(data, head + 36);
        YMin = I16(data, head + 38);
        XMax = I16(data, head + 40);
        YMax = I16(data, head + 42);
        var macStyle = U16(data, head + 44);
        LongLocaFormat = I16(data, head + 50) == 1;

        GlyphCount = U16(data, Table("maxp", minLength: 6) + 4);

        if (!TryTable("glyf", out _, out _) || !TryTable("loca", out _, out var locaLength))
            throw new NotSupportedException("The font has no TrueType outlines (glyf/loca tables); bitmap-only and CFF fonts are not supported.");
        if (locaLength < (GlyphCount + 1L) * (LongLocaFormat ? 4 : 2))
            throw new InvalidDataException("Invalid font: the 'loca' table is too short.");

        var hhea = Table("hhea", minLength: 36);
        Ascender = I16(data, hhea + 4);
        Descender = I16(data, hhea + 6);
        LineGap = I16(data, hhea + 8);
        var numberOfHMetrics = U16(data, hhea + 34);

        var weight = 400;
        var italic = (macStyle & 2) != 0;
        CapHeight = (short)(Ascender * 0.7);
        StrikeoutSize = (short)(UnitsPerEm / 20);
        StrikeoutPosition = (short)(UnitsPerEm / 4);

        if (TryTable("OS/2", out var os2, out var os2Length) && os2Length >= 78)
        {
            weight = U16(data, os2 + 4);
            var fsSelection = U16(data, os2 + 62);
            italic |= (fsSelection & 1) != 0;
            StrikeoutSize = I16(data, os2 + 26);
            StrikeoutPosition = I16(data, os2 + 28);

            // USE_TYPO_METRICS: the font asks for typographic metrics instead of hhea ones.
            if ((fsSelection & 0x80) != 0)
            {
                Ascender = I16(data, os2 + 68);
                Descender = I16(data, os2 + 70);
                LineGap = I16(data, os2 + 72);
            }

            var version = U16(data, os2);
            if (version >= 2 && os2Length >= 90)
                CapHeight = I16(data, os2 + 88);
        }

        UnderlinePosition = (short)(-UnitsPerEm / 10);
        UnderlineThickness = (short)(UnitsPerEm / 20);
        if (TryTable("post", out var post, out _))
        {
            ItalicAngle = I32(data, post + 4) / 65536f;
            UnderlinePosition = I16(data, post + 8);
            UnderlineThickness = I16(data, post + 10);
            IsFixedPitch = U32(data, post + 12) != 0;
        }

        var names = ReadNames(data, _tables);
        Info = new FontFaceInfo(names.Family, weight, italic, true);
        PostScriptName = SanitizePostScriptName(names.PostScript ?? names.Family + "-" + names.Subfamily);

        TryTable("hmtx", out var hmtx, out var hmtxLength);
        _advances = ReadAdvances(data, hmtx, Math.Min(numberOfHMetrics, hmtxLength / 4), GlyphCount);
        TryTable("cmap", out var cmap, out var cmapLength);
        (_bmpGlyphs, _supplementaryGlyphs) = ReadCharacterMap(data, cmap, cmapLength);
    }

    /// <summary>Loads every face in a font file (.ttf, or all faces of a .ttc collection).</summary>
    public static IReadOnlyList<TrueTypeFont> LoadAll(byte[] data)
    {
        var offsets = GetFaceOffsets(data);
        var result = new List<TrueTypeFont>(offsets.Length);
        foreach (var offset in offsets)
        {
            if (U32(data, offset) == TagOtto)
                continue; // CFF outlines are not supported for embedding.

            try
            {
                result.Add(Create(data, offset));
            }
            catch (NotSupportedException) when (offsets.Length > 1)
            {
                // Skip unsupported faces (e.g. bitmap-only) inside collections.
            }
        }

        return result;
    }

    public static TrueTypeFont Load(byte[] data, int faceIndex = 0)
    {
        var offsets = GetFaceOffsets(data);
        if ((uint)faceIndex >= (uint)offsets.Length)
            throw new ArgumentOutOfRangeException(nameof(faceIndex), $"Font file has {offsets.Length} face(s).");

        if (U32(data, offsets[faceIndex]) == TagOtto)
            throw new NotSupportedException("OpenType fonts with CFF outlines (.otf) are not supported yet. Use a TrueType-flavored font (.ttf).");

        return Create(data, offsets[faceIndex]);
    }

    /// <summary>Parses a face; malformed data surfaces as <see cref="InvalidDataException"/>.</summary>
    private static TrueTypeFont Create(byte[] data, int faceOffset)
    {
        try
        {
            return new TrueTypeFont(data, faceOffset);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Malformed font data.", e);
        }
    }

    /// <summary>
    /// Reads only the naming/style tables of each face. Used to index system fonts without paying for a full parse.
    /// <paramref name="read"/> reads <c>count</c> bytes at <c>offset</c> of the underlying file.
    /// </summary>
    public static List<FontFaceInfo> ReadFaceInfos(Func<long, int, byte[]> read)
    {
        var result = new List<FontFaceInfo>();
        var header = read(0, 12);
        if (header.Length < 12)
            return result;

        int[] faceOffsets;
        if (U32(header, 0) == TagTtcf)
        {
            var count = (int)Math.Min(U32(header, 8), 256);
            var offsetBytes = read(12, count * 4);
            faceOffsets = new int[count];
            for (var i = 0; i < count; i++)
                faceOffsets[i] = (int)U32(offsetBytes, i * 4);
        }
        else
        {
            faceOffsets = [0];
        }

        foreach (var faceOffset in faceOffsets)
        {
            var faceHeader = read(faceOffset, 12);
            if (faceHeader.Length < 12)
                continue;

            var isCff = U32(faceHeader, 0) == TagOtto;
            var numTables = U16(faceHeader, 4);
            var directory = read(faceOffset + 12, numTables * 16);
            if (directory.Length < numTables * 16)
                continue;

            var tables = new Dictionary<uint, (int, int)>();
            for (var i = 0; i < numTables; i++)
            {
                var tag = U32(directory, i * 16);
                var entry = ((int)U32(directory, i * 16 + 8), (int)U32(directory, i * 16 + 12));
                tables[tag] = entry;
            }

            if (!tables.TryGetValue(Tag("name"), out var nameEntry))
                continue;

            var nameData = read(nameEntry.Item1, nameEntry.Item2);
            var names = ReadNames(nameData, new Dictionary<uint, (int, int)> { [Tag("name")] = (0, nameData.Length) });

            var weight = 400;
            var italic = false;
            if (tables.TryGetValue(Tag("OS/2"), out var os2Entry) && os2Entry.Item2 >= 64)
            {
                var os2 = read(os2Entry.Item1, 64);
                weight = U16(os2, 4);
                italic = (U16(os2, 62) & 1) != 0;
            }

            if (tables.TryGetValue(Tag("head"), out var headEntry))
            {
                var head = read(headEntry.Item1, 54);
                italic |= head.Length >= 46 && (U16(head, 44) & 2) != 0;
            }

            var hasOutlines = !isCff && tables.ContainsKey(Tag("glyf")) && tables.ContainsKey(Tag("loca"));
            result.Add(new FontFaceInfo(names.Family, weight, italic, hasOutlines));
        }

        return result;
    }

    public ushort GetGlyph(int codepoint)
    {
        ushort glyph;
        if ((uint)codepoint <= 0xFFFF)
            glyph = _bmpGlyphs[codepoint];
        else if (_supplementaryGlyphs == null || !_supplementaryGlyphs.TryGetValue(codepoint, out glyph))
            return 0;

        // Ignore mappings to glyphs that don't exist in the font.
        return glyph < GlyphCount ? glyph : (ushort)0;
    }

    public int GetAdvance(ushort glyph) => glyph < _advances.Length ? _advances[glyph] : 0;

    private int Table(string tag, int minLength)
    {
        if (!TryTable(tag, out var offset, out var length) || length < minLength)
            throw new InvalidDataException($"Invalid font: the required '{tag}' table is missing or too short.");
        return offset;
    }

    public bool TryTable(string tag, out int offset, out int length)
    {
        if (_tables.TryGetValue(Tag(tag), out var entry))
        {
            (offset, length) = entry;
            return true;
        }

        offset = length = 0;
        return false;
    }

    private static int[] GetFaceOffsets(byte[] data)
    {
        if (data.Length < 12)
            throw new InvalidDataException("Font data is too short.");

        if (U32(data, 0) != TagTtcf)
            return [0];

        var count = U32(data, 8);
        if (count == 0 || count > (uint)(data.Length - 12) / 4)
            throw new InvalidDataException("Invalid font collection header.");

        var offsets = new int[count];
        for (var i = 0; i < offsets.Length; i++)
        {
            var offset = U32(data, 12 + i * 4);
            if (offset > (uint)data.Length - 12)
                throw new InvalidDataException("Invalid font collection header.");
            offsets[i] = (int)offset;
        }

        return offsets;
    }

    private static Dictionary<uint, (int, int)> ReadTableDirectory(byte[] data, int faceOffset)
    {
        var numTables = Math.Min((int)U16(data, faceOffset + 4), (data.Length - faceOffset - 12) / 16);
        var tables = new Dictionary<uint, (int, int)>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var record = faceOffset + 12 + i * 16;
            var offset = U32(data, record + 8);
            var length = U32(data, record + 12);
            if ((ulong)offset + length > (ulong)data.Length)
                throw new InvalidDataException("Invalid font: a table lies outside the file (truncated or corrupt).");
            tables[U32(data, record)] = ((int)offset, (int)length);
        }

        return tables;
    }

    private static ushort[] ReadAdvances(byte[] data, int hmtx, int numberOfHMetrics, int glyphCount)
    {
        var advances = new ushort[Math.Max(glyphCount, numberOfHMetrics)];
        ushort last = 0;
        for (var i = 0; i < advances.Length; i++)
        {
            if (i < numberOfHMetrics)
                last = U16(data, hmtx + i * 4);
            advances[i] = last;
        }

        return advances;
    }

    private static (ushort[] Bmp, Dictionary<int, ushort>? Supplementary) ReadCharacterMap(byte[] data, int cmap, int cmapLength)
    {
        // Every codepoint can be assigned at most a few times; this bounds the work for crafted tables.
        const int MaxAssignments = 0x110000 * 2;

        var bmp = new ushort[0x10000];
        Dictionary<int, ushort>? supplementary = null;
        var cmapEnd = cmap + cmapLength;

        if (cmapLength < 4)
            throw new InvalidDataException("Invalid font: the 'cmap' table is missing or too short.");

        var numSubtables = Math.Min((int)U16(data, cmap + 2), (cmapLength - 4) / 8);
        int best = -1, bestScore = -1, bestEnd = 0;
        var symbol = false;

        for (var i = 0; i < numSubtables; i++)
        {
            var record = cmap + 4 + i * 8;
            var platform = U16(data, record);
            var encoding = U16(data, record + 2);
            var offset = U32(data, record + 4);
            if (offset > (uint)cmapLength - 8)
                continue;

            var subtable = cmap + (int)offset;
            var format = U16(data, subtable);
            var length = format == 12 ? U32(data, subtable + 4) : U16(data, subtable + 2);
            var subtableEnd = (int)Math.Min((long)subtable + length, cmapEnd);

            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (0, _, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                (3, 0, 4) => 1,
                _ => -1,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = subtable;
                bestEnd = subtableEnd;
                symbol = platform == 3 && encoding == 0;
            }
        }

        if (best < 0)
            throw new NotSupportedException("The font has no supported Unicode character map (cmap format 4 or 12).");

        var assignments = 0;

        if (U16(data, best) == 4)
        {
            var segCount = Math.Min(U16(data, best + 6) / 2, (bestEnd - best - 16) / 8);
            var endCodes = best + 14;
            var startCodes = endCodes + segCount * 2 + 2;
            var deltas = startCodes + segCount * 2;
            var rangeOffsets = deltas + segCount * 2;

            for (var s = 0; s < segCount && assignments < MaxAssignments; s++)
            {
                int end = U16(data, endCodes + s * 2);
                int start = U16(data, startCodes + s * 2);
                var delta = I16(data, deltas + s * 2);
                var rangeOffsetPosition = rangeOffsets + s * 2;
                var rangeOffset = U16(data, rangeOffsetPosition);

                for (var c = start; c <= end && c != 0xFFFF && assignments < MaxAssignments; c++, assignments++)
                {
                    int glyph;
                    if (rangeOffset == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        var address = rangeOffsetPosition + rangeOffset + (c - start) * 2;
                        if (address + 2 > bestEnd)
                            continue;
                        glyph = U16(data, address);
                        if (glyph != 0)
                            glyph = (glyph + delta) & 0xFFFF;
                    }

                    bmp[c] = (ushort)glyph;
                }
            }
        }
        else
        {
            var groups = (int)Math.Min(U32(data, best + 12), (uint)Math.Max(0, (bestEnd - best - 16) / 12));
            for (var g = 0; g < groups && assignments < MaxAssignments; g++)
            {
                var group = best + 16 + g * 12;
                var start = U32(data, group);
                var end = U32(data, group + 4);
                var glyph = U32(data, group + 8);
                if (start > end || end > 0x10FFFF)
                    continue;

                for (var c = (int)start; c <= (int)end && assignments < MaxAssignments; c++, glyph++, assignments++)
                {
                    if (glyph > 0xFFFF)
                        break;

                    if (c <= 0xFFFF)
                    {
                        bmp[c] = (ushort)glyph;
                    }
                    else
                    {
                        supplementary ??= new Dictionary<int, ushort>();
                        supplementary[c] = (ushort)glyph;
                    }
                }
            }
        }

        // Symbol fonts map their characters to U+F000..U+F0FF; expose them at their plain codes too.
        if (symbol)
        {
            for (var c = 0; c <= 0xFF; c++)
            {
                if (bmp[c] == 0)
                    bmp[c] = bmp[0xF000 + c];
            }
        }

        return (bmp, supplementary);
    }

    private static (string Family, string Subfamily, string? PostScript) ReadNames(byte[] data, Dictionary<uint, (int Offset, int Length)> tables)
    {
        string? family = null, subfamily = null, typoFamily = null, typoSubfamily = null, postScript = null;

        if (tables.TryGetValue(Tag("name"), out var entry))
        {
            var table = entry.Offset;
            var count = U16(data, table + 2);
            var storage = table + U16(data, table + 4);

            // Pass 0: Windows Unicode English; pass 1: any Windows Unicode; pass 2: Mac Roman.
            for (var pass = 0; pass < 3; pass++)
            {
                for (var i = 0; i < count; i++)
                {
                    var record = table + 6 + i * 12;
                    if (record + 12 > data.Length)
                        break;

                    var platform = U16(data, record);
                    var encoding = U16(data, record + 2);
                    var language = U16(data, record + 4);
                    var nameId = U16(data, record + 6);
                    var length = U16(data, record + 8);
                    var offset = storage + U16(data, record + 10);
                    if (offset + length > data.Length)
                        continue;

                    var matches = pass switch
                    {
                        0 => platform == 3 && encoding is 1 or 10 && language == 0x409,
                        1 => platform == 3 && encoding is 1 or 10,
                        _ => platform == 1 && encoding == 0,
                    };
                    if (!matches)
                        continue;

                    string Decode() => pass < 2
                        ? Encoding.BigEndianUnicode.GetString(data, offset, length)
                        : Encoding.Latin1.GetString(data, offset, length);

                    switch (nameId)
                    {
                        case 1: family ??= Decode(); break;
                        case 2: subfamily ??= Decode(); break;
                        case 6: postScript ??= Decode(); break;
                        case 16: typoFamily ??= Decode(); break;
                        case 17: typoSubfamily ??= Decode(); break;
                    }
                }
            }
        }

        return ((typoFamily ?? family ?? "Unknown").Trim(), (typoSubfamily ?? subfamily ?? "Regular").Trim(), postScript?.Trim());
    }

    private static string SanitizePostScriptName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is > ' ' and < (char)127 and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%' or '#'))
                builder.Append(c);
        }

        return builder.Length == 0 ? "Font" : builder.ToString();
    }

    internal static uint Tag(string tag) =>
        (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);

    internal static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    internal static short I16(byte[] data, int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));
    internal static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    internal static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
}
