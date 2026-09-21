using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>
/// Pair kerning of a font, read from the OpenType GPOS 'kern' feature (pair adjustment lookups) or,
/// for older fonts, from the legacy 'kern' table. Values are horizontal advance adjustments in font units.
/// Malformed or oversized tables disable kerning instead of failing the font.
/// </summary>
internal sealed class KerningTable
{
    // Limits against crafted fonts; real fonts stay far below them.
    private const int MaxPairs = 1_000_000;
    private const int MaxClassCells = 1_000_000;

    public static readonly KerningTable None = new([], 0);

    // Lookups are applied cumulatively; inside a lookup the first subtable that applies wins.
    private readonly Lookup[] _lookups;

    private KerningTable(PairSubtable[][] lookups, int glyphCount)
    {
        _lookups = new Lookup[lookups.Length];
        for (var i = 0; i < lookups.Length; i++)
            _lookups[i] = new Lookup(lookups[i], glyphCount);
    }

    public bool IsEmpty => _lookups.Length == 0;

    // Recently used pairs, per thread. The full tables are a few hundred kilobytes and fall out of the CPU cache
    // during layout, while text repeats the same pairs constantly; a small cache keeps lookups fast.
    [ThreadStatic]
    private static CacheEntry[]? t_cache;

    private struct CacheEntry
    {
        public KerningTable? Table;
        public uint Pair;
        public int Value;
    }

    /// <summary>Adjustment of the advance of <paramref name="left"/> when followed by <paramref name="right"/>, in font units.</summary>
    public int Get(ushort left, ushort right)
    {
        if (_lookups.Length == 0)
            return 0;

        var pair = ((uint)left << 16) | right;
        var cache = t_cache ??= new CacheEntry[1024];
        ref var entry = ref cache[(int)((pair * 2654435761u) >> 22)];
        if (ReferenceEquals(entry.Table, this) && entry.Pair == pair)
            return entry.Value;

        var value = Compute(left, right);
        entry.Table = this;
        entry.Pair = pair;
        entry.Value = value;
        return value;
    }

    private int Compute(ushort left, ushort right)
    {
        var total = 0;
        foreach (var lookup in _lookups)
        {
            if (left >= lookup.FirstSubtable.Length)
                continue;

            var subtables = lookup.Subtables;
            for (int s = lookup.FirstSubtable[left]; s >= 0 && s < subtables.Length; s++)
            {
                if (subtables[s].TryGet(left, right, out var value))
                {
                    total += value;
                    break;
                }
            }
        }

        return total;
    }

    /// <summary>Subtables of one lookup, with the first subtable covering each glyph precomputed.</summary>
    private sealed class Lookup
    {
        public Lookup(PairSubtable[] subtables, int glyphCount)
        {
            Subtables = subtables;
            FirstSubtable = new short[glyphCount];
            Array.Fill(FirstSubtable, (short)-1);
            for (var glyph = 0; glyph < glyphCount; glyph++)
            {
                for (var s = 0; s < subtables.Length; s++)
                {
                    if (subtables[s].Covers(glyph))
                    {
                        FirstSubtable[glyph] = (short)s;
                        break;
                    }
                }
            }
        }

        public PairSubtable[] Subtables { get; }
        public short[] FirstSubtable { get; }
    }

    public static KerningTable Load(TrueTypeFont font)
    {
        try
        {
            if (font.TryTable("GPOS", out var gpos, out var gposLength))
            {
                var table = ReadGpos(font.Data, gpos, gposLength, font.GlyphCount);
                if (!table.IsEmpty)
                    return table;
            }

            if (font.TryTable("kern", out var kern, out var kernLength))
                return ReadLegacyKern(font.Data, kern, kernLength, font.GlyphCount);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            // Malformed kerning data: render without kerning.
        }

        return None;
    }

    private static KerningTable ReadGpos(byte[] data, int gpos, int length, int glyphCount)
    {
        var end = gpos + length;
        var featureList = gpos + U16(data, gpos + 6);
        var lookupList = gpos + U16(data, gpos + 8);

        // Lookups referenced by any 'kern' feature (all scripts and languages).
        var lookupIndices = new SortedSet<int>();
        var featureCount = U16(data, featureList);
        for (var i = 0; i < featureCount; i++)
        {
            var record = featureList + 2 + i * 6;
            if (U32(data, record) != Tag("kern"))
                continue;

            var feature = featureList + U16(data, record + 4);
            var indexCount = U16(data, feature + 2);
            for (var k = 0; k < indexCount; k++)
                lookupIndices.Add(U16(data, feature + 4 + k * 2));
        }

        var lookupCount = U16(data, lookupList);
        var budget = new Budget();
        var lookups = new List<PairSubtable[]>();

        foreach (var index in lookupIndices)
        {
            if (index >= lookupCount)
                continue;

            var lookup = lookupList + U16(data, lookupList + 2 + index * 2);
            var type = U16(data, lookup);
            var subtableCount = U16(data, lookup + 4);
            var subtables = new List<PairSubtable>();

            for (var s = 0; s < subtableCount; s++)
            {
                var subtable = lookup + U16(data, lookup + 6 + s * 2);
                var subtableType = type;

                if (type == 9)
                {
                    subtableType = U16(data, subtable + 2);
                    subtable += (int)U32(data, subtable + 4);
                }

                if (subtableType != 2 || subtable < gpos || subtable >= end)
                    continue;

                PairSubtable? parsed = U16(data, subtable) switch
                {
                    1 => ReadPairSets(data, subtable, glyphCount, budget),
                    2 => ReadClassPairs(data, subtable, glyphCount, budget),
                    _ => null,
                };

                if (parsed != null)
                    subtables.Add(parsed);
            }

            if (subtables.Count > 0)
                lookups.Add([.. subtables]);
        }

        return new KerningTable([.. lookups], glyphCount);
    }

    /// <summary>PairPos format 1: explicit glyph pairs.</summary>
    private static PairSetSubtable? ReadPairSets(byte[] data, int subtable, int glyphCount, Budget budget)
    {
        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var format1 = U16(data, subtable + 4);
        var format2 = U16(data, subtable + 6);
        if ((format1 & 0x0004) == 0)
            return null; // no XAdvance on the first glyph: nothing for horizontal kerning

        var size1 = ValueRecordSize(format1);
        var recordSize = 2 + size1 + ValueRecordSize(format2);
        var xAdvance = XAdvanceOffset(format1);
        var pairSetCount = U16(data, subtable + 8);

        var builder = new PairSetSubtable.Builder(glyphCount);
        for (var glyph = 0; glyph < glyphCount; glyph++)
        {
            var index = coverage[glyph];
            if (index < 0 || index >= pairSetCount)
                continue;

            builder.Cover(glyph);
            var pairSet = subtable + U16(data, subtable + 10 + index * 2);
            var count = U16(data, pairSet);
            budget.Spend(count, MaxPairs);

            for (var p = 0; p < count; p++)
            {
                var record = pairSet + 2 + p * recordSize;

                // Zero values are kept: a matching pair stops the search in this lookup.
                builder.Add(glyph, U16(data, record), I16(data, record + 2 + xAdvance));
            }
        }

        return builder.Build();
    }

    /// <summary>PairPos format 2: adjustments between glyph classes.</summary>
    private static ClassPairSubtable? ReadClassPairs(byte[] data, int subtable, int glyphCount, Budget budget)
    {
        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var format1 = U16(data, subtable + 4);
        var format2 = U16(data, subtable + 6);
        if ((format1 & 0x0004) == 0)
            return null;

        var class1 = ReadClassDef(data, subtable + U16(data, subtable + 8), glyphCount);
        var class2 = ReadClassDef(data, subtable + U16(data, subtable + 10), glyphCount);
        var class1Count = U16(data, subtable + 12);
        var class2Count = U16(data, subtable + 14);
        budget.Spend(class1Count * class2Count, MaxClassCells);

        var recordSize = ValueRecordSize(format1) + ValueRecordSize(format2);
        var xAdvance = XAdvanceOffset(format1);
        var matrix = new short[class1Count * class2Count];
        for (var c1 = 0; c1 < class1Count; c1++)
        {
            for (var c2 = 0; c2 < class2Count; c2++)
            {
                var record = subtable + 16 + (c1 * class2Count + c2) * recordSize;
                matrix[c1 * class2Count + c2] = I16(data, record + xAdvance);
            }
        }

        var covered = new bool[glyphCount];
        for (var glyph = 0; glyph < glyphCount; glyph++)
            covered[glyph] = coverage[glyph] >= 0;

        return new ClassPairSubtable(covered, class1, class2, matrix, class1Count, class2Count);
    }

    private static KerningTable ReadLegacyKern(byte[] data, int kern, int length, int glyphCount)
    {
        if (U16(data, kern) != 0)
            return None; // Apple 'kern' (version 1) is not supported

        var end = kern + length;
        var tableCount = U16(data, kern + 2);
        var position = kern + 4;
        var builder = new PairSetSubtable.Builder(glyphCount);
        var budget = new Budget();

        for (var t = 0; t < tableCount && position + 6 <= end; t++)
        {
            var subtableLength = U16(data, position + 2);
            var coverage = U16(data, position + 4);

            // Format 0, horizontal, not cross-stream, not minimum values.
            if (coverage >> 8 == 0 && (coverage & 0x0F) == 0x01)
            {
                var count = U16(data, position + 6);
                budget.Spend(count, MaxPairs);
                for (var p = 0; p < count; p++)
                {
                    var record = position + 14 + p * 6;
                    var left = U16(data, record);
                    var right = U16(data, record + 2);
                    var value = I16(data, record + 4);
                    if (left < glyphCount && value != 0)
                    {
                        builder.Cover(left);
                        builder.Add(left, right, value);
                    }
                }
            }

            if (subtableLength == 0)
                break;
            position += subtableLength;
        }

        return builder.Count == 0 ? None : new KerningTable([[builder.Build()]], glyphCount);
    }

    /// <summary>Coverage index per glyph id, or -1 when the glyph is not covered.</summary>
    private static int[] ReadCoverage(byte[] data, int coverage, int glyphCount)
    {
        var indices = new int[glyphCount];
        Array.Fill(indices, -1);

        var format = U16(data, coverage);
        var count = U16(data, coverage + 2);
        if (format == 1)
        {
            for (var i = 0; i < count; i++)
            {
                var glyph = U16(data, coverage + 4 + i * 2);
                if (glyph < glyphCount)
                    indices[glyph] = i;
            }
        }
        else if (format == 2)
        {
            for (var r = 0; r < count; r++)
            {
                var record = coverage + 4 + r * 6;
                int start = U16(data, record), end = U16(data, record + 2), startIndex = U16(data, record + 4);
                for (var glyph = start; glyph <= end && glyph < glyphCount; glyph++)
                    indices[glyph] = startIndex + glyph - start;
            }
        }

        return indices;
    }

    /// <summary>Class value per glyph id (0 for glyphs not listed).</summary>
    private static ushort[] ReadClassDef(byte[] data, int classDef, int glyphCount)
    {
        var classes = new ushort[glyphCount];
        var format = U16(data, classDef);
        if (format == 1)
        {
            var start = U16(data, classDef + 2);
            var count = U16(data, classDef + 4);
            for (var i = 0; i < count && start + i < glyphCount; i++)
                classes[start + i] = U16(data, classDef + 6 + i * 2);
        }
        else if (format == 2)
        {
            var count = U16(data, classDef + 2);
            for (var r = 0; r < count; r++)
            {
                var record = classDef + 4 + r * 6;
                int start = U16(data, record), end = U16(data, record + 2);
                var value = U16(data, record + 4);
                for (var glyph = start; glyph <= end && glyph < glyphCount; glyph++)
                    classes[glyph] = value;
            }
        }

        return classes;
    }

    /// <summary>Each set bit of a ValueFormat adds one 16-bit field (values and device-table offsets alike).</summary>
    private static int ValueRecordSize(int format) => System.Numerics.BitOperations.PopCount((uint)(format & 0xFF)) * 2;

    /// <summary>Offset of XAdvance inside a value record: after XPlacement and YPlacement when present.</summary>
    private static int XAdvanceOffset(int format) => System.Numerics.BitOperations.PopCount((uint)(format & 0x0003)) * 2;

    private sealed class Budget
    {
        private long _spent;

        public void Spend(long amount, long limit)
        {
            _spent += amount;
            if (_spent > limit)
                throw new InvalidDataException("Kerning data exceeds the supported size.");
        }
    }

    private abstract class PairSubtable
    {
        public abstract bool Covers(int glyph);

        public abstract bool TryGet(ushort left, ushort right, out short value);
    }

    /// <summary>
    /// Explicit pairs grouped by first glyph: the second glyphs of each first glyph are sorted,
    /// so a lookup is a binary search in a short array.
    /// </summary>
    private sealed class PairSetSubtable : PairSubtable
    {
        private readonly bool[] _covered;
        private readonly int[] _start; // pairs of glyph g are at [_start[g], _start[g + 1])
        private readonly ushort[] _second;
        private readonly short[] _value;

        private PairSetSubtable(bool[] covered, int[] start, ushort[] second, short[] value)
        {
            _covered = covered;
            _start = start;
            _second = second;
            _value = value;
        }

        public override bool Covers(int glyph) => _covered[glyph];

        public override bool TryGet(ushort left, ushort right, out short value)
        {
            value = 0;
            if (!_covered[left])
                return false;

            var from = _start[left];
            var index = _second.AsSpan(from, _start[left + 1] - from).BinarySearch(right);
            if (index < 0)
                return false;

            value = _value[from + index];
            return true;
        }

        public sealed class Builder(int glyphCount)
        {
            private readonly bool[] _covered = new bool[glyphCount];
            private readonly List<(ushort First, ushort Second, short Value)> _pairs = [];

            public int Count => _pairs.Count;

            public void Cover(int glyph) => _covered[glyph] = true;

            public void Add(int first, ushort second, short value) => _pairs.Add(((ushort)first, second, value));

            public PairSetSubtable Build()
            {
                // Sort by (first, second, insertion order) packed into one key; the insertion order keeps the
                // first occurrence of a duplicate pair, as a sequential search would.
                var keys = new ulong[_pairs.Count];
                for (var i = 0; i < keys.Length; i++)
                    keys[i] = ((ulong)_pairs[i].First << 48) | ((ulong)_pairs[i].Second << 32) | (uint)i;
                Array.Sort(keys);

                var start = new int[glyphCount + 1];
                var second = new List<ushort>(keys.Length);
                var value = new List<short>(keys.Length);
                var previous = ulong.MaxValue;

                foreach (var key in keys)
                {
                    var pair = key >> 32;
                    if (pair == previous)
                        continue;
                    previous = pair;

                    var (first, next, adjustment) = _pairs[(int)(uint)key];
                    start[first + 1]++;
                    second.Add(next);
                    value.Add(adjustment);
                }

                for (var g = 0; g < glyphCount; g++)
                    start[g + 1] += start[g];

                return new PairSetSubtable(_covered, start, [.. second], [.. value]);
            }
        }
    }

    private sealed class ClassPairSubtable(bool[] covered, ushort[] class1, ushort[] class2, short[] matrix, int class1Count, int class2Count) : PairSubtable
    {
        public override bool Covers(int glyph) => covered[glyph];

        public override bool TryGet(ushort left, ushort right, out short value)
        {
            value = 0;
            if (left >= covered.Length || !covered[left])
                return false;

            // A covered first glyph makes this subtable apply, even when the adjustment is zero.
            int c1 = class1[left], c2 = right < class2.Length ? class2[right] : 0;
            if (c1 < class1Count && c2 < class2Count)
                value = matrix[c1 * class2Count + c2];
            return true;
        }
    }
}
