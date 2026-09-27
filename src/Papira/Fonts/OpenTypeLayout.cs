using Papira.Text;
using static Papira.Fonts.TrueTypeFont;

namespace Papira.Fonts;

/// <summary>The classes GDEF gives a glyph; lookups use them to skip glyphs they do not apply to.</summary>
internal enum GlyphKind : byte { Unknown = 0, Base = 1, Ligature = 2, Mark = 3, Component = 4 }

/// <summary>What GDEF says about the glyphs: their class, and the mark groups that lookups filter by.</summary>
internal readonly record struct GlyphDefinitions(GlyphKind[] Kinds, ushort[] MarkAttachClasses, int[][] MarkSets);

/// <summary>One of the lookups a context rule runs, at the position it names.</summary>
internal readonly record struct LookupRecord(ushort Position, ushort Lookup);

/// <summary>
/// What a rule expects to find at a position: a particular glyph, a glyph of a class, or one the
/// coverage table lists. A glyph a rule may step over — a joiner — is only stepped over when it is
/// not what the rule was looking for.
/// </summary>
internal readonly struct GlyphMatch
{
    private readonly ushort[]? _classes;
    private readonly int[]? _coverage;
    private readonly ushort _expected;
    private readonly bool _any;

    private GlyphMatch(ushort[]? classes, int[]? coverage, ushort expected, bool any)
    {
        _classes = classes;
        _coverage = coverage;
        _expected = expected;
        _any = any;
    }

    /// <summary>Matches whatever comes next, which is what a pair or a mark lookup asks for.</summary>
    public static GlyphMatch Any => new(null, null, 0, true);

    public static GlyphMatch Glyph(ushort glyph) => new(null, null, glyph, false);

    public static GlyphMatch Class(ushort[]? classes, ushort expected) =>
        classes == null ? Glyph(expected) : new(classes, null, expected, false);

    public static GlyphMatch Covered(int[] coverage) => new(null, coverage, 0, false);

    public bool Matches(ushort glyph)
    {
        if (_any)
            return true;

        if (_coverage != null)
            return glyph < _coverage.Length && _coverage[glyph] >= 0;

        if (_classes != null)
            return (glyph < _classes.Length ? _classes[glyph] : (ushort)0) == _expected;

        return glyph == _expected;
    }
}

/// <summary>
/// What a subtable needs from the table it belongs to: which glyphs its lookup skips, and how to run
/// another lookup of the same table, as a context rule does.
/// </summary>
internal interface ILayoutEngine
{
    bool Skips(ushort flags, int markSet, ushort glyph);

    /// <summary>
    /// The next position a lookup looks at. <paramref name="input"/> tells whether this is part of what
    /// the rule replaces or only the context around it: the context is matched over every glyph, while
    /// the input only matches glyphs the feature applies to.
    /// </summary>
    int Next(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input = true);

    int Previous(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input = true);

    /// <summary>The next position that holds what the rule expects, or -1 when the rule does not match.</summary>
    int Next(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input, in GlyphMatch expected);

    int Previous(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input, in GlyphMatch expected);

    void ApplyNested(ShapingBuffer buffer, int position, int lookupIndex);
}

/// <summary>One subtable of a lookup. What it returns is how many positions the caller moves on by.</summary>
internal abstract class LayoutSubtable
{
    public abstract int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet);

    /// <summary>
    /// Whether the subtable would put something else in the place of exactly these glyphs. This asks
    /// what a font can do without changing anything: the shaper of the Indic scripts decides how to
    /// order a syllable by what the font offers. With <paramref name="zeroContext"/> a rule that needs
    /// glyphs around it does not count; without it, only what the rule itself matches has to fit.
    /// </summary>
    public virtual bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext) => false;

    protected static int Index(int[] coverage, ushort glyph) => glyph < coverage.Length ? coverage[glyph] : -1;
}

/// <summary>A lookup: the subtables that are tried in turn, and the glyphs they skip over.</summary>
internal sealed class LayoutLookup(ushort flags, int markSet, LayoutSubtable[] subtables)
{
    public ushort Flags => flags;

    public int MarkSet => markSet;

    public int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position)
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

    /// <summary>Whether any subtable would replace exactly these glyphs.</summary>
    public bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
    {
        foreach (var subtable in subtables)
        {
            if (subtable.WouldApply(glyphs, zeroContext))
                return true;
        }

        return false;
    }
}

/// <summary>
/// What the two layout tables of OpenType have in common: the scripts and features that lead to lookups,
/// what GDEF says about the glyphs, and the context rules that run other lookups. GSUB substitutes glyphs
/// on top of this, GPOS positions them.
/// </summary>
internal abstract class LayoutTable : ILayoutEngine
{
    protected const int MaxLookups = 4096;
    protected const int MaxSubtablesPerLookup = 256;
    protected const int MaxSequenceLength = 64;
    protected const int MaxApplications = 100_000;

    private readonly LayoutLookup?[] _lookups;
    private readonly Dictionary<long, int[]> _features;
    private readonly GlyphKind[] _kinds;
    private readonly ushort[] _markAttachClasses;
    private readonly int[][] _markSets;

    // The lookups a script needs, stage by stage, with the forms each one applies to. The features asked
    // for are part of the key: the same script is shaped differently when the ligatures are left out.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Script, uint[][] Stages), (int Lookup, byte Forms)[][]> _plans = new();

    protected LayoutTable(LayoutLookup?[] lookups, Dictionary<long, int[]> features, GlyphDefinitions definitions)
    {
        _lookups = lookups;
        _features = features;
        _kinds = definitions.Kinds;
        _markAttachClasses = definitions.MarkAttachClasses;
        _markSets = definitions.MarkSets;
    }

    public bool IsEmpty => _features.Count == 0;

    public bool Has(uint script, uint feature) => TryLookups(script, feature, out _);

    /// <summary>True when the font has rules of its own for the script, rather than only the default ones.</summary>
    public bool HasScript(uint script)
    {
        foreach (var key in _features.Keys)
        {
            if ((uint)(key >> 32) == script)
                return true;
        }

        return false;
    }

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

    /// <summary>
    /// Runs the lookups of each stage over the buffer. Within a stage the lookups run in the order the
    /// font lists them, not feature by feature: that is the order OpenType prescribes, and later lookups
    /// are written to work on what earlier ones produced.
    /// </summary>
    protected void RunStages(uint script, uint[][] stages, ShapingBuffer buffer)
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
        if (_plans.TryGetValue((script, stages), out var cached))
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

        _plans.TryAdd((script, stages), plan);
        return plan;
    }

    /// <summary>
    /// Applies the lookups of one feature to the glyphs that carry <paramref name="mask"/>, or to every
    /// glyph when it is zero. This is how the scripts that reorder their syllables drive the font: one
    /// feature at a time, over the glyphs it is meant for.
    /// </summary>
    public bool ApplyFeature(uint script, uint feature, ShapingBuffer buffer, uint mask, bool stepOverJoiners = true)
    {
        if (!TryLookups(script, feature, out var lookups))
            return false;

        var applied = false;
        var budget = MaxApplications;
        buffer.ActiveMask = mask;
        buffer.StepOverJoiners = stepOverJoiners;

        foreach (var index in lookups)
        {
            if ((uint)index >= (uint)_lookups.Length || _lookups[index] is not { } lookup)
                continue;

            for (var position = 0; position < buffer.Length && budget > 0;)
            {
                if (mask != 0 && (buffer[position].Features & mask) == 0)
                {
                    position++;
                    continue;
                }

                budget--;
                var consumed = lookup.Apply(this, buffer, position);
                applied |= consumed > 0;
                position += consumed > 0 ? consumed : 1;
            }
        }

        buffer.ActiveMask = 0;
        buffer.StepOverJoiners = true;
        return applied;
    }

    // What each stage of features comes to, per script; the features are the same array every time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Script, (uint Feature, uint Mask, bool StepOverJoiners)[] Features), (int Lookup, uint Mask, bool StepOverJoiners)[]> _stages = new();

    /// <summary>
    /// Applies several features at once, in the order the font lists their lookups rather than feature
    /// by feature. Fonts are written expecting that order — the lookups of different features are
    /// interleaved on purpose — so the scripts that ask for a group of features apply them this way.
    /// </summary>
    public void ApplyStage(uint script, (uint Feature, uint Mask, bool StepOverJoiners)[] features, ShapingBuffer buffer)
    {
        var budget = MaxApplications;
        foreach (var (index, mask, stepOver) in StagePlan(script, features))
        {
            if ((uint)index >= (uint)_lookups.Length || _lookups[index] is not { } lookup)
                continue;

            buffer.ActiveMask = mask;
            buffer.StepOverJoiners = stepOver;
            for (var position = 0; position < buffer.Length && budget > 0;)
            {
                if (mask != 0 && (buffer[position].Features & mask) == 0)
                {
                    position++;
                    continue;
                }

                budget--;
                var consumed = lookup.Apply(this, buffer, position);
                position += consumed > 0 ? consumed : 1;
            }
        }

        buffer.ActiveMask = 0;
        buffer.StepOverJoiners = true;
    }

    /// <summary>The lookups a group of features comes to, in the order the font lists them.</summary>
    private (int Lookup, uint Mask, bool StepOverJoiners)[] StagePlan(uint script, (uint Feature, uint Mask, bool StepOverJoiners)[] features)
    {
        if (_stages.TryGetValue((script, features), out var cached))
            return cached;

        var masks = new SortedDictionary<int, (uint Mask, bool StepOver)>();
        foreach (var (feature, mask, stepOver) in features)
        {
            if (!TryLookups(script, feature, out var lookups))
                continue;

            foreach (var index in lookups)
            {
                if (masks.TryGetValue(index, out var existing))
                {
                    // A lookup that any feature applies to every glyph is applied to every glyph, and
                    // one that any feature wants the joiners kept for keeps them.
                    masks[index] = (existing.Mask == 0 || mask == 0 ? 0 : existing.Mask | mask, existing.StepOver && stepOver);
                }
                else
                {
                    masks[index] = (mask, stepOver);
                }
            }
        }

        var plan = masks.Select(entry => (entry.Key, entry.Value.Mask, entry.Value.StepOver)).ToArray();
        _stages.TryAdd((script, features), plan);
        return plan;
    }

    /// <summary>
    /// Whether a feature would put something else in the place of the given glyphs. The shaper asks
    /// this to find out what a font can do — whether a letter has a below-base form, say — before it
    /// decides how to order the syllable.
    /// </summary>
    public bool WouldSubstitute(uint script, uint feature, ReadOnlySpan<ushort> glyphs, bool zeroContext)
    {
        if (glyphs.Length == 0 || !TryLookups(script, feature, out var lookups))
            return false;

        foreach (var index in lookups)
        {
            if ((uint)index >= (uint)_lookups.Length || _lookups[index] is not { } lookup)
                continue;

            if (lookup.WouldApply(glyphs, zeroContext))
                return true;
        }

        return false;
    }

    private bool TryLookups(uint script, uint feature, out int[] lookups) =>
        _features.TryGetValue(Key(script, feature), out lookups!) ||
        _features.TryGetValue(Key(Tag("DFLT"), feature), out lookups!);

    private static long Key(uint script, uint feature) => ((long)script << 32) | feature;

    internal GlyphKind KindOf(ushort glyph) => glyph < _kinds.Length ? _kinds[glyph] : GlyphKind.Unknown;

    /// <summary>
    /// True when a lookup skips this glyph: it can ignore bases, ligatures or marks altogether, or work
    /// on one group of marks only — either an attachment class or a set the font lists for the lookup.
    /// </summary>
    public bool Skips(ushort flags, int markSet, ushort glyph)
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

    /// <summary>
    /// The next position a lookup looks at, or -1 past the end of the buffer. A glyph the feature does
    /// not apply to ends the match rather than being stepped over.
    /// </summary>
    public int Next(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input = true) =>
        Next(buffer, position, flags, markSet, input, GlyphMatch.Any);

    public int Previous(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input = true) =>
        Previous(buffer, position, flags, markSet, input, GlyphMatch.Any);

    public int Next(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input, in GlyphMatch expected)
    {
        for (var i = position + 1; i < buffer.Length; i++)
        {
            if (Skips(flags, markSet, buffer[i].Glyph))
                continue;

            if (Accepts(buffer, i, input, expected))
                return i;

            // A joiner the rule was not looking for is stepped over; anything else ends the match.
            if (StepsOver(buffer, i, input))
                continue;

            return -1;
        }

        return -1;
    }

    public int Previous(ShapingBuffer buffer, int position, ushort flags, int markSet, bool input, in GlyphMatch expected)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (Skips(flags, markSet, buffer[i].Glyph))
                continue;

            if (Accepts(buffer, i, input, expected))
                return i;

            if (StepsOver(buffer, i, input))
                continue;

            return -1;
        }

        return -1;
    }

    /// <summary>Whether the glyph is what the rule expects, and one the feature applies to.</summary>
    private static bool Accepts(ShapingBuffer buffer, int index, bool input, in GlyphMatch expected)
    {
        if (input && buffer.ActiveMask != 0 && (buffer[index].Features & buffer.ActiveMask) == 0)
            return false;

        return expected.Matches(buffer[index].Glyph);
    }

    /// <summary>
    /// Whether a rule looks straight past a joiner. The context around a rule is always read without
    /// them; what the rule itself replaces reads them, unless the feature is one of those that leave
    /// the joiners to the shaper.
    /// </summary>
    private static bool StepsOver(ShapingBuffer buffer, int index, bool input) => buffer[index].JoinerKind switch
    {
        JoinerKind.Joiner => !input || buffer.StepOverJoiners,
        JoinerKind.NonJoiner => !input && buffer.StepOverJoiners,
        _ => false,
    };

    public void ApplyNested(ShapingBuffer buffer, int position, int lookupIndex)
    {
        if ((uint)lookupIndex < (uint)_lookups.Length && _lookups[lookupIndex] is { } lookup)
            lookup.Apply(this, buffer, position);
    }

    // ---- Reading -----------------------------------------------------------------------------------

    /// <summary>What GDEF says about the glyphs of a font; both layout tables are steered by it.</summary>
    internal static GlyphDefinitions ReadDefinitions(TrueTypeFont font)
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

    /// <summary>
    /// Reads the shared part of a layout table: which lookups every script and feature leads to, and the
    /// lookups themselves, whose subtables the caller reads because they differ between GSUB and GPOS.
    /// </summary>
    protected static bool TryReadTable(
        byte[] data,
        int table,
        int length,
        int glyphCount,
        int extensionType,
        Func<byte[], int, int, int, LayoutSubtable?> readSubtable,
        out LayoutLookup?[] lookups,
        out Dictionary<long, int[]> features)
    {
        lookups = [];
        features = [];

        var end = table + length;
        var scriptList = table + U16(data, table + 4);
        var featureList = table + U16(data, table + 6);
        var lookupList = table + U16(data, table + 8);

        var featureCount = U16(data, featureList);
        var lookupCount = Math.Min((int)U16(data, lookupList), MaxLookups);

        // Which lookups every (script, feature) pair uses. Only the default language system is read:
        // the language specific ones differ in details Papira does not expose.
        var scriptCount = U16(data, scriptList);
        for (var s = 0; s < scriptCount; s++)
        {
            var record = scriptList + 2 + s * 6;
            var script = U32(data, record);
            var scriptTable = scriptList + U16(data, record + 4);
            var defaultLangSys = U16(data, scriptTable);
            if (defaultLangSys == 0 || scriptTable + defaultLangSys >= end)
                continue;

            var langSys = scriptTable + defaultLangSys;
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
            return false;

        lookups = new LayoutLookup?[lookupCount];
        for (var i = 0; i < lookupCount; i++)
            lookups[i] = ReadLookup(data, lookupList + U16(data, lookupList + 2 + i * 2), table, end, glyphCount, extensionType, readSubtable);

        return true;
    }

    private static LayoutLookup? ReadLookup(
        byte[] data,
        int lookup,
        int table,
        int end,
        int glyphCount,
        int extensionType,
        Func<byte[], int, int, int, LayoutSubtable?> readSubtable)
    {
        var type = U16(data, lookup);
        var flags = U16(data, lookup + 2);
        var subtableCount = Math.Min((int)U16(data, lookup + 4), MaxSubtablesPerLookup);

        // A lookup that filters by mark set names it after its subtable offsets.
        var markSet = (flags & 0x0010) != 0 ? U16(data, lookup + 6 + U16(data, lookup + 4) * 2) : -1;

        var subtables = new List<LayoutSubtable>(subtableCount);
        for (var s = 0; s < subtableCount; s++)
        {
            var subtable = lookup + U16(data, lookup + 6 + s * 2);
            var subtableType = type;

            // An extension subtable only points at a subtable of another type, so that it can live
            // beyond the 64 KB an offset reaches. GSUB numbers it 7, GPOS numbers it 9.
            if (type == extensionType)
            {
                subtableType = U16(data, subtable + 2);
                subtable += (int)U32(data, subtable + 4);
            }

            if (subtable < table || subtable >= end)
                continue;

            var parsed = readSubtable(data, subtable, subtableType, glyphCount);
            if (parsed != null)
                subtables.Add(parsed);
        }

        return subtables.Count > 0 ? new LayoutLookup(flags, markSet, [.. subtables]) : null;
    }

    internal static int[] ReadCoverage(byte[] data, int coverage, int glyphCount)
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

    internal static ushort[] ReadClassDef(byte[] data, int classDef, int glyphCount)
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

    // ---- Context rules -----------------------------------------------------------------------------

    private readonly record struct ContextRule(ushort[] Backtrack, ushort[] Input, ushort[] Lookahead, LookupRecord[] Records);

    /// <summary>
    /// Reads a context or chaining context subtable, which both tables share: the rules say which glyphs
    /// have to stand around a position, and which lookups run there when they do.
    /// </summary>
    internal static LayoutSubtable? ReadContext(byte[] data, int subtable, int format, int glyphCount, bool chaining)
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
                        rules.Add(chaining ? ReadChainRule(data, rule) : ReadRule(data, rule));
                    }

                    sets[i] = [.. rules];
                }

                return new ContextSubtable(coverage, sets, null, null, null);
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
                        rules.Add(chaining ? ReadChainRule(data, rule) : ReadRule(data, rule));
                    }

                    sets[i] = [.. rules];
                }

                return new ContextSubtable(
                    coverage,
                    sets,
                    inputClassDef == 0 ? null : ReadClassDef(data, subtable + inputClassDef, glyphCount),
                    backtrackClassDef == 0 ? null : ReadClassDef(data, subtable + backtrackClassDef, glyphCount),
                    lookaheadClassDef == 0 ? null : ReadClassDef(data, subtable + lookaheadClassDef, glyphCount));
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

    private static ContextRule ReadRule(byte[] data, int rule)
    {
        var glyphCount = Math.Min((int)U16(data, rule), MaxSequenceLength);
        var input = new ushort[Math.Max(glyphCount - 1, 0)];
        for (var i = 0; i < input.Length; i++)
            input[i] = U16(data, rule + 4 + i * 2);

        return new ContextRule([], input, [], ReadLookupRecords(data, rule + 4 + input.Length * 2, U16(data, rule + 2)));
    }

    private static ContextRule ReadChainRule(byte[] data, int rule)
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

    /// <summary>Formats 1 and 2 of a context rule: the context is listed by glyph or by class.</summary>
    private sealed class ContextSubtable(
        int[] coverage,
        ContextRule[][] sets,
        ushort[]? inputClasses,
        ushort[]? backtrackClasses,
        ushort[]? lookaheadClasses) : LayoutSubtable
    {
        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
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

        public override bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
        {
            if (glyphs.Length == 0)
                return false;

            var glyph = glyphs[0];
            int index;
            if (inputClasses != null)
            {
                if (Index(coverage, glyph) < 0)
                    return false;

                index = glyph < inputClasses.Length ? inputClasses[glyph] : 0;
            }
            else
            {
                index = Index(coverage, glyph);
            }

            if (index < 0 || index >= sets.Length)
                return false;

            foreach (var rule in sets[index])
            {
                if (zeroContext && (rule.Backtrack.Length > 0 || rule.Lookahead.Length > 0))
                    continue;

                if (rule.Input.Length + 1 != glyphs.Length)
                    continue;

                var matched = true;
                for (var i = 0; i < rule.Input.Length && matched; i++)
                    matched = Same(glyphs[i + 1], rule.Input[i], inputClasses);

                if (matched)
                    return true;
            }

            return false;
        }

        private bool Matches(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet, ContextRule rule, out List<int> positions)
        {
            positions = [position];

            var previous = position;
            foreach (var expected in rule.Backtrack)
            {
                previous = owner.Previous(buffer, previous, flags, markSet, false, GlyphMatch.Class(backtrackClasses, expected));
                if (previous < 0)
                    return false;
            }

            var next = position;
            foreach (var expected in rule.Input)
            {
                next = owner.Next(buffer, next, flags, markSet, true, GlyphMatch.Class(inputClasses, expected));
                if (next < 0)
                    return false;

                positions.Add(next);
            }

            foreach (var expected in rule.Lookahead)
            {
                next = owner.Next(buffer, next, flags, markSet, false, GlyphMatch.Class(lookaheadClasses, expected));
                if (next < 0)
                    return false;
            }

            return true;
        }

        private static bool Same(ushort glyph, ushort expected, ushort[]? classes) =>
            classes == null ? glyph == expected : (glyph < classes.Length ? classes[glyph] : 0) == expected;

        internal static void Run(ILayoutEngine owner, ShapingBuffer buffer, List<int> positions, LookupRecord[] records)
        {
            foreach (var record in records)
            {
                if (record.Position < positions.Count)
                    owner.ApplyNested(buffer, positions[record.Position], record.Lookup);
            }
        }
    }

    /// <summary>Format 3 of a context rule: the context is given as coverage tables.</summary>
    private sealed class CoverageContextSubtable(int[][] backtrack, int[][] input, int[][] lookahead, LookupRecord[] records) : LayoutSubtable
    {
        public override bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
        {
            if (zeroContext && (backtrack.Length > 0 || lookahead.Length > 0))
                return false;

            if (input.Length != glyphs.Length)
                return false;

            for (var i = 0; i < input.Length; i++)
            {
                if (Index(input[i], glyphs[i]) < 0)
                    return false;
            }

            return true;
        }

        public override int Apply(ILayoutEngine owner, ShapingBuffer buffer, int position, ushort flags, int markSet)
        {
            if (input.Length == 0 || Index(input[0], buffer[position].Glyph) < 0)
                return 0;

            var positions = new List<int>(input.Length) { position };

            var previous = position;
            foreach (var coverage in backtrack)
            {
                previous = owner.Previous(buffer, previous, flags, markSet, false, GlyphMatch.Covered(coverage));
                if (previous < 0)
                    return 0;
            }

            var next = position;
            for (var i = 1; i < input.Length; i++)
            {
                next = owner.Next(buffer, next, flags, markSet, true, GlyphMatch.Covered(input[i]));
                if (next < 0)
                    return 0;

                positions.Add(next);
            }

            foreach (var coverage in lookahead)
            {
                next = owner.Next(buffer, next, flags, markSet, false, GlyphMatch.Covered(coverage));
                if (next < 0)
                    return 0;
            }

            ContextSubtable.Run(owner, buffer, positions, records);
            return positions.Count;
        }
    }
}
