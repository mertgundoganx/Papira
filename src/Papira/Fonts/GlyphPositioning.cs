using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>
/// The glyph positioning table (GPOS) of a font: where each glyph goes once the substitutions have chosen
/// it. This is what puts an accent over the letter it belongs to rather than beside it, joins the letters
/// of a cursive script at the right height, and kerns pairs.
/// </summary>
/// <remarks>
/// Lookup types 1 (single), 2 (pair), 3 (cursive), 4 (mark to base), 5 (mark to ligature), 6 (mark to
/// mark), 7 (context), 8 (chaining context) and 9 (extension) are applied. Device tables, which adjust
/// positions for a particular pixel size, are ignored: a PDF has no pixel grid to fit. A malformed table
/// is ignored rather than fatal.
/// </remarks>
internal sealed class GlyphPositioning : LayoutTable
{
    /// <summary>
    /// What Papira asks the font for. Unlike the substitutions these run in a single stage: the lookups
    /// of a font are written to be applied in the order the font lists them.
    /// </summary>
    public static readonly uint[][] Stages =
    [
        [Tag("curs"), Tag("kern"), Tag("dist"), Tag("mark"), Tag("mkmk"), Tag("abvm"), Tag("blwm")],
    ];

    public static readonly GlyphPositioning None = new([], [], new GlyphDefinitions([], [], []));

    private GlyphPositioning(LayoutLookup?[] lookups, Dictionary<long, int[]> features, GlyphDefinitions definitions)
        : base(lookups, features, definitions)
    {
    }

    public static GlyphPositioning Load(TrueTypeFont font)
    {
        try
        {
            if (!font.TryTable("GPOS", out var gpos, out var length))
                return None;

            return TryReadTable(font.Data, gpos, length, font.GlyphCount, 9, ReadSubtable, out var lookups, out var features)
                ? new GlyphPositioning(lookups, features, font.Definitions)
                : None;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            // A font with a broken positioning table is still usable, only unpositioned.
            return None;
        }
    }

    /// <summary>True when the font positions marks itself, so that they need no fallback placement.</summary>
    public bool HasMarks(uint script) => Has(script, Tag("mark")) || Has(script, Tag("abvm")) || Has(script, Tag("blwm"));

    /// <summary>True when the font kerns through its positioning table, so the kerning table is not used.</summary>
    public bool HasKerning(uint script) => !IsEmpty && (Has(script, Tag("kern")) || Has(script, Tag("dist")));

    /// <summary>
    /// Positions the glyphs of a run. Attachments are recorded while the lookups run and resolved
    /// afterwards, because a mark on a mark only knows where it goes once the mark below it does.
    /// </summary>
    public void Apply(uint script, ShapingBuffer buffer, bool rightToLeft)
    {
        RunStages(script, Stages, buffer);

        for (var i = 0; i < buffer.Length; i++)
            Propagate(buffer, rightToLeft, i);
    }

    /// <summary>
    /// Adds the position a glyph is attached to onto its own, walking the chain from mark to mark down to
    /// the base, and takes off the advances between them: a mark is placed relative to the glyph it
    /// belongs to, not to where the pen happens to stand.
    /// </summary>
    private static void Propagate(ShapingBuffer buffer, bool rightToLeft, int index)
    {
        ref var glyph = ref buffer[index];
        if (glyph.AttachType == AttachmentKind.None)
            return;

        var chain = glyph.AttachChain;
        var kind = glyph.AttachType;
        glyph.AttachType = AttachmentKind.None;
        glyph.AttachChain = 0;

        var target = index + chain;
        if (chain == 0 || target < 0 || target >= buffer.Length)
            return;

        // The glyph this one hangs on has to know its own place first.
        Propagate(buffer, rightToLeft, target);

        if (kind == AttachmentKind.Mark)
        {
            buffer[index].XOffset += buffer[target].XOffset;
            buffer[index].YOffset += buffer[target].YOffset;

            if (!rightToLeft)
            {
                for (var k = target; k < index; k++)
                    buffer[index].XOffset -= Advance(buffer, k);
            }
            else
            {
                for (var k = target + 1; k <= index; k++)
                    buffer[index].XOffset += Advance(buffer, k);
            }
        }
        else
        {
            // Cursive attachment only carries the height across; the advances were adjusted in place.
            buffer[index].YOffset = (short)(buffer[target].YOffset + buffer[index].YOffset);
        }
    }

    /// <summary>
    /// Places the marks of a run that the font itself says nothing about. Fonts written before mark
    /// attachment was common leave an accent where the pen stands, which draws it beside the letter
    /// instead of on it; here each mark is centred over the letter it belongs to and lifted clear of it.
    /// </summary>
    public static void PlaceMarksWithoutRules(ShapingBuffer buffer, TrueTypeFont font)
    {
        var gap = font.UnitsPerEm / 16;
        for (var i = 1; i < buffer.Length; i++)
        {
            if (!buffer[i].Mark || buffer[i].AttachType != AttachmentKind.None ||
                buffer[i].BaseAdvance + buffer[i].XAdvance != 0 ||
                buffer[i].XOffset != 0 || buffer[i].YOffset != 0)
            {
                continue;
            }

            // The letter the mark belongs to is the last glyph that is not itself a mark.
            var baseIndex = i - 1;
            while (baseIndex > 0 && buffer[baseIndex].Mark)
                baseIndex--;

            if (buffer[baseIndex].Mark ||
                !font.TryGlyphBounds(buffer[baseIndex].Glyph, out var baseLeft, out var baseBottom, out var baseRight, out var baseTop) ||
                !font.TryGlyphBounds(buffer[i].Glyph, out var markLeft, out var markBottom, out var markRight, out var markTop))
            {
                continue;
            }

            // Everything between the letter and the mark has already moved the pen along.
            var pen = 0;
            for (var k = baseIndex; k < i; k++)
                pen += buffer[k].BaseAdvance + buffer[k].XAdvance;

            var centre = baseLeft + ((baseRight - baseLeft) / 2);
            var markCentre = markLeft + ((markRight - markLeft) / 2);
            buffer[i].XOffset = (short)Math.Clamp(centre - markCentre - pen, short.MinValue, short.MaxValue);

            // A mark drawn below the baseline goes under the letter, anything else above it.
            buffer[i].YOffset = markTop <= 0
                ? (short)Math.Clamp(baseBottom - gap - markTop, short.MinValue, short.MaxValue)
                : (short)Math.Clamp(baseTop + gap - markBottom, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>The advance of a glyph as it stands: what the font gives it, plus what positioning added.</summary>
    private static short Advance(ShapingBuffer buffer, int index) =>
        (short)Math.Clamp(buffer[index].BaseAdvance + buffer[index].XAdvance, short.MinValue, short.MaxValue);

    // ---- Reading -----------------------------------------------------------------------------------

    private static LayoutSubtable? ReadSubtable(byte[] data, int subtable, int type, int glyphCount)
    {
        var format = U16(data, subtable);
        return type switch
        {
            1 => ReadSingle(data, subtable, format, glyphCount),
            2 => ReadPair(data, subtable, format, glyphCount),
            3 => ReadCursive(data, subtable, format, glyphCount),
            4 => ReadMarkToBase(data, subtable, format, glyphCount),
            5 => ReadMarkToLigature(data, subtable, format, glyphCount),
            6 => ReadMarkToMark(data, subtable, format, glyphCount),
            7 => ReadContext(data, subtable, format, glyphCount, false),
            8 => ReadContext(data, subtable, format, glyphCount, true),
            _ => null,
        };
    }

    /// <summary>How many bytes a value record of this format takes.</summary>
    private static int ValueSize(int format) => System.Numerics.BitOperations.PopCount((uint)(format & 0xFFFF)) * 2;

    /// <summary>
    /// A value record: how far the glyph moves and how much its advance changes. The device tables that
    /// may follow are counted but not read.
    /// </summary>
    private static Value ReadValue(byte[] data, int offset, int format)
    {
        short x = 0, y = 0, xAdvance = 0, yAdvance = 0;
        var position = offset;

        if ((format & 0x0001) != 0)
        {
            x = (short)U16(data, position);
            position += 2;
        }

        if ((format & 0x0002) != 0)
        {
            y = (short)U16(data, position);
            position += 2;
        }

        if ((format & 0x0004) != 0)
        {
            xAdvance = (short)U16(data, position);
            position += 2;
        }

        if ((format & 0x0008) != 0)
            yAdvance = (short)U16(data, position);

        _ = yAdvance; // Papira lays text out horizontally.
        return new Value(x, y, xAdvance);
    }

    /// <summary>An anchor: the point of a glyph another glyph is fastened to.</summary>
    private static Anchor ReadAnchor(byte[] data, int anchor)
    {
        if (anchor == 0)
            return Anchor.None;

        // Format 2 names a contour point as well and format 3 device tables; both place the anchor at
        // the same coordinates, and what they add only matters when fitting to a pixel grid.
        return new Anchor((short)U16(data, anchor + 2), (short)U16(data, anchor + 4), true);
    }

    private static SingleSubtable? ReadSingle(byte[] data, int subtable, int format, int glyphCount)
    {
        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var valueFormat = U16(data, subtable + 4);

        if (format == 1)
            return new SingleSubtable(coverage, [ReadValue(data, subtable + 6, valueFormat)]);

        if (format != 2)
            return null;

        var count = U16(data, subtable + 6);
        var size = ValueSize(valueFormat);
        var values = new Value[count];
        for (var i = 0; i < count; i++)
            values[i] = ReadValue(data, subtable + 8 + i * size, valueFormat);

        return new SingleSubtable(coverage, values);
    }

    private static LayoutSubtable? ReadPair(byte[] data, int subtable, int format, int glyphCount)
    {
        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        int first = U16(data, subtable + 4), second = U16(data, subtable + 6);
        int firstSize = ValueSize(first), secondSize = ValueSize(second);

        if (format == 1)
        {
            var setCount = U16(data, subtable + 8);
            var sets = new Pair[setCount][];
            for (var i = 0; i < setCount; i++)
            {
                var set = subtable + U16(data, subtable + 10 + i * 2);
                var count = U16(data, set);
                var pairs = new Pair[count];
                for (var p = 0; p < count; p++)
                {
                    var record = set + 2 + p * (2 + firstSize + secondSize);
                    pairs[p] = new Pair(
                        U16(data, record),
                        ReadValue(data, record + 2, first),
                        ReadValue(data, record + 2 + firstSize, second));
                }

                Array.Sort(pairs, (left, right) => left.Second.CompareTo(right.Second));
                sets[i] = pairs;
            }

            return new PairSubtable(coverage, sets, second != 0);
        }

        if (format != 2)
            return null;

        var firstClasses = ReadClassDef(data, subtable + U16(data, subtable + 8), glyphCount);
        var secondClasses = ReadClassDef(data, subtable + U16(data, subtable + 10), glyphCount);
        int firstCount = U16(data, subtable + 12), secondCount = U16(data, subtable + 14);

        // A crafted font could claim a grid of billions of cells.
        if ((long)firstCount * secondCount > 1_000_000)
            return null;

        var values = new Value[firstCount * secondCount * 2];
        var cell = firstSize + secondSize;
        for (var f = 0; f < firstCount; f++)
        {
            for (var s = 0; s < secondCount; s++)
            {
                var record = subtable + 16 + (f * secondCount + s) * cell;
                values[(f * secondCount + s) * 2] = ReadValue(data, record, first);
                values[(f * secondCount + s) * 2 + 1] = ReadValue(data, record + firstSize, second);
            }
        }

        return new ClassPairSubtable(coverage, firstClasses, secondClasses, secondCount, values, second != 0);
    }

    private static CursiveSubtable? ReadCursive(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var count = U16(data, subtable + 4);
        var entries = new Anchor[count];
        var exits = new Anchor[count];
        for (var i = 0; i < count; i++)
        {
            var record = subtable + 6 + i * 4;
            var entry = U16(data, record);
            var exit = U16(data, record + 2);
            entries[i] = entry == 0 ? Anchor.None : ReadAnchor(data, subtable + entry);
            exits[i] = exit == 0 ? Anchor.None : ReadAnchor(data, subtable + exit);
        }

        return new CursiveSubtable(coverage, entries, exits);
    }

    /// <summary>The marks of a lookup: which class each one belongs to, and where it is fastened.</summary>
    private static (int[] Coverage, MarkRecord[] Marks) ReadMarks(byte[] data, int subtable, int coverageOffset, int arrayOffset, int glyphCount)
    {
        var coverage = ReadCoverage(data, subtable + coverageOffset, glyphCount);
        var array = subtable + arrayOffset;
        var count = U16(data, array);
        var marks = new MarkRecord[count];
        for (var i = 0; i < count; i++)
        {
            var record = array + 2 + i * 4;
            var anchor = U16(data, record + 2);
            marks[i] = new MarkRecord(U16(data, record), anchor == 0 ? Anchor.None : ReadAnchor(data, array + anchor));
        }

        return (coverage, marks);
    }

    /// <summary>A table of anchors: one row per glyph, one column per mark class.</summary>
    private static Anchor[] ReadAnchorGrid(byte[] data, int array, int rows, int columns)
    {
        if ((long)rows * columns > 1_000_000)
            throw new InvalidDataException("The font has a larger mark table than Papira supports.");

        var anchors = new Anchor[rows * columns];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var offset = U16(data, array + 2 + (r * columns + c) * 2);
                anchors[r * columns + c] = offset == 0 ? Anchor.None : ReadAnchor(data, array + offset);
            }
        }

        return anchors;
    }

    private static MarkToBaseSubtable? ReadMarkToBase(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var (markCoverage, marks) = ReadMarks(data, subtable, U16(data, subtable + 2), U16(data, subtable + 8), glyphCount);
        var baseCoverage = ReadCoverage(data, subtable + U16(data, subtable + 4), glyphCount);
        var classes = U16(data, subtable + 6);
        var baseArray = subtable + U16(data, subtable + 10);
        var anchors = ReadAnchorGrid(data, baseArray, U16(data, baseArray), classes);

        return new MarkToBaseSubtable(markCoverage, marks, baseCoverage, anchors, classes);
    }

    private static MarkToLigatureSubtable? ReadMarkToLigature(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var (markCoverage, marks) = ReadMarks(data, subtable, U16(data, subtable + 2), U16(data, subtable + 8), glyphCount);
        var ligatureCoverage = ReadCoverage(data, subtable + U16(data, subtable + 4), glyphCount);
        var classes = U16(data, subtable + 6);
        var ligatureArray = subtable + U16(data, subtable + 10);
        var ligatureCount = U16(data, ligatureArray);

        // One table of anchors per ligature, with a row per component of it.
        var attachments = new Anchor[ligatureCount][];
        var components = new int[ligatureCount];
        for (var i = 0; i < ligatureCount; i++)
        {
            var attach = ligatureArray + U16(data, ligatureArray + 2 + i * 2);
            components[i] = U16(data, attach);
            attachments[i] = ReadAnchorGrid(data, attach, components[i], classes);
        }

        return new MarkToLigatureSubtable(markCoverage, marks, ligatureCoverage, attachments, components, classes);
    }

    private static MarkToMarkSubtable? ReadMarkToMark(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var (markCoverage, marks) = ReadMarks(data, subtable, U16(data, subtable + 2), U16(data, subtable + 8), glyphCount);
        var baseCoverage = ReadCoverage(data, subtable + U16(data, subtable + 4), glyphCount);
        var classes = U16(data, subtable + 6);
        var baseArray = subtable + U16(data, subtable + 10);
        var anchors = ReadAnchorGrid(data, baseArray, U16(data, baseArray), classes);

        return new MarkToMarkSubtable(markCoverage, marks, baseCoverage, anchors, classes);
    }

    // ---- Lookups -----------------------------------------------------------------------------------

    private readonly record struct Value(short X, short Y, short XAdvance)
    {
        public bool IsZero => X == 0 && Y == 0 && XAdvance == 0;

        public void ApplyTo(ref ShapedGlyph glyph)
        {
            glyph.XOffset += X;
            glyph.YOffset += Y;
            glyph.XAdvance += XAdvance;
        }
    }

    private readonly record struct Anchor(short X, short Y, bool Exists)
    {
        public static Anchor None => default;
    }

    private readonly record struct Pair(ushort Second, Value First, Value Value);

    private readonly record struct MarkRecord(ushort Class, Anchor Anchor);

    /// <summary>Type 1: one glyph is moved, or its advance changed.</summary>
    private sealed class SingleSubtable(int[] coverage, Value[] values) : LayoutSubtable
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0)
                return 0;

            values[values.Length == 1 ? 0 : Math.Min(index, values.Length - 1)].ApplyTo(ref buffer[position]);
            return 1;
        }
    }

    /// <summary>Type 2, format 1: pairs listed glyph by glyph.</summary>
    private sealed class PairSubtable(int[] coverage, Pair[][] sets, bool positionsSecond) : LayoutSubtable
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= sets.Length)
                return 0;

            var next = owner.Next(buffer, position, flags, markSet);
            if (next < 0)
                return 0;

            var pairs = sets[index];
            var low = 0;
            var high = pairs.Length - 1;
            var second = buffer[next].Glyph;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (pairs[middle].Second < second)
                    low = middle + 1;
                else if (pairs[middle].Second > second)
                    high = middle - 1;
                else
                {
                    pairs[middle].First.ApplyTo(ref buffer[position]);
                    pairs[middle].Value.ApplyTo(ref buffer[next]);
                    return positionsSecond ? next - position + 1 : Math.Max(next - position, 1);
                }
            }

            return 0;
        }
    }

    /// <summary>Type 2, format 2: pairs listed by the classes the two glyphs belong to.</summary>
    private sealed class ClassPairSubtable(
        int[] coverage,
        ushort[] firstClasses,
        ushort[] secondClasses,
        int secondCount,
        Value[] values,
        bool positionsSecond) : LayoutSubtable
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            if (Index(coverage, buffer[position].Glyph) < 0)
                return 0;

            var next = owner.Next(buffer, position, flags, markSet);
            if (next < 0)
                return 0;

            var first = Class(firstClasses, buffer[position].Glyph);
            var second = Class(secondClasses, buffer[next].Glyph);
            var cell = (first * secondCount + second) * 2;
            if (second >= secondCount || cell + 1 >= values.Length)
                return 0;

            values[cell].ApplyTo(ref buffer[position]);
            values[cell + 1].ApplyTo(ref buffer[next]);
            return positionsSecond ? next - position + 1 : Math.Max(next - position, 1);
        }

        private static int Class(ushort[] classes, ushort glyph) => glyph < classes.Length ? classes[glyph] : 0;
    }

    /// <summary>Type 3: the letters of a joining script are fastened to one another.</summary>
    private sealed class CursiveSubtable(int[] coverage, Anchor[] entries, Anchor[] exits) : LayoutSubtable
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= exits.Length || !exits[index].Exists)
                return 0;

            var next = owner.Next(buffer, position, flags, markSet);
            if (next < 0)
                return 0;

            var nextIndex = Index(coverage, buffer[next].Glyph);
            if (nextIndex < 0 || nextIndex >= entries.Length || !entries[nextIndex].Exists)
                return 0;

            var exit = exits[index];
            var entry = entries[nextIndex];

            // The exit of one letter and the entry of the next become the same point: the advance of the
            // first is cut back to its exit, and the second starts from its entry.
            buffer[position].XAdvance = (short)(exit.X + buffer[position].XOffset - Advance(buffer, position));
            var shift = entry.X + buffer[next].XOffset;
            buffer[next].XAdvance -= (short)shift;
            buffer[next].XOffset -= (short)shift;

            // Which of the two moves vertically depends on the direction the lookup is written for.
            if ((flags & 0x0001) != 0)
            {
                buffer[position].AttachType = AttachmentKind.Cursive;
                buffer[position].AttachChain = (short)(next - position);
                buffer[position].YOffset = (short)(entry.Y - exit.Y);
            }
            else
            {
                buffer[next].AttachType = AttachmentKind.Cursive;
                buffer[next].AttachChain = (short)(position - next);
                buffer[next].YOffset = (short)(exit.Y - entry.Y);
            }

            return next - position;
        }

        /// <summary>The advance the glyph has so far, which the attachment replaces.</summary>
        private static short Advance(ShapingBuffer buffer, int position) =>
            (short)(buffer[position].BaseAdvance + buffer[position].XAdvance);
    }

    /// <summary>What the mark lookups have in common: fastening a mark to an anchor of another glyph.</summary>
    private abstract class MarkSubtable(int[] markCoverage, MarkRecord[] marks, int[] baseCoverage, int classes) : LayoutSubtable
    {
        protected int MarkIndex(ushort glyph) => Index(markCoverage, glyph);

        protected int BaseIndex(ushort glyph) => Index(baseCoverage, glyph);

        protected int Classes => classes;

        /// <summary>Moves the mark so that its own anchor meets the one of the glyph it belongs to.</summary>
        protected bool Attach(ShapingBuffer buffer, int mark, int index, int target, Anchor[] anchors, int row)
        {
            if (index < 0 || index >= marks.Length || !marks[index].Anchor.Exists)
                return false;

            var column = marks[index].Class;
            var cell = row * classes + column;
            if (column >= classes || cell < 0 || cell >= anchors.Length || !anchors[cell].Exists)
                return false;

            buffer[mark].XOffset = (short)(anchors[cell].X - marks[index].Anchor.X);
            buffer[mark].YOffset = (short)(anchors[cell].Y - marks[index].Anchor.Y);
            buffer[mark].AttachType = AttachmentKind.Mark;
            buffer[mark].AttachChain = (short)(target - mark);
            return true;
        }
    }

    /// <summary>Type 4: a mark on a letter.</summary>
    private sealed class MarkToBaseSubtable(int[] markCoverage, MarkRecord[] marks, int[] baseCoverage, Anchor[] anchors, int classes)
        : MarkSubtable(markCoverage, marks, baseCoverage, classes)
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = MarkIndex(buffer[position].Glyph);
            if (index < 0)
                return 0;

            // Whatever the lookup skips otherwise, the letter a mark belongs to is the last one that is
            // not itself a mark.
            var previous = owner.Previous(buffer, position, (ushort)((flags & ~0x000E) | 0x0008), -1);
            if (previous < 0)
                return 0;

            var row = BaseIndex(buffer[previous].Glyph);
            return row >= 0 && Attach(buffer, position, index, previous, anchors, row) ? 1 : 0;
        }
    }

    /// <summary>Type 5: a mark on one component of a ligature.</summary>
    private sealed class MarkToLigatureSubtable(
        int[] markCoverage,
        MarkRecord[] marks,
        int[] ligatureCoverage,
        Anchor[][] attachments,
        int[] components,
        int classes) : MarkSubtable(markCoverage, marks, ligatureCoverage, classes)
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = MarkIndex(buffer[position].Glyph);
            if (index < 0)
                return 0;

            var previous = owner.Previous(buffer, position, (ushort)((flags & ~0x000E) | 0x0008), -1);
            if (previous < 0)
                return 0;

            var ligature = BaseIndex(buffer[previous].Glyph);
            if (ligature < 0 || ligature >= attachments.Length || components[ligature] == 0)
                return 0;

            // Which part of the ligature the mark belongs to follows from the character it came from.
            var component = Math.Clamp(buffer[position].Cluster - buffer[previous].Cluster, 0, components[ligature] - 1);
            return Attach(buffer, position, index, previous, attachments[ligature], component) ? 1 : 0;
        }
    }

    /// <summary>Type 6: a mark on another mark, as a second accent above the first.</summary>
    private sealed class MarkToMarkSubtable(int[] markCoverage, MarkRecord[] marks, int[] baseCoverage, Anchor[] anchors, int classes)
        : MarkSubtable(markCoverage, marks, baseCoverage, classes)
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = MarkIndex(buffer[position].Glyph);
            if (index < 0)
                return 0;

            // The mark below is the glyph just before, marks included.
            var previous = owner.Previous(buffer, position, (ushort)(flags & ~0x000E), markSet);
            if (previous < 0 || !buffer[previous].Mark)
                return 0;

            var row = BaseIndex(buffer[previous].Glyph);
            return row >= 0 && Attach(buffer, position, index, previous, anchors, row) ? 1 : 0;
        }
    }
}
