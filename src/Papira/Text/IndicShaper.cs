using Papira.Fonts;
using static Papira.Fonts.TrueTypeFont;

namespace Papira.Text;

/// <summary>
/// Shapes the scripts of India: Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu,
/// Kannada and Malayalam. These scripts are written in syllables, and what is written is not the
/// order things are drawn in: a vowel sign written after a consonant may be drawn before it, an
/// initial Ra climbs on top of the syllable as a reph, and consonants that meet form conjuncts.
/// </summary>
/// <remarks>
/// The rules are those of the OpenType Indic script development specification: the text is split
/// into syllables, the base consonant of each one is found, everything else is placed relative to
/// it, and the font is then asked — one feature at a time — to draw the forms that order implies.
/// The character properties come from the Unicode Character Database.
/// </remarks>
internal static class IndicShaper
{
    // The features that build the forms of a syllable, applied one at a time and in this order.
    private static readonly uint Nukt = Tag("nukt");
    private static readonly uint Akhn = Tag("akhn");
    private static readonly uint Rphf = Tag("rphf");
    private static readonly uint Rkrf = Tag("rkrf");
    private static readonly uint Pref = Tag("pref");
    private static readonly uint Blwf = Tag("blwf");
    private static readonly uint Abvf = Tag("abvf");
    private static readonly uint Half = Tag("half");
    private static readonly uint Pstf = Tag("pstf");
    private static readonly uint Vatu = Tag("vatu");
    private static readonly uint Cjct = Tag("cjct");

    // The features that are applied to the whole syllable once it is in order.
    private static readonly uint Init = Tag("init");
    private static readonly uint Pres = Tag("pres");
    private static readonly uint Abvs = Tag("abvs");
    private static readonly uint Blws = Tag("blws");
    private static readonly uint Psts = Tag("psts");
    private static readonly uint Haln = Tag("haln");
    private static readonly uint Locl = Tag("locl");
    private static readonly uint Ccmp = Tag("ccmp");

    // The features every script gets, whatever its own rules are.
    private static readonly uint Rvrn = Tag("rvrn");
    private static readonly uint Rlig = Tag("rlig");
    private static readonly uint Calt = Tag("calt");
    private static readonly uint Clig = Tag("clig");
    private static readonly uint Rclt = Tag("rclt");

    // The features a glyph carries when it is to take part in them.
    private const uint MaskRphf = 1 << 0;
    private const uint MaskPref = 1 << 1;
    private const uint MaskBlwf = 1 << 2;
    private const uint MaskAbvf = 1 << 3;
    private const uint MaskHalf = 1 << 4;
    private const uint MaskPstf = 1 << 5;
    private const uint MaskInit = 1 << 6;

    /// <summary>Where the reph of a script is drawn.</summary>
    private enum RephPosition : byte { AfterMain = 5, BeforeSub = 7, AfterSub = 9, BeforePost = 10, AfterPost = 12 }

    /// <summary>How the reph is written: as Ra and a virama, as that with a joiner, or as a character.</summary>
    private enum RephMode : byte { Implicit, Explicit, LogicalRepha }

    /// <summary>Whether the below-base form is asked for before the base as well as after it.</summary>
    private enum BelowMode : byte { PreAndPost, PostOnly }

    private readonly record struct Config(int Virama, RephPosition RephPos, RephMode Reph, BelowMode Below);

    private static readonly Dictionary<uint, Config> Configs = new()
    {
        [Tag("deva")] = new Config(0x094D, RephPosition.BeforePost, RephMode.Implicit, BelowMode.PreAndPost),
        [Tag("beng")] = new Config(0x09CD, RephPosition.AfterSub, RephMode.Implicit, BelowMode.PreAndPost),
        [Tag("guru")] = new Config(0x0A4D, RephPosition.BeforeSub, RephMode.Implicit, BelowMode.PreAndPost),
        [Tag("gujr")] = new Config(0x0ACD, RephPosition.BeforePost, RephMode.Implicit, BelowMode.PreAndPost),
        [Tag("orya")] = new Config(0x0B4D, RephPosition.AfterMain, RephMode.Implicit, BelowMode.PreAndPost),
        [Tag("taml")] = new Config(0x0BCD, RephPosition.AfterPost, RephMode.Implicit, BelowMode.PreAndPost),
        [Tag("telu")] = new Config(0x0C4D, RephPosition.AfterPost, RephMode.Explicit, BelowMode.PostOnly),
        [Tag("knda")] = new Config(0x0CCD, RephPosition.AfterPost, RephMode.Implicit, BelowMode.PostOnly),
        [Tag("mlym")] = new Config(0x0D4D, RephPosition.AfterMain, RephMode.LogicalRepha, BelowMode.PreAndPost),
    };

    /// <summary>The script tags of the Indic scripts, old and new, by the range the characters are in.</summary>
    public static uint ScriptOf(int codepoint) => codepoint switch
    {
        >= 0x0900 and <= 0x097F => Tag("deva"),
        >= 0x0980 and <= 0x09FF => Tag("beng"),
        >= 0x0A00 and <= 0x0A7F => Tag("guru"),
        >= 0x0A80 and <= 0x0AFF => Tag("gujr"),
        >= 0x0B00 and <= 0x0B7F => Tag("orya"),
        >= 0x0B80 and <= 0x0BFF => Tag("taml"),
        >= 0x0C00 and <= 0x0C7F => Tag("telu"),
        >= 0x0C80 and <= 0x0CFF => Tag("knda"),
        >= 0x0D00 and <= 0x0D7F => Tag("mlym"),
        >= 0x1CD0 and <= 0x1CFF => Tag("deva"),   // the Vedic extensions are written with Devanagari
        >= 0xA8E0 and <= 0xA8FF => Tag("deva"),
        _ => 0,
    };

    public static bool IsIndic(uint script) => script != 0 && Configs.ContainsKey(script);

    /// <summary>The tag of the script as the font names it: the newer one where the font has it.</summary>
    public static uint ScriptTag(TrueTypeFont font, uint script)
    {
        var modern = Modern(script);
        return modern != 0 && font.Substitution.HasScript(modern) ? modern : script;
    }

    /// <summary>
    /// The second tag each Indic script was given when OpenType changed what it asks of a font. A font
    /// that carries it follows the newer rules; the names are not a shortening of the older ones.
    /// </summary>
    private static readonly Dictionary<uint, uint> ModernTags = new()
    {
        [Tag("deva")] = Tag("dev2"),
        [Tag("beng")] = Tag("bng2"),
        [Tag("guru")] = Tag("gur2"),
        [Tag("gujr")] = Tag("gjr2"),
        [Tag("orya")] = Tag("ory2"),
        [Tag("taml")] = Tag("tml2"),
        [Tag("telu")] = Tag("tel2"),
        [Tag("knda")] = Tag("knd2"),
        [Tag("mlym")] = Tag("mlm2"),
    };

    private static uint Modern(uint script) => ModernTags.GetValueOrDefault(script);

    /// <summary>
    /// Shapes a run of Indic text into <paramref name="buffer"/>: the characters are split into
    /// syllables, and each syllable is ordered and drawn as the font's rules say.
    /// </summary>
    public static void Shape(TrueTypeFont font, ReadOnlySpan<int> text, uint script, ShapingBuffer buffer)
    {
        buffer.Clear();
        if (text.Length == 0)
            return;

        var tag = ScriptTag(font, script);
        var oldSpec = tag == script;
        var config = Configs.TryGetValue(script, out var known) ? known : Configs[Tag("deva")];

        // A vowel sign written in two parts is taken apart first: its pieces go to different places
        // in the syllable, one of them before the consonant.
        var characters = new List<int>(text.Length + 4);
        var clusters = new List<int>(text.Length + 4);
        Decompose(text, characters, clusters);

        var decomposed = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(characters);
        var categories = new IndicCategory[decomposed.Length];
        var positions = new IndicPosition[decomposed.Length];
        IndicSyllables.Classify(decomposed, categories, positions);

        var syllables = new List<(int Start, int End, IndicSyllableKind Kind)>();
        IndicSyllables.Split(categories, syllables);

        var syllable = new ShapingBuffer();

        // What a font offers is asked for without any context around it, except where the rules of a
        // script are written with context in mind.
        var zeroContext = !oldSpec && script != Tag("mlym");

        foreach (var (start, end, kind) in syllables)
        {
            syllable.Clear();
            for (var i = start; i < end; i++)
            {
                syllable.Add(font.GetGlyph(decomposed[i]), clusters[i], JoiningForm.Isolated);
                syllable[syllable.Length - 1].Category = (byte)categories[i];
                syllable[syllable.Length - 1].Position = (byte)positions[i];
                syllable[syllable.Length - 1].JoinerKind = categories[i] switch
                {
                    IndicCategory.ZWJ => JoinerKind.Joiner,
                    IndicCategory.ZWNJ => JoinerKind.NonJoiner,
                    _ => JoinerKind.None,
                };
            }

            // A syllable the rules could not make sense of is shown around a dotted circle, which is
            // what a reader expects to see when text is broken.
            if (kind == IndicSyllableKind.Broken && font.GetGlyph(0x25CC) is var circle and not 0)
            {
                syllable.Insert(0, circle, clusters[start]);
                syllable[0].Category = (byte)IndicCategory.DottedCircle;
                syllable[0].Position = (byte)IndicPosition.BaseC;
            }

            ShapeSyllable(font, syllable, script, tag, config, oldSpec, kind, start == 0, zeroContext);

            for (var i = 0; i < syllable.Length; i++)
            {
                // The joiners did their work by being there; they have nothing to draw.
                if ((IndicCategory)syllable[i].Category is IndicCategory.ZWJ or IndicCategory.ZWNJ && !syllable[i].Ligated)
                    syllable[i].Invisible = true;

                buffer.Add(syllable[i]);
            }
        }

        buffer.RemoveInvisible();
    }

    /// <summary>The features that are applied before the syllable is put in order.</summary>
    private static readonly (uint Feature, uint Mask, bool StepOverJoiners)[] FirstStage =
        [(Rvrn, 0, true), (Locl, 0, true), (Ccmp, 0, true)];

    /// <summary>
    /// The features that finish the syllable. They are applied together, in the order the font lists
    /// their lookups: fonts interleave the lookups of these features on purpose.
    /// </summary>
    private static readonly (uint Feature, uint Mask, bool StepOverJoiners)[] LastStage =
    [
        // The features of the script see the joiners: a non-joiner is there to keep letters apart.
        (Init, MaskInit, false), (Pres, 0, false), (Abvs, 0, false), (Blws, 0, false), (Psts, 0, false), (Haln, 0, false),

        // The ones every script gets step over them, as they do everywhere else.
        (Rlig, 0, true), (Calt, 0, true), (Clig, 0, true), (Rclt, 0, true),
    ];

    /// <summary>
    /// Takes every character apart as Unicode says it is composed, so that each piece can be placed
    /// where it belongs. A few letters are left alone: they are written as a composition but are read
    /// as letters of their own.
    /// </summary>
    private static void Decompose(ReadOnlySpan<int> text, List<int> characters, List<int> clusters)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var codepoint = text[i];

            // Bengali writes YYA as a letter with a nukta under it; Unicode keeps the two apart, but
            // the fonts draw the letter, so the two are put back together here.
            if (codepoint == 0x09AF && i + 1 < text.Length && text[i + 1] == 0x09BC)
            {
                characters.Add(0x09DF);
                clusters.Add(i);
                i++;
                continue;
            }

            if (Parts(codepoint) is { } parts)
            {
                foreach (var part in parts)
                {
                    characters.Add(part);
                    clusters.Add(i);
                }

                continue;
            }

            characters.Add(codepoint);
            clusters.Add(i);
        }
    }

    /// <summary>The pieces a character is written with, or null when it stands on its own.</summary>
    private static int[]? Parts(int codepoint)
    {
        // Letters that Unicode composes but that are read as letters of their own.
        if (codepoint is 0x0931 or 0x09DC or 0x09DD or 0x09DF or 0x0B94 or < 0x0900)
            return null;

        var text = char.ConvertFromUtf32(codepoint);
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        if (decomposed == text)
            return null;

        var parts = new List<int>(3);
        foreach (var rune in decomposed.EnumerateRunes())
            parts.Add(rune.Value);

        return parts.Count > 1 ? [.. parts] : null;
    }

    private static void ShapeSyllable(
        TrueTypeFont font,
        ShapingBuffer buffer,
        uint script,
        uint tag,
        Config config,
        bool oldSpec,
        IndicSyllableKind kind,
        bool startsRun,
        bool zeroContext)
    {
        var gsub = font.Substitution;
        gsub.ApplyStage(tag, FirstStage, buffer);

        if (kind is IndicSyllableKind.Consonant or IndicSyllableKind.Vowel or IndicSyllableKind.Standalone or IndicSyllableKind.Broken)
        {
            UpdateConsonantPositions(font, buffer, tag, config, zeroContext);
            InitialReorder(font, buffer, script, tag, config, oldSpec, zeroContext);
        }

        // The basic features, one at a time: each one works on what the one before it produced.
        gsub.ApplyFeature(tag, Nukt, buffer, 0, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Akhn, buffer, 0, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Rphf, buffer, MaskRphf, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Rkrf, buffer, 0, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Pref, buffer, MaskPref, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Blwf, buffer, MaskBlwf, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Abvf, buffer, MaskAbvf, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Half, buffer, MaskHalf, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Pstf, buffer, MaskPstf, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Vatu, buffer, 0, stepOverJoiners: false);
        gsub.ApplyFeature(tag, Cjct, buffer, 0, stepOverJoiners: false);

        if (kind is IndicSyllableKind.Consonant or IndicSyllableKind.Vowel or IndicSyllableKind.Standalone or IndicSyllableKind.Broken)
            FinalReorder(font, buffer, script, tag, config, startsRun);

        gsub.ApplyStage(tag, LastStage, buffer);
    }

    // ---- What the font can do --------------------------------------------------------------------

    /// <summary>
    /// Asks the font where each consonant goes: a letter that has a below-base form belongs below the
    /// base, one with a post-base form after it. The question is put as a pair of glyphs the feature
    /// would replace.
    /// </summary>
    private static void UpdateConsonantPositions(TrueTypeFont font, ShapingBuffer buffer, uint tag, Config config, bool zeroContext)
    {
        var virama = font.GetGlyph(config.Virama);
        if (virama == 0)
            return;

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i].Position != (byte)IndicPosition.BaseC)
                continue;

            buffer[i].Position = (byte)ConsonantPosition(font, tag, virama, buffer[i].Glyph, zeroContext);
        }
    }

    private static IndicPosition ConsonantPosition(TrueTypeFont font, uint tag, ushort virama, ushort consonant, bool zeroContext)
    {
        // Fonts written for the older rules put the consonant first, newer ones the virama; both are
        // asked about, because fonts exist that keep the older lookups under the newer script tag.
        Span<ushort> before = [virama, consonant];
        Span<ushort> after = [consonant, virama];
        var gsub = font.Substitution;

        if (gsub.WouldSubstitute(tag, Blwf, before, zeroContext) || gsub.WouldSubstitute(tag, Blwf, after, zeroContext) ||
            gsub.WouldSubstitute(tag, Vatu, before, zeroContext) || gsub.WouldSubstitute(tag, Vatu, after, zeroContext))
        {
            return IndicPosition.BelowC;
        }

        if (gsub.WouldSubstitute(tag, Pstf, before, zeroContext) || gsub.WouldSubstitute(tag, Pstf, after, zeroContext))
            return IndicPosition.PostC;

        if (gsub.WouldSubstitute(tag, Pref, before, zeroContext) || gsub.WouldSubstitute(tag, Pref, after, zeroContext))
            return IndicPosition.PostC;

        return IndicPosition.BaseC;
    }

    // ---- Ordering the syllable -------------------------------------------------------------------

    private static bool IsConsonant(in ShapedGlyph glyph) =>
        !glyph.Ligated && (IndicCategory)glyph.Category is
            IndicCategory.C or IndicCategory.CS or IndicCategory.Ra or IndicCategory.CM or
            IndicCategory.V or IndicCategory.Placeholder or IndicCategory.DottedCircle;

    private static bool IsJoiner(in ShapedGlyph glyph) =>
        !glyph.Ligated && (IndicCategory)glyph.Category is IndicCategory.ZWJ or IndicCategory.ZWNJ;

    private static bool IsHalant(in ShapedGlyph glyph) =>
        !glyph.Ligated && (IndicCategory)glyph.Category == IndicCategory.H;

    private static bool Is(in ShapedGlyph glyph, IndicCategory category) =>
        !glyph.Ligated && (IndicCategory)glyph.Category == category;

    /// <summary>
    /// Puts the syllable in the order it is drawn in: the base consonant is found, everything before
    /// it becomes a pre-base form, everything after it a below- or post-base one, and the vowel signs
    /// are sorted into the places they belong.
    /// </summary>
    private static void InitialReorder(TrueTypeFont font, ShapingBuffer buffer, uint script, uint tag, Config config, bool oldSpec, bool zeroContext)
    {
        var end = buffer.Length;
        if (end == 0)
            return;

        var gsub = font.Substitution;
        var hasReph = false;
        var limit = 0;
        var baseIndex = end;

        // 1. Find the base consonant, starting from the end of the syllable.
        if (gsub.Has(tag, Rphf) && end >= 3 &&
            ((config.Reph == RephMode.Implicit && !IsJoiner(buffer[2])) ||
             (config.Reph == RephMode.Explicit && Is(buffer[2], IndicCategory.ZWJ))))
        {
            Span<ushort> reph = config.Reph == RephMode.Explicit
                ? [buffer[0].Glyph, buffer[1].Glyph, buffer[2].Glyph]
                : [buffer[0].Glyph, buffer[1].Glyph];

            if (gsub.WouldSubstitute(tag, Rphf, reph[..2], zeroContext) ||
                (config.Reph == RephMode.Explicit && gsub.WouldSubstitute(tag, Rphf, reph, zeroContext)))
            {
                limit = 2;
                while (limit < end && IsJoiner(buffer[limit]))
                    limit++;

                baseIndex = 0;
                hasReph = true;
            }
        }
        else if (config.Reph == RephMode.LogicalRepha && Is(buffer[0], IndicCategory.Repha))
        {
            limit = 1;
            while (limit < end && IsJoiner(buffer[limit]))
                limit++;

            baseIndex = 0;
            hasReph = true;
        }

        {
            var i = end;
            var seenBelow = false;
            do
            {
                i--;
                if (IsConsonant(buffer[i]))
                {
                    // A consonant with no below-base or post-base form of its own is the base.
                    if (buffer[i].Position != (byte)IndicPosition.BelowC &&
                        (buffer[i].Position != (byte)IndicPosition.PostC || seenBelow))
                    {
                        baseIndex = i;
                        break;
                    }

                    if (buffer[i].Position == (byte)IndicPosition.BelowC)
                        seenBelow = true;

                    baseIndex = i;
                }
                else if (i > 0 && Is(buffer[i], IndicCategory.ZWJ) && IsHalant(buffer[i - 1]))
                {
                    // A joiner after a virama asks for a half form, and stops the search.
                    break;
                }
            }
            while (i > limit);
        }

        // A reph needs a consonant of its own to sit on.
        if (hasReph && baseIndex == 0 && limit <= 2)
            hasReph = false;

        // Everything before the base is drawn before it.
        for (var i = 0; i < baseIndex; i++)
            buffer[i].Position = (byte)Math.Min((int)IndicPosition.PreC, (int)buffer[i].Position);

        if (baseIndex < end)
            buffer[baseIndex].Position = (byte)IndicPosition.BaseC;

        if (hasReph)
            buffer[0].Position = (byte)IndicPosition.RaToBecomeReph;

        // Fonts written for the older rules expect the virama after the last consonant.
        if (oldSpec)
        {
            var disallowDouble = script == Tag("knda");
            for (var i = baseIndex + 1; i < end; i++)
            {
                if (!Is(buffer[i], IndicCategory.H))
                    continue;

                var j = end - 1;
                for (; j > i; j--)
                {
                    if (IsConsonant(buffer[j]) || (disallowDouble && Is(buffer[j], IndicCategory.H)))
                        break;
                }

                if (!Is(buffer[j], IndicCategory.H) && j > i)
                    buffer.Move(i, j);

                break;
            }
        }

        // The marks travel with the letter they belong to.
        {
            var lastPosition = IndicPosition.Start;
            for (var i = 0; i < end; i++)
            {
                var category = (IndicCategory)buffer[i].Category;
                if (!buffer[i].Ligated && category is IndicCategory.ZWJ or IndicCategory.ZWNJ or IndicCategory.N or
                    IndicCategory.RS or IndicCategory.CM or IndicCategory.H)
                {
                    buffer[i].Position = (byte)lastPosition;

                    // A virama does not travel with a vowel sign written before the consonant.
                    if (category == IndicCategory.H && buffer[i].Position == (byte)IndicPosition.PreM)
                    {
                        for (var j = i; j > 0; j--)
                        {
                            if (buffer[j - 1].Position != (byte)IndicPosition.PreM)
                            {
                                buffer[i].Position = buffer[j - 1].Position;
                                break;
                            }
                        }
                    }
                }
                else if (buffer[i].Position != (byte)IndicPosition.Smvd)
                {
                    if (category == IndicCategory.MPst && i > 0 && Is(buffer[i - 1], IndicCategory.SM))
                        buffer[i - 1].Position = buffer[i].Position;

                    lastPosition = (IndicPosition)buffer[i].Position;
                }
            }
        }

        // A consonant after the base takes with it everything since the last consonant or vowel sign.
        {
            var last = baseIndex;
            for (var i = baseIndex + 1; i < end; i++)
            {
                if (IsConsonant(buffer[i]))
                {
                    for (var j = last + 1; j < i; j++)
                    {
                        if (buffer[j].Position < (byte)IndicPosition.Smvd)
                            buffer[j].Position = buffer[i].Position;
                    }

                    last = i;
                }
                else if ((IndicCategory)buffer[i].Category is IndicCategory.M or IndicCategory.MPst)
                {
                    last = i;
                }
            }
        }

        buffer.StableSortByPosition(0, end);

        // Find the base again, and put a run of vowel signs written before the consonant back in order.
        var firstLeft = end;
        var lastLeft = end;
        baseIndex = end;
        for (var i = 0; i < end; i++)
        {
            if (buffer[i].Position == (byte)IndicPosition.BaseC)
            {
                baseIndex = i;
                break;
            }

            if (buffer[i].Position == (byte)IndicPosition.PreM)
            {
                if (firstLeft == end)
                    firstLeft = i;

                lastLeft = i;
            }
        }

        if (firstLeft < lastLeft)
        {
            buffer.Reverse(firstLeft, lastLeft + 1);

            // The marks of each vowel sign are turned back the right way round.
            var i = firstLeft;
            for (var j = i; j <= lastLeft; j++)
            {
                if ((IndicCategory)buffer[j].Category is IndicCategory.M or IndicCategory.MPst)
                {
                    buffer.Reverse(i, j + 1);
                    i = j + 1;
                }
            }
        }

        // Which glyphs take part in which feature.
        for (var i = 0; i < end && buffer[i].Position == (byte)IndicPosition.RaToBecomeReph; i++)
            buffer[i].Features |= MaskRphf;

        var mask = MaskHalf;
        if (!oldSpec && config.Below == BelowMode.PreAndPost)
            mask |= MaskBlwf;

        for (var i = 0; i < baseIndex; i++)
            buffer[i].Features |= mask;

        for (var i = baseIndex + 1; i < end; i++)
            buffer[i].Features |= MaskBlwf | MaskAbvf | MaskPstf;

        // The eye-lash Ra of the older Devanagari rules is asked for with the below-base form.
        if (oldSpec && script == Tag("deva"))
        {
            for (var i = 0; i + 1 < baseIndex; i++)
            {
                if (Is(buffer[i], IndicCategory.Ra) && Is(buffer[i + 1], IndicCategory.H) &&
                    (i + 2 == baseIndex || !Is(buffer[i + 2], IndicCategory.ZWJ)))
                {
                    buffer[i].Features |= MaskBlwf;
                    buffer[i + 1].Features |= MaskBlwf;
                }
            }
        }

        // A virama and Ra after the base may be drawn before it, which the font decides.
        if (gsub.Has(tag, Pref) && baseIndex + 2 < end)
        {
            for (var i = baseIndex + 1; i + 1 < end; i++)
            {
                Span<ushort> pair = [buffer[i].Glyph, buffer[i + 1].Glyph];
                if (gsub.WouldSubstitute(tag, Pref, pair, zeroContext))
                {
                    buffer[i].Features |= MaskPref;
                    buffer[i + 1].Features |= MaskPref;
                    break;
                }
            }
        }

        // A non-joiner asks for the full form of the letter before it rather than the half form.
        for (var i = 1; i < end; i++)
        {
            if (!IsJoiner(buffer[i]))
                continue;

            var nonJoiner = Is(buffer[i], IndicCategory.ZWNJ);
            var j = i;
            do
            {
                j--;
                if (nonJoiner)
                    buffer[j].Features &= ~MaskHalf;
            }
            while (j > 0 && !IsConsonant(buffer[j]));
        }
    }

    /// <summary>
    /// Moves what the font has now drawn into its final place: the vowel sign written before the
    /// consonant goes as close to it as the half forms allow, the reph climbs to where the script
    /// puts it, and a pre-base Ra moves before the base.
    /// </summary>
    private static void FinalReorder(TrueTypeFont font, ShapingBuffer buffer, uint script, uint tag, Config config, bool startsRun)
    {
        var end = buffer.Length;
        if (end == 0)
            return;

        var gsub = font.Substitution;
        var tryPref = gsub.Has(tag, Pref);

        // The virama may have been drawn as part of something else; where it was, it still acts as one.
        var virama = font.GetGlyph(config.Virama);
        if (virama != 0)
        {
            for (var i = 0; i < end; i++)
            {
                if (buffer[i].Glyph == virama && buffer[i].Ligated && buffer[i].Multiplied)
                {
                    buffer[i].Category = (byte)IndicCategory.H;
                    buffer[i].Ligated = false;
                    buffer[i].Multiplied = false;
                }
            }
        }

        // Find the base again.
        int baseIndex;
        for (baseIndex = 0; baseIndex < end; baseIndex++)
        {
            if (buffer[baseIndex].Position < (byte)IndicPosition.BaseC)
                continue;

            if (tryPref && baseIndex + 1 < end)
            {
                for (var i = baseIndex + 1; i < end; i++)
                {
                    if ((buffer[i].Features & MaskPref) == 0)
                        continue;

                    if (!(buffer[i].Substituted && buffer[i].LigatedAlone))
                    {
                        // The font offers the form but did not draw it, so the base is around here.
                        baseIndex = i;
                        while (baseIndex < end && IsHalant(buffer[baseIndex]))
                            baseIndex++;

                        if (baseIndex < end)
                            buffer[baseIndex].Position = (byte)IndicPosition.BaseC;

                        tryPref = false;
                    }

                    break;
                }

                if (baseIndex == end)
                    break;
            }

            // Malayalam skips over below-base forms the font did not draw.
            if (script == Tag("mlym"))
            {
                for (var i = baseIndex + 1; i < end; i++)
                {
                    while (i < end && IsJoiner(buffer[i]))
                        i++;

                    if (i == end || !IsHalant(buffer[i]))
                        break;

                    i++;
                    while (i < end && IsJoiner(buffer[i]))
                        i++;

                    if (i < end && IsConsonant(buffer[i]) && buffer[i].Position == (byte)IndicPosition.BelowC)
                    {
                        baseIndex = i;
                        buffer[baseIndex].Position = (byte)IndicPosition.BaseC;
                    }
                }
            }

            if (baseIndex > 0 && buffer[baseIndex].Position > (byte)IndicPosition.BaseC)
                baseIndex--;

            break;
        }

        if (baseIndex == end && baseIndex > 0 && Is(buffer[baseIndex - 1], IndicCategory.ZWJ))
            baseIndex--;

        if (baseIndex < end)
        {
            while (baseIndex > 0 && (Is(buffer[baseIndex], IndicCategory.N) || Is(buffer[baseIndex], IndicCategory.H)))
                baseIndex--;
        }

        // The vowel sign written before the consonant moves as close to it as the half forms allow.
        if (end > 1 && baseIndex > 0)
        {
            var newPosition = baseIndex == end ? baseIndex - 2 : baseIndex - 1;

            // Malayalam and Tamil have no half forms, so nothing is moved past them.
            if (script != Tag("mlym") && script != Tag("taml"))
            {
                while (true)
                {
                    while (newPosition > 0 &&
                        !(Is(buffer[newPosition], IndicCategory.M) || Is(buffer[newPosition], IndicCategory.MPst) || Is(buffer[newPosition], IndicCategory.H)))
                    {
                        newPosition--;
                    }

                    if (IsHalant(buffer[newPosition]) && buffer[newPosition].Position != (byte)IndicPosition.PreM)
                    {
                        // A joiner after the virama keeps the vowel sign where it is.
                        if (newPosition + 1 < end && Is(buffer[newPosition + 1], IndicCategory.ZWJ) && newPosition > 0)
                        {
                            newPosition--;
                            continue;
                        }
                    }
                    else
                    {
                        newPosition = 0;
                    }

                    break;
                }
            }

            if (newPosition > 0 && buffer[newPosition].Position != (byte)IndicPosition.PreM)
            {
                for (var i = newPosition; i > 0; i--)
                {
                    if (buffer[i - 1].Position != (byte)IndicPosition.PreM)
                        continue;

                    var old = i - 1;
                    if (old < baseIndex && baseIndex <= newPosition)
                        baseIndex--;

                    buffer.Move(old, newPosition);
                    newPosition--;
                }
            }
        }

        // The reph climbs to where the script puts it.
        if (end > 1 && buffer[0].Position == (byte)IndicPosition.RaToBecomeReph &&
            Is(buffer[0], IndicCategory.Repha) != buffer[0].LigatedAlone)
        {
            var rephPosition = config.RephPos;
            var moved = false;
            var newReph = 0;

            if (rephPosition != RephPosition.AfterPost)
            {
                // After the first virama between the reph and the last consonant.
                newReph = 1;
                while (newReph < baseIndex && !IsHalant(buffer[newReph]))
                    newReph++;

                if (newReph < baseIndex && IsHalant(buffer[newReph]))
                {
                    if (newReph + 1 < baseIndex && IsJoiner(buffer[newReph + 1]))
                        newReph++;

                    moved = true;
                }

                if (!moved && rephPosition == RephPosition.AfterMain)
                {
                    newReph = baseIndex;
                    while (newReph + 1 < end && buffer[newReph + 1].Position <= (byte)IndicPosition.AfterMain)
                        newReph++;

                    moved = newReph < end;
                }

                if (!moved && rephPosition == RephPosition.AfterSub)
                {
                    newReph = baseIndex;
                    while (newReph + 1 < end &&
                        buffer[newReph + 1].Position is not ((byte)IndicPosition.PostC or (byte)IndicPosition.AfterPost or (byte)IndicPosition.Smvd))
                    {
                        newReph++;
                    }

                    moved = newReph < end;
                }
            }
            else
            {
                newReph = 1;
                while (newReph < baseIndex && !IsHalant(buffer[newReph]))
                    newReph++;

                if (newReph < baseIndex && IsHalant(buffer[newReph]))
                {
                    if (newReph + 1 < baseIndex && IsJoiner(buffer[newReph + 1]))
                        newReph++;

                    moved = true;
                }
            }

            if (!moved)
            {
                // Otherwise it goes to the end of the syllable, before the marks that hang there.
                newReph = end - 1;
                while (newReph > 0 && buffer[newReph].Position == (byte)IndicPosition.Smvd)
                    newReph--;

                // A reph that would end up after a vowel sign and a virama goes before that virama.
                if (IsHalant(buffer[newReph]))
                {
                    for (var i = baseIndex + 1; i < newReph; i++)
                    {
                        if ((IndicCategory)buffer[i].Category is IndicCategory.M or IndicCategory.MPst)
                            newReph--;
                    }
                }
            }

            buffer.Move(0, newReph);
            if (baseIndex > 0 && baseIndex <= newReph)
                baseIndex--;
        }

        // A Ra that is drawn before the base moves there.
        if (tryPref && baseIndex + 1 < end)
        {
            for (var i = baseIndex + 1; i < end; i++)
            {
                if ((buffer[i].Features & MaskPref) == 0)
                    continue;

                if (buffer[i].LigatedAlone)
                {
                    var newPosition = baseIndex;
                    if (script != Tag("mlym") && script != Tag("taml"))
                    {
                        while (newPosition > 0 &&
                            !(Is(buffer[newPosition - 1], IndicCategory.M) || Is(buffer[newPosition - 1], IndicCategory.MPst) ||
                              Is(buffer[newPosition - 1], IndicCategory.H)))
                        {
                            newPosition--;
                        }
                    }

                    if (newPosition > 0 && IsHalant(buffer[newPosition - 1]) && newPosition < end && IsJoiner(buffer[newPosition]))
                        newPosition++;

                    buffer.Move(i, newPosition);
                    if (newPosition <= baseIndex && baseIndex < i)
                        baseIndex++;
                }

                break;
            }
        }

        // The vowel sign that starts a word is drawn with the form the font keeps for that.
        if (buffer[0].Position == (byte)IndicPosition.PreM && startsRun)
            buffer[0].Features |= MaskInit;
    }
}
