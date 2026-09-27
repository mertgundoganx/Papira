using Papira.Text;
using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>The classes GDEF gives a glyph; lookups use them to skip glyphs they do not apply to.</summary>
internal enum GlyphKind : byte { Unknown = 0, Base = 1, Ligature = 2, Mark = 3, Component = 4 }

/// <summary>
/// The glyph substitution table (GSUB) of a font: the rules that turn a sequence of characters into the
/// glyphs that are actually drawn. Papira uses it for cursive scripts, where every letter has an initial,
/// medial, final and isolated form, and for the ligatures a script requires, such as lam-alef in Arabic.
/// </summary>
/// <remarks>
/// Lookup types 1 (single), 2 (multiple), 3 (alternate), 4 (ligature), 5 (context), 6 (chaining context)
/// and 7 (extension) are applied. Reverse chaining substitution, which only Nastaliq style fonts need,
/// is not. A malformed table is ignored rather than fatal: the text is then drawn unshaped.
/// </remarks>
internal sealed class GlyphSubstitution
{
    private const int MaxLookups = 4096;
    private const int MaxSubtablesPerLookup = 256;
    private const int MaxLigatures = 200_000;
    private const int MaxSequenceLength = 64;
    private const int MaxApplications = 100_000;

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

    /// <summary>Scripts that do not join still need their compositions and required ligatures.</summary>
    public static readonly uint[][] SimpleStages =
    [
        [Tag("rvrn")],
        [Tag("ccmp"), Tag("locl")],
        [Tag("rlig"), Tag("calt"), Tag("clig"), Tag("liga"), Tag("rclt")],
    ];

    public static readonly GlyphSubstitution None = new([], new Dictionary<long, int[]>(), new GlyphDefinitions([], [], []));

    private readonly Lookup?[] _lookups;
    private readonly Dictionary<long, int[]> _features;
    private readonly GlyphKind[] _kinds;
    private readonly ushort[] _markAttachClasses;
    private readonly int[][] _markSets;

    // The lookups a script needs, stage by stage, with the forms each one applies to.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (int Lookup, byte Forms)[][]> _plans = new();

    private GlyphSubstitution(Lookup?[] lookups, Dictionary<long, int[]> features, GlyphDefinitions definitions)
    {
        _lookups = lookups;
        _features = features;
        _kinds = definitions.Kinds;
        _markAttachClasses = definitions.MarkAttachClasses;
        _markSets = definitions.MarkSets;
    }

    /// <summary>What GDEF says about the glyphs: their class, and the mark groups that lookups filter by.</summary>
    private readonly record struct GlyphDefinitions(GlyphKind[] Kinds, ushort[] MarkAttachClasses, int[][] MarkSets);

    public bool IsEmpty => _features.Count == 0;

    /// <summary>The form features are applied only where the letter takes that form.</summary>
    public static JoiningForm? FormOf(uint feature)
    {
        if (feature == Tag("isol"))
            return JoiningForm.Isolated;

        if (feature == Tag("init"))
            return JoiningForm.Initial;

        if (feature == Tag("medi"))
            return JoiningForm.Medial;

        if (feature == Tag("fina"))
            return JoiningForm.Final;

        return null;
    }

    public static GlyphSubstitution Load(TrueTypeFont font)
    {
        try
        {
            if (!font.TryTable("GSUB", out var gsub, out var length))
                return None;

            return Read(font.Data, gsub, length, font.GlyphCount, ReadDefinitions(font));
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            // A font with a broken substitution table is still usable, only unshaped.
            return None;
        }
    }

    /// <summary>
    /// Applies the substitutions of a script to the buffer. The lookups run in the order the font lists
    /// them, not feature by feature: that is the order OpenType prescribes, and later lookups are written
    /// to work on what earlier ones produced.
    /// </summary>
    public void Apply(uint script, uint[][] stages, ShapingBuffer buffer)
    {
        var budget = MaxApplications;

        foreach (var stage in Plan(script, stages))
        {
            foreach (var (index, forms) in stage)
            {
                if (_lookups[index] is not { } lookup)
                    continue;

                for (var position = 0; position < buffer.Length && budget > 0;)
                {
                    // A positional feature only applies where the letter takes that form.
                    if ((forms & (1 << (int)buffer[position].Form)) == 0)
                    {
                        position++;
                        continue;
                    }

                    budget--;
                    var consumed = lookup.Apply(this, buffer, position);
                    position += consumed > 0 ? consumed : 1;
                }
            }
        }
    }

    /// <summary>
    /// The lookups of each stage, sorted by their index in the font, which is the order OpenType applies
    /// the lookups of one stage in.
    /// </summary>
    private (int Lookup, byte Forms)[][] Plan(uint script, uint[][] stages)
    {
        if (_plans.TryGetValue(script, out var cached))
            return cached;

        const byte anyForm = 0x0F;
        var plan = new (int Lookup, byte Forms)[stages.Length][];
        for (var stage = 0; stage < stages.Length; stage++)
        {
            var masks = new SortedDictionary<int, byte>();
            foreach (var feature in stages[stage])
            {
                if (!TryLookups(script, feature, out var lookups))
                    continue;

                var form = FormOf(feature);
                var mask = form == null ? anyForm : (byte)(1 << (int)form);
                foreach (var index in lookups)
                    masks[index] = (byte)(masks.GetValueOrDefault(index) | mask);
            }

            plan[stage] = masks.Select(entry => (entry.Key, entry.Value)).ToArray();
        }

        _plans.TryAdd(script, plan);
        return plan;
    }

    public bool Has(uint script, uint feature) => TryLookups(script, feature, out _);

    private bool TryLookups(uint script, uint feature, out int[] lookups) =>
        _features.TryGetValue(Key(script, feature), out lookups!) ||
        _features.TryGetValue(Key(Tag("DFLT"), feature), out lookups!);

    private static long Key(uint script, uint feature) => ((long)script << 32) | feature;

    internal GlyphKind KindOf(ushort glyph) => glyph < _kinds.Length ? _kinds[glyph] : GlyphKind.Unknown;

    /// <summary>
    /// True when a lookup skips this glyph: it can ignore bases, ligatures or marks altogether, or work
    /// on one group of marks only — either an attachment class or a set the font lists for the lookup.
    /// </summary>
    internal bool Skips(ushort flags, int markSet, ushort glyph)
    {
        switch (KindOf(glyph))
        {
            case GlyphKind.Base when (flags & 0x0002) != 0:
            case GlyphKind.Ligature when (flags & 0x0004) != 0:
            case GlyphKind.Mark when (flags & 0x0008) != 0:
                return true;

            case GlyphKind.Mark:
                var attachment = flags >> 8;
                if (attachment != 0 && (glyph >= _markAttachClasses.Length || _markAttachClasses[glyph] != attachment))
                    return true;

                if (markSet >= 0 && markSet < _markSets.Length)
                    return glyph >= _markSets[markSet].Length || _markSets[markSet][glyph] < 0;

                return false;

            default:
                return false;
        }
    }

    /// <summary>The next position a lookup looks at, or -1 past the end of the buffer.</summary>
    internal int Next(ShapingBuffer buffer, int position, ushort flags, int markSet)
    {
        for (var i = position + 1; i < buffer.Length; i++)
        {
            if (!Skips(flags, markSet, buffer[i].Glyph))
                return i;
        }

        return -1;
    }

    internal int Previous(ShapingBuffer buffer, int position, ushort flags, int markSet)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (!Skips(flags, markSet, buffer[i].Glyph))
                return i;
        }

        return -1;
    }

    internal void ApplyNested(ShapingBuffer buffer, int position, int lookupIndex)
    {
        if ((uint)lookupIndex < (uint)_lookups.Length && _lookups[lookupIndex] is { } lookup)
            lookup.Apply(this, buffer, position);
    }

    // ---- Reading -----------------------------------------------------------------------------------

    private static GlyphDefinitions ReadDefinitions(TrueTypeFont font)
    {
        if (!font.TryTable("GDEF", out var gdef, out _))
            return new GlyphDefinitions([], [], []);

        var data = font.Data;
        var kinds = Array.Empty<GlyphKind>();
        if (U16(data, gdef + 4) is var classDef and > 0)
        {
            var classes = ReadClassDef(data, gdef + classDef, font.GlyphCount);
            kinds = new GlyphKind[classes.Length];
            for (var i = 0; i < classes.Length; i++)
                kinds[i] = classes[i] <= 4 ? (GlyphKind)classes[i] : GlyphKind.Unknown;
        }

        var attachClasses = U16(data, gdef + 10) is var markAttach and > 0
            ? ReadClassDef(data, gdef + markAttach, font.GlyphCount)
            : [];

        // Mark glyph sets arrived with version 1.2 of the table.
        var sets = Array.Empty<int[]>();
        if (U16(data, gdef + 2) >= 2 && U16(data, gdef + 12) is var markSets and > 0)
        {
            var table = gdef + markSets;
            var count = Math.Min((int)U16(data, table + 2), 256);
            sets = new int[count][];
            for (var i = 0; i < count; i++)
                sets[i] = ReadCoverage(data, table + (int)U32(data, table + 4 + i * 4), font.GlyphCount);
        }

        return new GlyphDefinitions(kinds, attachClasses, sets);
    }

    private static GlyphSubstitution Read(byte[] data, int gsub, int length, int glyphCount, GlyphDefinitions definitions)
    {
        var end = gsub + length;
        var scriptList = gsub + U16(data, gsub + 4);
        var featureList = gsub + U16(data, gsub + 6);
        var lookupList = gsub + U16(data, gsub + 8);

        var featureCount = U16(data, featureList);
        var lookupCount = Math.Min((int)U16(data, lookupList), MaxLookups);

        // Which lookups every (script, feature) pair uses. Only the default language system is read:
        // the language specific ones differ in details Papira does not expose.
        var features = new Dictionary<long, int[]>();
        var scriptCount = U16(data, scriptList);
        for (var s = 0; s < scriptCount; s++)
        {
            var record = scriptList + 2 + s * 6;
            var script = U32(data, record);
            var table = scriptList + U16(data, record + 4);
            var defaultLangSys = U16(data, table);
            if (defaultLangSys == 0 || table + defaultLangSys >= end)
                continue;

            var langSys = table + defaultLangSys;
            var indexCount = U16(data, langSys + 4);
            for (var f = 0; f < indexCount; f++)
            {
                var featureIndex = U16(data, langSys + 6 + f * 2);
                if (featureIndex >= featureCount)
                    continue;

                var featureRecord = featureList + 2 + featureIndex * 6;
                var feature = featureList + U16(data, featureRecord + 4);
                var lookupIndexCount = U16(data, feature + 2);

                var indices = new List<int>(lookupIndexCount);
                for (var i = 0; i < lookupIndexCount; i++)
                {
                    var index = U16(data, feature + 4 + i * 2);
                    if (index < lookupCount)
                        indices.Add(index);
                }

                if (indices.Count == 0)
                    continue;

                var key = ((long)script << 32) | U32(data, featureRecord);
                if (features.TryGetValue(key, out var existing))
                    indices.AddRange(existing);

                indices.Sort();
                features[key] = [.. indices.Distinct()];
            }
        }

        if (features.Count == 0)
            return None;

        var lookups = new Lookup?[lookupCount];
        for (var i = 0; i < lookupCount; i++)
            lookups[i] = ReadLookup(data, lookupList + U16(data, lookupList + 2 + i * 2), gsub, end, glyphCount);

        return new GlyphSubstitution(lookups, features, definitions);
    }

    private static Lookup? ReadLookup(byte[] data, int lookup, int gsub, int end, int glyphCount)
    {
        var type = U16(data, lookup);
        var flags = U16(data, lookup + 2);
        var subtableCount = Math.Min((int)U16(data, lookup + 4), MaxSubtablesPerLookup);

        // A lookup that filters by mark set names it after its subtable offsets.
        var markSet = (flags & 0x0010) != 0 ? U16(data, lookup + 6 + U16(data, lookup + 4) * 2) : -1;

        var subtables = new List<Subtable>(subtableCount);
        for (var s = 0; s < subtableCount; s++)
        {
            var subtable = lookup + U16(data, lookup + 6 + s * 2);
            var subtableType = type;

            // Type 7 only points at a subtable of another type, so that it can live beyond 64 KB.
            if (type == 7)
            {
                subtableType = U16(data, subtable + 2);
                subtable += (int)U32(data, subtable + 4);
            }

            if (subtable < gsub || subtable >= end)
                continue;

            var parsed = ReadSubtable(data, subtable, subtableType, glyphCount);
            if (parsed != null)
                subtables.Add(parsed);
        }

        return subtables.Count > 0 ? new Lookup(flags, markSet, [.. subtables]) : null;
    }

    private static Subtable? ReadSubtable(byte[] data, int subtable, int type, int glyphCount)
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

    private static Subtable? ReadContext(byte[] data, int subtable, int format, int glyphCount, bool chaining)
    {
        switch (format)
        {
            case 1:
            {
                var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
                var setCount = U16(data, subtable + 4);
                var sets = new ContextRule[setCount][];
                for (var i = 0; i < setCount; i++)
                {
                    var offset = U16(data, subtable + 6 + i * 2);
                    if (offset == 0)
                    {
                        sets[i] = [];
                        continue;
                    }

                    var set = subtable + offset;
                    var count = U16(data, set);
                    var rules = new List<ContextRule>(count);
                    for (var r = 0; r < count; r++)
                    {
                        var rule = set + U16(data, set + 2 + r * 2);
                        rules.Add(chaining ? ReadChainRule(data, rule, false) : ReadRule(data, rule, false));
                    }

                    sets[i] = [.. rules];
                }

                return new ContextSubtable(coverage, sets, null, null, null, null);
            }

            case 2:
            {
                var coverage = ReadCoverage(data, subtable + U16(data, subtable + 2), glyphCount);
                int backtrackClassDef = 0, inputClassDef, lookaheadClassDef = 0, setsOffset;
                if (chaining)
                {
                    backtrackClassDef = U16(data, subtable + 4);
                    inputClassDef = U16(data, subtable + 6);
                    lookaheadClassDef = U16(data, subtable + 8);
                    setsOffset = subtable + 10;
                }
                else
                {
                    inputClassDef = U16(data, subtable + 4);
                    setsOffset = subtable + 6;
                }

                var setCount = U16(data, setsOffset);
                var sets = new ContextRule[setCount][];
                for (var i = 0; i < setCount; i++)
                {
                    var offset = U16(data, setsOffset + 2 + i * 2);
                    if (offset == 0)
                    {
                        sets[i] = [];
                        continue;
                    }

                    var set = subtable + offset;
                    var count = U16(data, set);
                    var rules = new List<ContextRule>(count);
                    for (var r = 0; r < count; r++)
                    {
                        var rule = set + U16(data, set + 2 + r * 2);
                        rules.Add(chaining ? ReadChainRule(data, rule, true) : ReadRule(data, rule, true));
                    }

                    sets[i] = [.. rules];
                }

                return new ContextSubtable(
                    coverage,
                    sets,
                    inputClassDef == 0 ? null : ReadClassDef(data, subtable + inputClassDef, glyphCount),
                    backtrackClassDef == 0 ? null : ReadClassDef(data, subtable + backtrackClassDef, glyphCount),
                    lookaheadClassDef == 0 ? null : ReadClassDef(data, subtable + lookaheadClassDef, glyphCount),
                    null);
            }

            case 3 when chaining:
            {
                // Backtrack, input and lookahead, each a count followed by that many coverage tables.
                var backtrack = ReadCoverageList(data, subtable, subtable + 2, glyphCount, out var position);
                var input = ReadCoverageList(data, subtable, position, glyphCount, out position);
                var lookahead = ReadCoverageList(data, subtable, position, glyphCount, out position);
                return new CoverageContextSubtable(backtrack, input, lookahead, ReadLookupRecords(data, position));
            }

            case 3:
            {
                // Without chaining both counts come first, and the coverage tables follow them.
                var glyphs = Math.Min((int)U16(data, subtable + 2), MaxSequenceLength);
                var records = Math.Min((int)U16(data, subtable + 4), MaxSequenceLength);
                var input = new int[glyphs][];
                for (var i = 0; i < glyphs; i++)
                    input[i] = ReadCoverage(data, subtable + U16(data, subtable + 6 + i * 2), glyphCount);

                return new CoverageContextSubtable([], input, [], ReadLookupRecords(data, subtable + 6 + glyphs * 2, records));
            }

            default:
                return null;
        }
    }

    private static int[][] ReadCoverageList(byte[] data, int subtable, int offset, int glyphCount, out int next)
    {
        var count = Math.Min((int)U16(data, offset), MaxSequenceLength);
        var coverages = new int[count][];
        for (var i = 0; i < count; i++)
            coverages[i] = ReadCoverage(data, subtable + U16(data, offset + 2 + i * 2), glyphCount);

        next = offset + 2 + count * 2;
        return coverages;
    }

    private static ContextRule ReadRule(byte[] data, int rule, bool byClass)
    {
        var glyphCount = Math.Min((int)U16(data, rule), MaxSequenceLength);
        var input = new ushort[Math.Max(glyphCount - 1, 0)];
        for (var i = 0; i < input.Length; i++)
            input[i] = U16(data, rule + 4 + i * 2);

        _ = byClass;
        return new ContextRule([], input, [], ReadLookupRecords(data, rule + 4 + input.Length * 2, U16(data, rule + 2)));
    }

    private static ContextRule ReadChainRule(byte[] data, int rule, bool byClass)
    {
        var offset = rule;
        var backtrackCount = Math.Min((int)U16(data, offset), MaxSequenceLength);
        var backtrack = new ushort[backtrackCount];
        for (var i = 0; i < backtrackCount; i++)
            backtrack[i] = U16(data, offset + 2 + i * 2);

        offset += 2 + backtrackCount * 2;
        var inputCount = Math.Min((int)U16(data, offset), MaxSequenceLength);
        var input = new ushort[Math.Max(inputCount - 1, 0)];
        for (var i = 0; i < input.Length; i++)
            input[i] = U16(data, offset + 2 + i * 2);

        offset += 2 + input.Length * 2;
        var lookaheadCount = Math.Min((int)U16(data, offset), MaxSequenceLength);
        var lookahead = new ushort[lookaheadCount];
        for (var i = 0; i < lookaheadCount; i++)
            lookahead[i] = U16(data, offset + 2 + i * 2);

        offset += 2 + lookaheadCount * 2;
        _ = byClass;
        return new ContextRule(backtrack, input, lookahead, ReadLookupRecords(data, offset));
    }

    /// <summary>Reads a count followed by that many lookup records.</summary>
    private static LookupRecord[] ReadLookupRecords(byte[] data, int offset) =>
        ReadLookupRecords(data, offset + 2, Math.Min((int)U16(data, offset), MaxSequenceLength));

    private static LookupRecord[] ReadLookupRecords(byte[] data, int offset, int count)
    {
        count = Math.Min(count, MaxSequenceLength);
        var records = new LookupRecord[count];
        for (var i = 0; i < count; i++)
            records[i] = new LookupRecord(U16(data, offset + i * 4), U16(data, offset + 2 + i * 4));

        return records;
    }

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

    // ---- Lookups -----------------------------------------------------------------------------------

    private readonly record struct Ligature(ushort Glyph, ushort[] Components);

    private readonly record struct LookupRecord(ushort Position, ushort Lookup);

    private readonly record struct ContextRule(ushort[] Backtrack, ushort[] Input, ushort[] Lookahead, LookupRecord[] Records);

    private sealed class Lookup(ushort flags, int markSet, Subtable[] subtables)
    {
        public int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position)
        {
            if (owner.Skips(flags, markSet, buffer[position].Glyph))
                return 0;

            foreach (var subtable in subtables)
            {
                var consumed = subtable.Apply(owner, buffer, position, flags, markSet);
                if (consumed > 0)
                    return consumed;
            }

            return 0;
        }
    }

    private abstract class Subtable
    {
        public abstract int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet);

        protected static int Index(int[] coverage, ushort glyph) => glyph < coverage.Length ? coverage[glyph] : -1;
    }

    /// <summary>Type 1: one glyph for another.</summary>
    private sealed class SingleSubtable(int[] coverage, ushort[]? glyphs, short delta) : Subtable
    {
        public override int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
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
    private sealed class MultipleSubtable(int[] coverage, ushort[][] sequences) : Subtable
    {
        public override int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= sequences.Length || sequences[index].Length == 0)
                return 0;

            buffer.Expand(position, sequences[index]);
            return sequences[index].Length;
        }
    }

    /// <summary>Type 3: a choice of glyphs, of which the first is taken.</summary>
    private sealed class AlternateSubtable(int[] coverage, ushort[] alternates) : Subtable
    {
        public override int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            var index = Index(coverage, buffer[position].Glyph);
            if (index < 0 || index >= alternates.Length || alternates[index] == 0)
                return 0;

            buffer.Replace(position, alternates[index]);
            return 1;
        }
    }

    /// <summary>Type 4: several glyphs become one.</summary>
    private sealed class LigatureSubtable(int[] coverage, Ligature[][] sets) : Subtable
    {
        public override int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
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
                    next = owner.Next(buffer, next, flags, markSet);
                    if (next < 0 || buffer[next].Glyph != ligature.Components[c])
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

    /// <summary>Types 5 and 6, formats 1 and 2: rules listed by glyph or by class.</summary>
    private sealed class ContextSubtable(
        int[] coverage,
        ContextRule[][] sets,
        ushort[]? inputClasses,
        ushort[]? backtrackClasses,
        ushort[]? lookaheadClasses,
        ushort[]? unused) : Subtable
    {
        public override int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            _ = unused;
            var glyph = buffer[position].Glyph;
            int index;
            if (inputClasses != null)
            {
                if (Index(coverage, glyph) < 0)
                    return 0;

                index = glyph < inputClasses.Length ? inputClasses[glyph] : 0;
            }
            else
            {
                index = Index(coverage, glyph);
            }

            if (index < 0 || index >= sets.Length)
                return 0;

            foreach (var rule in sets[index])
            {
                if (Matches(owner, buffer, position, flags, markSet, rule, out var positions))
                {
                    Run(owner, buffer, positions, rule.Records);
                    return positions.Count;
                }
            }

            return 0;
        }

        private bool Matches(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet, ContextRule rule, out List<int> positions)
        {
            positions = [position];

            var previous = position;
            foreach (var expected in rule.Backtrack)
            {
                previous = owner.Previous(buffer, previous, flags, markSet);
                if (previous < 0 || !Same(buffer[previous].Glyph, expected, backtrackClasses))
                    return false;
            }

            var next = position;
            foreach (var expected in rule.Input)
            {
                next = owner.Next(buffer, next, flags, markSet);
                if (next < 0 || !Same(buffer[next].Glyph, expected, inputClasses))
                    return false;

                positions.Add(next);
            }

            foreach (var expected in rule.Lookahead)
            {
                next = owner.Next(buffer, next, flags, markSet);
                if (next < 0 || !Same(buffer[next].Glyph, expected, lookaheadClasses))
                    return false;
            }

            return true;
        }

        private static bool Same(ushort glyph, ushort expected, ushort[]? classes) =>
            classes == null ? glyph == expected : (glyph < classes.Length ? classes[glyph] : 0) == expected;

        internal static void Run(GlyphSubstitution owner, ShapingBuffer buffer, List<int> positions, LookupRecord[] records)
        {
            foreach (var record in records)
            {
                if (record.Position < positions.Count)
                    owner.ApplyNested(buffer, positions[record.Position], record.Lookup);
            }
        }
    }

    /// <summary>Types 5 and 6, format 3: the context is given as coverage tables.</summary>
    private sealed class CoverageContextSubtable(int[][] backtrack, int[][] input, int[][] lookahead, LookupRecord[] records) : Subtable
    {
        public override int Apply(GlyphSubstitution owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            if (input.Length == 0 || Index(input[0], buffer[position].Glyph) < 0)
                return 0;

            var positions = new List<int>(input.Length) { position };

            var previous = position;
            foreach (var coverage in backtrack)
            {
                previous = owner.Previous(buffer, previous, flags, markSet);
                if (previous < 0 || Index(coverage, buffer[previous].Glyph) < 0)
                    return 0;
            }

            var next = position;
            for (var i = 1; i < input.Length; i++)
            {
                next = owner.Next(buffer, next, flags, markSet);
                if (next < 0 || Index(input[i], buffer[next].Glyph) < 0)
                    return 0;

                positions.Add(next);
            }

            foreach (var coverage in lookahead)
            {
                next = owner.Next(buffer, next, flags, markSet);
                if (next < 0 || Index(coverage, buffer[next].Glyph) < 0)
                    return 0;
            }

            ContextSubtable.Run(owner, buffer, positions, records);
            return positions.Count;
        }
    }
}
