using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>
/// The glyph substitution table (GSUB) of a font: the rules that turn a sequence of characters into the
/// glyphs that are actually drawn. Papira uses it for cursive scripts, where every letter has an initial,
/// medial, final and isolated form, for the ligatures a script requires, such as lam-alef in Arabic, and
/// for the ones Latin fonts offer, such as fi and fl.
/// </summary>
/// <remarks>
/// Lookup types 1 (single), 2 (multiple), 3 (alternate), 4 (ligature), 5 (context), 6 (chaining context)
/// and 7 (extension) are applied. Reverse chaining substitution, which only Nastaliq style fonts need,
/// is not. A malformed table is ignored rather than fatal: the text is then drawn unshaped.
/// </remarks>
internal sealed class GlyphSubstitution : LayoutTable
{
    private const int MaxLigatures = 200_000;

    /// <summary>
    /// What Papira asks the font for when a script joins its letters, one stage at a time: the compositions
    /// first, then each positional form on its own — a later form is chosen from what the previous one
    /// produced — and finally the ligatures. Syriac has three more final and medial forms than Arabic.
    /// </summary>
    public static readonly uint[][] CursiveStages =
    [
        [Tag("rvrn")],
        [Tag("ccmp"), Tag("locl")],
        [Tag("isol")],
        [Tag("fina")],
        [Tag("fin2")],
        [Tag("fin3")],
        [Tag("medi")],
        [Tag("med2")],
        [Tag("init")],
        [Tag("rlig"), Tag("calt"), Tag("clig"), Tag("liga"), Tag("rclt"), Tag("mset")],
    ];

    /// <summary>Scripts that do not join still need their compositions and ligatures.</summary>
    public static readonly uint[][] SimpleStages =
    [
        [Tag("rvrn")],
        [Tag("ccmp"), Tag("locl")],
        [Tag("rlig"), Tag("calt"), Tag("clig"), Tag("liga"), Tag("rclt")],
    ];

    /// <summary>
    /// The same, without the ligatures a font offers of its own accord. Text whose letters are pulled
    /// apart is not joined into fi and fl, which is what browsers do as well; the ligatures a script
    /// requires (<c>rlig</c>) are formed all the same.
    /// </summary>
    public static readonly uint[][] CursiveStagesWithoutLigatures = Without(CursiveStages);

    public static readonly uint[][] SimpleStagesWithoutLigatures = Without(SimpleStages);

    private static uint[][] Without(uint[][] stages) =>
        [.. stages.Select(stage => stage.Where(feature => feature != Tag("liga") && feature != Tag("clig")).ToArray())];

    public static readonly GlyphSubstitution None = new([], [], new GlyphDefinitions([], [], []));

    private GlyphSubstitution(LayoutLookup?[] lookups, Dictionary<long, int[]> features, GlyphDefinitions definitions)
        : base(lookups, features, definitions)
    {
    }

    public static GlyphSubstitution Load(TrueTypeFont font)
    {
        try
        {
            if (!font.TryTable("GSUB", out var gsub, out var length))
                return None;

            return TryReadTable(font.Data, gsub, length, font.GlyphCount, 7, ReadSubtable, out var lookups, out var features)
                ? new GlyphSubstitution(lookups, features, font.Definitions)
                : None;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            // A font with a broken substitution table is still usable, only unshaped.
            return None;
        }
    }

    /// <summary>Applies the substitutions of a script to the buffer, stage by stage.</summary>
    public void Apply(uint script, uint[][] stages, ShapingBuffer buffer) => RunStages(script, stages, buffer);

    // ---- Reading -----------------------------------------------------------------------------------

    private static LayoutSubtable? ReadSubtable(byte[] data, int subtable, int type, int glyphCount)
    {
        var format = U16(data, subtable);
        return type switch
        {
            1 => ReadSingle(data, subtable, format, glyphCount),
            2 => ReadMultiple(data, subtable, format, glyphCount),
            3 => ReadAlternate(data, subtable, format, glyphCount),
            4 => ReadLigature(data, subtable, format, glyphCount),
            5 => ReadContext(data, subtable, format, glyphCount, false),
            6 => ReadContext(data, subtable, format, glyphCount, true),
            _ => null,
        };
    }

    private static SingleSubtable? ReadSingle(byte[] data, int subtable, int format, int glyphCount)
    {
        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        if (format == 1)
        {
            var delta = (short)U16(data, subtable + 4);
            return new SingleSubtable(coverage, null, delta);
        }

        if (format != 2)
            return null;

        var count = U16(data, subtable + 4);
        var glyphs = new ushort[count];
        for (var i = 0; i < count; i++)
            glyphs[i] = U16(data, subtable + 6 + i * 2);

        return new SingleSubtable(coverage, glyphs, 0);
    }

    private static MultipleSubtable? ReadMultiple(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var count = U16(data, subtable + 4);
        var sequences = new ushort[count][];
        for (var i = 0; i < count; i++)
        {
            var sequence = subtable + U16(data, subtable + 6 + i * 2);
            var glyphCountInSequence = Math.Min((int)U16(data, sequence), MaxSequenceLength);
            var glyphs = new ushort[glyphCountInSequence];
            for (var g = 0; g < glyphCountInSequence; g++)
                glyphs[g] = U16(data, sequence + 2 + g * 2);

            sequences[i] = glyphs;
        }

        return new MultipleSubtable(coverage, sequences);
    }

    private static AlternateSubtable? ReadAlternate(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var count = U16(data, subtable + 4);
        var alternates = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            var set = subtable + U16(data, subtable + 6 + i * 2);

            // Without a way to choose, the first alternate is the one the font recommends.
            alternates[i] = U16(data, set) > 0 ? U16(data, set + 2) : (ushort)0;
        }

        return new AlternateSubtable(coverage, alternates);
    }

    private static LigatureSubtable? ReadLigature(byte[] data, int subtable, int format, int glyphCount)
    {
        if (format != 1)
            return null;

        var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
        var setCount = U16(data, subtable + 4);
        var sets = new Ligature[setCount][];
        var total = 0;

        for (var i = 0; i < setCount; i++)
        {
            var set = subtable + U16(data, subtable + 6 + i * 2);
            var count = U16(data, set);
            total += count;
            if (total > MaxLigatures)
                throw new InvalidDataException("The font has more ligatures than Papira supports.");

            var ligatures = new Ligature[count];
            for (var l = 0; l < count; l++)
            {
                var ligature = set + U16(data, set + 2 + l * 2);
                var componentCount = Math.Min((int)U16(data, ligature + 2), MaxSequenceLength);
                var components = new ushort[Math.Max(componentCount - 1, 0)];
                for (var c = 0; c < components.Length; c++)
                    components[c] = U16(data, ligature + 4 + c * 2);

                ligatures[l] = new Ligature(U16(data, ligature), components);
            }

            // Longer ligatures win, so they are tried first.
            Array.Sort(ligatures, (left, right) => right.Components.Length.CompareTo(left.Components.Length));
            sets[i] = ligatures;
        }

        return new LigatureSubtable(coverage, sets);
    }

    // ---- Lookups -----------------------------------------------------------------------------------

    private readonly record struct Ligature(ushort Glyph, ushort[] Components);

    /// <summary>Type 1: one glyph for another.</summary>
    private sealed class SingleSubtable(int[] coverage, ushort[]? glyphs, short delta) : LayoutSubtable
    {
        public override bool WouldApply(ReadOnlySpan<ushort> input, bool zeroContext) =>
            input.Length == 1 && Index(coverage, input[0]) >= 0;

        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0)
                return 0;

            if (glyphs == null)
                buffer.Replace(position, (ushort)((buffer[position].Glyph + delta) & 0xFFFF));
            else if (index < glyphs.Length)
                buffer.Replace(position, glyphs[index]);
            else
                return 0;

            return 1;
        }
    }

    /// <summary>Type 2: one glyph becomes several.</summary>
    private sealed class MultipleSubtable(int[] coverage, ushort[][] sequences) : LayoutSubtable
    {
        public override bool WouldApply(ReadOnlySpan<ushort> input, bool zeroContext) =>
            input.Length == 1 && Index(coverage, input[0]) >= 0;

        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= sequences.Length || sequences[index].Length == 0)
                return 0;

            buffer.Expand(position, sequences[index]);
            return sequences[index].Length;
        }
    }

    /// <summary>Type 3: a choice of glyphs, of which the first is taken.</summary>
    private sealed class AlternateSubtable(int[] coverage, ushort[] alternates) : LayoutSubtable
    {
        public override bool WouldApply(ReadOnlySpan<ushort> input, bool zeroContext) =>
            input.Length == 1 && Index(coverage, input[0]) >= 0;

        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= alternates.Length || alternates[index] == 0)
                return 0;

            buffer.Replace(position, alternates[index]);
            return 1;
        }
    }

    /// <summary>Type 4: several glyphs become one.</summary>
    private sealed class LigatureSubtable(int[] coverage, Ligature[][] sets) : LayoutSubtable
    {
        public override bool WouldApply(ReadOnlySpan<ushort> input, bool zeroContext)
        {
            var index = input.Length == 0 ? -1 : Index(coverage, input[0]);
            if (index < 0 || index >= sets.Length)
                return false;

            foreach (var ligature in sets[index])
            {
                if (ligature.Components.Length + 1 != input.Length)
                    continue;

                var matched = true;
                for (var i = 0; i < ligature.Components.Length && matched; i++)
                    matched = ligature.Components[i] == input[i + 1];

                if (matched)
                    return true;
            }

            return false;
        }

        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= sets.Length)
                return 0;

            foreach (var ligature in sets[index])
            {
                var next = position;
                var matched = true;
                Span<int> positions = stackalloc int[Math.Min(ligature.Components.Length, MaxSequenceLength) + 1];
                positions[0] = position;

                for (var c = 0; c < ligature.Components.Length; c++)
                {
                    // The components need not be neighbours: the lookup may be set to skip marks between them.
                    next = owner.Next(buffer, next, flags, markSet, true, GlyphMatch.Glyph(ligature.Components[c]));
                    if (next < 0)
                    {
                        matched = false;
                        break;
                    }

                    positions[c + 1] = next;
                }

                if (!matched)
                    continue;

                buffer.Merge(positions, ligature.Glyph);
                return 1;
            }

            return 0;
        }
    }
}
