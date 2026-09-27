namespace Papira.Text;

/// <summary>
/// What a character is in a syllable of an Indic script. The names are the ones the OpenType Indic
/// script development specification uses.
/// </summary>
internal enum IndicCategory : byte
{
    /// <summary>Anything the shaper has no rules for.</summary>
    X = 0,

    /// <summary>A consonant.</summary>
    C = 1,

    /// <summary>An independent vowel.</summary>
    V = 2,

    /// <summary>A nukta, which changes the consonant it sits under.</summary>
    N = 3,

    /// <summary>A virama (halant), which strips the vowel of the consonant before it.</summary>
    H = 4,

    ZWNJ = 5,
    ZWJ = 6,

    /// <summary>A dependent vowel sign (a matra).</summary>
    M = 7,

    /// <summary>A syllable modifier: a bindu, a visarga, a gemination mark.</summary>
    SM = 8,

    /// <summary>A cantillation mark of the Vedas, or another mark that hangs at the end.</summary>
    A = 9,

    /// <summary>Something a syllable can be built around although it is not a letter, such as a digit.</summary>
    Placeholder = 10,

    /// <summary>The dotted circle a broken syllable is shown around.</summary>
    DottedCircle = 11,

    /// <summary>A register shifter, used by Khmer.</summary>
    RS = 12,

    /// <summary>A matra that always follows the base.</summary>
    MPst = 13,

    /// <summary>A repha written as a character of its own, as Malayalam does.</summary>
    Repha = 14,

    /// <summary>The letter Ra, which forms the reph.</summary>
    Ra = 15,

    /// <summary>A medial or subjoined consonant.</summary>
    CM = 16,

    /// <summary>An avagraha and its like, which take marks of their own.</summary>
    Symbol = 17,

    /// <summary>A consonant with a stacker.</summary>
    CS = 18,

    /// <summary>A syllable modifier that follows the base.</summary>
    SMPst = 19,
}

/// <summary>Where a glyph goes in a syllable, from the left of it to the right.</summary>
internal enum IndicPosition : byte
{
    Start = 0,
    RaToBecomeReph = 1,
    PreM = 2,
    PreC = 3,
    BaseC = 4,
    AfterMain = 5,
    AboveC = 6,
    BeforeSub = 7,
    BelowC = 8,
    AfterSub = 9,
    BeforePost = 10,
    PostC = 11,
    AfterPost = 12,
    Smvd = 13,
    End = 14,
}

/// <summary>The kinds of syllable the text is split into.</summary>
internal enum IndicSyllableKind : byte
{
    Consonant,
    Vowel,
    Standalone,
    Symbol,
    Broken,
    NonIndic,
}

/// <summary>
/// Splits a run of Indic text into syllables. A syllable is a consonant or a vowel with everything
/// that belongs to it — the marks above and below it, the consonants joined to it, the vowel sign
/// that is written before it — and it is the unit the shaping rules work on.
/// </summary>
/// <remarks>
/// The grammar is the one of the OpenType Indic specification, written out as a matcher: at every
/// position the longest of the six kinds of syllable is taken, and what matches nothing at all is a
/// syllable of one character. The categories are read from the Unicode Character Database.
/// </remarks>
internal static class IndicSyllables
{
    /// <summary>What each character of the run is, and where in a syllable it belongs.</summary>
    public static void Classify(ReadOnlySpan<int> text, Span<IndicCategory> categories, Span<IndicPosition> positions)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var (category, position) = Lookup(text[i]);
            categories[i] = category;
            positions[i] = position;
        }
    }

    /// <summary>What the Unicode Character Database says about a character.</summary>
    public static (IndicCategory Category, IndicPosition Position) Lookup(int codepoint)
    {
        var starts = IndicTables.Starts;
        var low = 0;
        var high = starts.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (codepoint < starts[middle])
                high = middle - 1;
            else if (codepoint > IndicTables.Ends[middle])
                low = middle + 1;
            else
                return ((IndicCategory)IndicTables.Categories[middle], (IndicPosition)IndicTables.Positions[middle]);
        }

        return (IndicCategory.X, IndicPosition.End);
    }

    /// <summary>
    /// The syllables of a run, in order. Each one is the stretch it covers and what kind it is.
    /// </summary>
    public static void Split(ReadOnlySpan<IndicCategory> categories, List<(int Start, int End, IndicSyllableKind Kind)> syllables)
    {
        syllables.Clear();
        var position = 0;
        while (position < categories.Length)
        {
            var kind = IndicSyllableKind.NonIndic;
            var end = position + 1;

            // The longest of the kinds wins; where two are equally long, the first one listed does.
            foreach (var candidate in Kinds)
            {
                var match = Match(categories, position, candidate);
                if (match > end || (match == end && match > position + 1 && kind == IndicSyllableKind.NonIndic))
                {
                    end = match;
                    kind = candidate;
                }
            }

            // A syllable of one character that matched a real rule is still that kind of syllable.
            if (end == position + 1 && kind == IndicSyllableKind.NonIndic)
            {
                foreach (var candidate in Kinds)
                {
                    if (Match(categories, position, candidate) == end)
                    {
                        kind = candidate;
                        break;
                    }
                }
            }

            syllables.Add((position, end, kind));
            position = end;
        }
    }

    private static readonly IndicSyllableKind[] Kinds =
    [
        IndicSyllableKind.Consonant,
        IndicSyllableKind.Vowel,
        IndicSyllableKind.Standalone,
        IndicSyllableKind.Symbol,
        IndicSyllableKind.Broken,
    ];

    private static int Match(ReadOnlySpan<IndicCategory> c, int i, IndicSyllableKind kind) => kind switch
    {
        // consonant_syllable = (Repha|CS)? cn complex_syllable_tail
        IndicSyllableKind.Consonant => Consonant(c, i),

        // vowel_syllable = reph? V n? (ZWJ | complex_syllable_tail)
        IndicSyllableKind.Vowel => Vowel(c, i),

        // standalone_cluster = ((Repha|CS)? PLACEHOLDER | reph? DOTTEDCIRCLE) n? complex_syllable_tail
        IndicSyllableKind.Standalone => Standalone(c, i),

        // symbol_cluster = symbol syllable_tail
        IndicSyllableKind.Symbol => Symbol(c, i),

        // broken_cluster = reph? n? complex_syllable_tail
        IndicSyllableKind.Broken => Broken(c, i),

        _ => i,
    };

    private static int Consonant(ReadOnlySpan<IndicCategory> c, int i)
    {
        var start = i;
        if (At(c, i) is IndicCategory.Repha or IndicCategory.CS)
            i++;

        var after = Cn(c, i);
        if (after == i)
            return start;

        return ComplexTail(c, after);
    }

    private static int Vowel(ReadOnlySpan<IndicCategory> c, int i)
    {
        var start = i;
        i = Reph(c, i);
        if (At(c, i) != IndicCategory.V)
            return start;

        i = N(c, i + 1);

        // Either a joiner ends the syllable, or everything a consonant syllable may hold follows.
        if (At(c, i) == IndicCategory.ZWJ)
            return i + 1;

        return ComplexTail(c, i);
    }

    private static int Standalone(ReadOnlySpan<IndicCategory> c, int i)
    {
        var start = i;
        var head = i;
        if (At(c, head) is IndicCategory.Repha or IndicCategory.CS)
            head++;

        if (At(c, head) == IndicCategory.Placeholder)
        {
            i = head + 1;
        }
        else
        {
            var reph = Reph(c, i);
            if (At(c, reph) != IndicCategory.DottedCircle)
                return start;

            i = reph + 1;
        }

        return ComplexTail(c, N(c, i));
    }

    private static int Symbol(ReadOnlySpan<IndicCategory> c, int i)
    {
        if (At(c, i) != IndicCategory.Symbol)
            return i;

        i++;
        if (At(c, i) == IndicCategory.N)
            i++;

        return SyllableTail(c, i);
    }

    private static int Broken(ReadOnlySpan<IndicCategory> c, int i)
    {
        var start = i;
        var end = ComplexTail(c, N(c, Reph(c, i)));
        return end > start ? end : start;
    }

    // ---- The pieces of the grammar ---------------------------------------------------------------

    private static IndicCategory At(ReadOnlySpan<IndicCategory> c, int i) =>
        (uint)i < (uint)c.Length ? c[i] : IndicCategory.X;

    private static bool IsConsonant(IndicCategory category) =>
        category is IndicCategory.C or IndicCategory.Ra;

    private static bool IsJoiner(IndicCategory category) =>
        category is IndicCategory.ZWJ or IndicCategory.ZWNJ;

    /// <summary>reph = (Ra H | Repha)</summary>
    private static int Reph(ReadOnlySpan<IndicCategory> c, int i)
    {
        if (At(c, i) == IndicCategory.Ra && At(c, i + 1) == IndicCategory.H)
            return i + 2;

        return At(c, i) == IndicCategory.Repha ? i + 1 : i;
    }

    /// <summary>n = ((ZWNJ? RS)? (N N?)?)</summary>
    private static int N(ReadOnlySpan<IndicCategory> c, int i)
    {
        var after = i;
        if (At(c, after) == IndicCategory.ZWNJ && At(c, after + 1) == IndicCategory.RS)
            after += 2;
        else if (At(c, after) == IndicCategory.RS)
            after++;

        if (At(c, after) == IndicCategory.N)
        {
            after++;
            if (At(c, after) == IndicCategory.N)
                after++;
        }

        return after;
    }

    /// <summary>cn = c ZWJ? n?</summary>
    private static int Cn(ReadOnlySpan<IndicCategory> c, int i)
    {
        if (!IsConsonant(At(c, i)))
            return i;

        i++;
        if (At(c, i) == IndicCategory.ZWJ)
            i++;

        return N(c, i);
    }

    /// <summary>halant_group = (z? H (ZWJ N?)?)</summary>
    private static int HalantGroup(ReadOnlySpan<IndicCategory> c, int i)
    {
        var after = i;
        if (IsJoiner(At(c, after)))
            after++;

        if (At(c, after) != IndicCategory.H)
            return i;

        after++;
        if (At(c, after) == IndicCategory.ZWJ)
        {
            after++;
            if (At(c, after) == IndicCategory.N)
                after++;
        }

        return after;
    }

    /// <summary>final_halant_group = halant_group | H ZWNJ</summary>
    private static int FinalHalantGroup(ReadOnlySpan<IndicCategory> c, int i)
    {
        var group = HalantGroup(c, i);
        if (At(c, i) == IndicCategory.H && At(c, i + 1) == IndicCategory.ZWNJ && i + 2 > group)
            return i + 2;

        return group;
    }

    /// <summary>matra_group = z* (M | sm? MPst) N? H?</summary>
    private static int MatraGroup(ReadOnlySpan<IndicCategory> c, int i)
    {
        var after = i;
        while (IsJoiner(At(c, after)))
            after++;

        if (At(c, after) == IndicCategory.M)
        {
            after++;
        }
        else
        {
            var matra = after;
            if (At(c, matra) is IndicCategory.SM or IndicCategory.SMPst)
                matra++;

            if (At(c, matra) != IndicCategory.MPst)
                return i;

            after = matra + 1;
        }

        if (At(c, after) == IndicCategory.N)
            after++;
        if (At(c, after) == IndicCategory.H)
            after++;

        return after;
    }

    /// <summary>syllable_tail = (z? sm sm? ZWNJ?)? (A | VD)*</summary>
    private static int SyllableTail(ReadOnlySpan<IndicCategory> c, int i)
    {
        var after = i;
        var marks = after;
        if (IsJoiner(At(c, marks)))
            marks++;

        if (At(c, marks) is IndicCategory.SM or IndicCategory.SMPst)
        {
            marks++;
            if (At(c, marks) is IndicCategory.SM or IndicCategory.SMPst)
                marks++;
            if (At(c, marks) == IndicCategory.ZWNJ)
                marks++;

            after = marks;
        }

        while (At(c, after) == IndicCategory.A)
            after++;

        return after;
    }

    /// <summary>complex_syllable_tail = (halant_group cn)* medial_group halant_or_matra_group syllable_tail</summary>
    private static int ComplexTail(ReadOnlySpan<IndicCategory> c, int i)
    {
        while (true)
        {
            var halant = HalantGroup(c, i);
            if (halant == i)
                break;

            var consonant = Cn(c, halant);
            if (consonant == halant)
                break;

            i = consonant;
        }

        // medial_group = CM?
        if (At(c, i) == IndicCategory.CM)
            i++;

        // halant_or_matra_group = (final_halant_group | matra_group*)
        var final = FinalHalantGroup(c, i);
        var matras = i;
        while (true)
        {
            var next = MatraGroup(c, matras);
            if (next == matras)
                break;

            matras = next;
        }

        i = Math.Max(final, matras);
        return SyllableTail(c, i);
    }
}
