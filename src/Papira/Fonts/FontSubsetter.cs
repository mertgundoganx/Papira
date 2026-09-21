using System.Buffers.Binary;
using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>
/// Produces a minimal TrueType font program containing only the glyphs used in a document.
/// Glyph ids are preserved (unused glyphs become empty), so the PDF can use an identity CID-to-GID mapping.
/// </summary>
internal static class FontSubsetter
{
    private static readonly string[] CopiedTables = ["cvt ", "fpgm", "prep", "OS/2"];

    public static byte[] Subset(TrueTypeFont font, IReadOnlyCollection<ushort> usedGlyphs)
    {
        var data = font.Data;
        var glyphCount = font.GlyphCount;
        font.TryTable("loca", out var loca, out _);
        font.TryTable("glyf", out var glyf, out var glyfLength);

        long RawOffset(int glyph) => font.LongLocaFormat
            ? U32(data, loca + glyph * 4)
            : U16(data, loca + glyph * 2) * 2L;

        // Bounds of a glyph inside 'glyf'. Corrupt entries are treated as empty glyphs.
        (int Start, int End) GlyphRange(int glyph)
        {
            var start = RawOffset(glyph);
            var end = RawOffset(glyph + 1);
            return start <= end && end <= glyfLength ? ((int)start, (int)end) : (0, 0);
        }

        // Collect glyphs, including components referenced by composite glyphs.
        var keep = new bool[glyphCount];
        var pending = new Stack<int>();
        pending.Push(0);
        foreach (var glyph in usedGlyphs)
            pending.Push(glyph);

        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (glyph >= glyphCount || keep[glyph])
                continue;
            keep[glyph] = true;

            var (start, end) = GlyphRange(glyph);
            if (end - start < 10 || I16(data, glyf + start) >= 0)
                continue;

            var position = glyf + start + 10;
            var glyphEnd = glyf + end;
            while (position + 4 <= glyphEnd)
            {
                var flags = U16(data, position);
                pending.Push(U16(data, position + 2));
                position += 4;
                position += (flags & 0x0001) != 0 ? 4 : 2;
                if ((flags & 0x0008) != 0) position += 2;
                else if ((flags & 0x0040) != 0) position += 4;
                else if ((flags & 0x0080) != 0) position += 8;
                if ((flags & 0x0020) == 0)
                    break;
            }
        }

        // Build glyf and loca (long format).
        long glyfSize = 0;
        for (var g = 0; g < glyphCount; g++)
        {
            if (keep[g])
            {
                var (start, end) = GlyphRange(g);
                glyfSize += Align4(end - start);
            }
        }

        // Glyphs of a valid font don't overlap, so the subset can't exceed the original table (plus padding).
        if (glyfSize > glyfLength + 4L * glyphCount)
            throw new InvalidDataException("Malformed font data: overlapping glyph ranges.");

        var newGlyf = new byte[glyfSize];
        var newLoca = new byte[(glyphCount + 1) * 4];
        var cursor = 0;
        for (var g = 0; g < glyphCount; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)cursor);
            if (!keep[g])
                continue;

            var (start, end) = GlyphRange(g);
            var length = end - start;
            if (length > 0)
            {
                data.AsSpan(glyf + start, length).CopyTo(newGlyf.AsSpan(cursor));
                cursor += Align4(length);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(glyphCount * 4), (uint)cursor);

        var head = CopyTable(font, "head");
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0); // checkSumAdjustment, fixed up below
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // indexToLocFormat = long

        var tables = new List<(uint Tag, byte[] Data)>
        {
            (Tag("glyf"), newGlyf),
            (Tag("head"), head),
            (Tag("hhea"), CopyTable(font, "hhea")),
            (Tag("hmtx"), CopyTable(font, "hmtx")),
            (Tag("loca"), newLoca),
            (Tag("maxp"), CopyTable(font, "maxp")),
        };

        foreach (var tag in CopiedTables)
        {
            if (font.TryTable(tag, out _, out _))
                tables.Add((Tag(tag), CopyTable(font, tag)));
        }

        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        return Assemble(tables);
    }

    private static byte[] Assemble(List<(uint Tag, byte[] Data)> tables)
    {
        var count = tables.Count;
        var headerSize = 12 + count * 16;
        var total = headerSize + tables.Sum(t => Align4(t.Data.Length));
        var output = new byte[total];
        var span = output.AsSpan();

        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var searchRange = (1 << entrySelector) * 16;
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(count * 16 - searchRange));

        var offset = headerSize;
        var headOffset = -1;
        for (var i = 0; i < count; i++)
        {
            var (tag, data) = tables[i];
            var record = span[(12 + i * 16)..];
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            data.CopyTo(span[offset..]);
            if (tag == Tag("head"))
                headOffset = offset;
            offset += Align4(data.Length);
        }

        if (headOffset >= 0)
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], unchecked(0xB1B0AFBA - Checksum(output)));

        return output;
    }

    private static byte[] CopyTable(TrueTypeFont font, string tag)
    {
        font.TryTable(tag, out var offset, out var length);
        return font.Data.AsSpan(offset, length).ToArray();
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));

        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            tail.Clear();
            data[i..].CopyTo(tail);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(tail));
        }

        return sum;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
