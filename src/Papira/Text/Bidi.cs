namespace Papira.Text;

/// <summary>The bidirectional character classes of the Unicode standard, in the order the tables store them.</summary>
internal enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON,
    LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>
/// The Unicode bidirectional algorithm (UAX #9). It resolves an embedding level for every character of a
/// paragraph and reorders a line of it into the order the characters are drawn in.
/// </summary>
/// <remarks>
/// The implementation follows the rules of the standard by name (P2, X1–X10, W1–W7, N0–N2, I1, I2, L1, L2)
/// and is checked against the conformance files of the Unicode Character Database.
/// </remarks>
internal static class Bidi
{
    /// <summary>The deepest embedding the standard allows.</summary>
    private const int MaxDepth = 125;

    /// <summary>Brackets nested deeper than this are not paired (BD16).</summary>
    private const int MaxBracketPairs = 63;

    public static BidiClass ClassOf(int codepoint) => (BidiClass)(byte)Lookup(UnicodeTables.BidiClasses, codepoint, 5);

    public static JoiningType JoiningOf(int codepoint) => (JoiningType)(byte)Lookup(UnicodeTables.JoiningTypes, codepoint, 3);

    /// <summary>The mirrored form of a character, or the character itself when it has none.</summary>
    public static int Mirror(int codepoint)
    {
        var index = UnicodeTables.MirrorFrom.BinarySearch(codepoint);
        return index >= 0 ? UnicodeTables.MirrorTo[index] : codepoint;
    }

    private static int Lookup(ReadOnlySpan<int> runs, int codepoint, int shift)
    {
        if ((uint)codepoint > 0x10FFFF)
            return 0;

        var mask = (1 << shift) - 1;
        var low = 0;
        var high = runs.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (runs[middle] >> shift <= codepoint)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return high >= 0 ? runs[high] & mask : 0;
    }

    /// <summary>
    /// True when the paragraph holds nothing the algorithm would move: no right to left character, no Arabic
    /// number and no explicit formatting character. The blocks listed here cover every such character, which
    /// the tests check against the tables, so plain text skips the algorithm entirely.
    /// </summary>
    public static bool IsPlainLeftToRight(ReadOnlySpan<int> text)
    {
        foreach (var codepoint in text)
        {
            if (!IsPlainLeftToRight(codepoint))
                return false;
        }

        return true;
    }

    /// <summary>True when this one character is neither right to left nor an explicit formatting character.</summary>
    public static bool IsPlainLeftToRight(int codepoint) =>
        codepoint < 0x0590 || codepoint is not ((>= 0x0590 and <= 0x08FF)
            or 0x200F
            or (>= 0x202A and <= 0x202E)
            or (>= 0x2066 and <= 0x2069)
            or (>= 0xFB1D and <= 0xFEFF)
            or (>= 0x10800 and <= 0x10FFF)
            or (>= 0x1E800 and <= 0x1EFFF));

    /// <summary>
    /// Resolves the embedding levels of a paragraph. <paramref name="levels"/> receives one level per
    /// character; the paragraph level is returned.
    /// </summary>
    public static byte Resolve(ReadOnlySpan<int> text, TextDirection direction, Span<byte> levels)
    {
        var length = text.Length;
        var original = new BidiClass[length];
        for (var i = 0; i < length; i++)
            original[i] = ClassOf(text[i]);

        var paragraphLevel = direction switch
        {
            TextDirection.LeftToRight => (byte)0,
            TextDirection.RightToLeft => (byte)1,
            _ => AutoLevel(original, 0, length),
        };

        var codepoints = text.ToArray();
        var classes = (BidiClass[])original.Clone();
        var matchingPdi = MatchIsolates(original, out var matchingInitiator);
        ExplicitLevels(original, classes, levels, paragraphLevel, matchingPdi);

        // X9: the explicit formatting characters take no part in the rules that follow.
        var kept = new int[length];
        var keptCount = 0;
        for (var i = 0; i < length; i++)
        {
            if (!IsRemoved(original[i]))
                kept[keptCount++] = i;
        }

        // X10 works on the levels the explicit rules assigned; I1 and I2 then raise them run by run.
        var explicitLevels = levels[..length].ToArray();
        ResolveSequences(codepoints, original, classes, explicitLevels, levels, kept.AsSpan(0, keptCount), paragraphLevel, matchingPdi, matchingInitiator);
        return paragraphLevel;
    }

    /// <summary>P2 and P3: the level of the first strongly directional character outside an isolate.</summary>
    private static byte AutoLevel(ReadOnlySpan<BidiClass> classes, int start, int end)
    {
        var isolates = 0;
        for (var i = start; i < end; i++)
        {
            switch (classes[i])
            {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    isolates++;
                    break;
                case BidiClass.PDI:
                    if (isolates > 0)
                        isolates--;

                    break;
                case BidiClass.L when isolates == 0:
                    return 0;
                case BidiClass.R or BidiClass.AL when isolates == 0:
                    return 1;
            }
        }

        return 0;
    }

    /// <summary>BD9: the position of the PDI that closes each isolate initiator, or the end of the text.</summary>
    private static int[] MatchIsolates(ReadOnlySpan<BidiClass> classes, out int[] matchingInitiator)
    {
        var matching = new int[classes.Length];
        matchingInitiator = new int[classes.Length];
        var open = new Stack<int>();
        for (var i = 0; i < classes.Length; i++)
        {
            matching[i] = -1;
            matchingInitiator[i] = -1;
            switch (classes[i])
            {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    matching[i] = classes.Length;
                    open.Push(i);
                    break;
                case BidiClass.PDI when open.Count > 0:
                    var initiator = open.Pop();
                    matching[initiator] = i;
                    matchingInitiator[i] = initiator;
                    break;
            }
        }

        return matching;
    }

    private static bool IsRemoved(BidiClass value) =>
        value is BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;

    private static bool IsIsolateInitiator(BidiClass value) =>
        value is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    /// <summary>Neutral or isolate formatting characters, which N1 and N2 resolve.</summary>
    private static bool IsNeutral(BidiClass value) =>
        value is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;

    private static byte NextOdd(byte level) => (byte)((level + 1) | 1);

    private static byte NextEven(byte level) => (byte)((level + 2) & ~1);

    /// <summary>X1 to X8: the levels the explicit formatting characters set.</summary>
    private static void ExplicitLevels(
        ReadOnlySpan<BidiClass> original,
        Span<BidiClass> classes,
        Span<byte> levels,
        byte paragraphLevel,
        int[] matchingPdi)
    {
        Span<byte> stackLevel = stackalloc byte[MaxDepth + 2];
        Span<BidiClass> stackOverride = stackalloc BidiClass[MaxDepth + 2];
        Span<bool> stackIsolate = stackalloc bool[MaxDepth + 2];

        var depth = 0;
        stackLevel[0] = paragraphLevel;
        stackOverride[0] = BidiClass.ON;
        stackIsolate[0] = false;

        int overflowIsolate = 0, overflowEmbedding = 0, validIsolate = 0;

        for (var i = 0; i < original.Length; i++)
        {
            var value = original[i];
            switch (value)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    levels[i] = stackLevel[depth];
                    var level = value is BidiClass.RLE or BidiClass.RLO ? NextOdd(stackLevel[depth]) : NextEven(stackLevel[depth]);
                    if (level <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        depth++;
                        stackLevel[depth] = level;
                        stackOverride[depth] = value switch
                        {
                            BidiClass.RLO => BidiClass.R,
                            BidiClass.LRO => BidiClass.L,
                            _ => BidiClass.ON,
                        };
                        stackIsolate[depth] = false;
                    }
                    else if (overflowIsolate == 0)
                    {
                        overflowEmbedding++;
                    }

                    break;
                }

                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    // An FSI takes the direction of the text it encloses.
                    var rightToLeft = value == BidiClass.RLI ||
                        (value == BidiClass.FSI && AutoLevel(original, i + 1, Math.Min(matchingPdi[i], original.Length)) == 1);

                    levels[i] = stackLevel[depth];
                    if (stackOverride[depth] != BidiClass.ON)
                        classes[i] = stackOverride[depth];

                    var level = rightToLeft ? NextOdd(stackLevel[depth]) : NextEven(stackLevel[depth]);
                    if (level <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        validIsolate++;
                        depth++;
                        stackLevel[depth] = level;
                        stackOverride[depth] = BidiClass.ON;
                        stackIsolate[depth] = true;
                    }
                    else
                    {
                        overflowIsolate++;
                    }

                    break;
                }

                case BidiClass.PDI:
                {
                    if (overflowIsolate > 0)
                    {
                        overflowIsolate--;
                    }
                    else if (validIsolate > 0)
                    {
                        overflowEmbedding = 0;
                        while (!stackIsolate[depth])
                            depth--;

                        depth--;
                        validIsolate--;
                    }

                    levels[i] = stackLevel[depth];
                    if (stackOverride[depth] != BidiClass.ON)
                        classes[i] = stackOverride[depth];

                    break;
                }

                case BidiClass.PDF:
                {
                    levels[i] = stackLevel[depth];
                    if (overflowIsolate > 0)
                    {
                        // An isolate is still open; this PDF belongs to nothing.
                    }
                    else if (overflowEmbedding > 0)
                    {
                        overflowEmbedding--;
                    }
                    else if (!stackIsolate[depth] && depth > 0)
                    {
                        depth--;
                    }

                    break;
                }

                case BidiClass.B:
                {
                    // X8: a paragraph separator ends every embedding and isolate.
                    depth = 0;
                    overflowIsolate = overflowEmbedding = validIsolate = 0;
                    levels[i] = paragraphLevel;
                    break;
                }

                default:
                {
                    levels[i] = stackLevel[depth];
                    if (stackOverride[depth] != BidiClass.ON)
                        classes[i] = stackOverride[depth];

                    break;
                }
            }
        }
    }

    /// <summary>X10: splits the text into isolating run sequences and resolves each one.</summary>
    private static void ResolveSequences(
        int[] text,
        BidiClass[] original,
        BidiClass[] classes,
        byte[] explicitLevels,
        Span<byte> levels,
        ReadOnlySpan<int> kept,
        byte paragraphLevel,
        int[] matchingPdi,
        int[] matchingInitiator)
    {
        if (kept.Length == 0)
            return;

        // Level runs: maximal stretches of kept characters that share an embedding level.
        var runStart = new List<int>();
        var runEnd = new List<int>();
        for (var i = 0; i < kept.Length;)
        {
            var start = i;
            var level = explicitLevels[kept[i]];
            while (i < kept.Length && explicitLevels[kept[i]] == level)
                i++;

            runStart.Add(start);
            runEnd.Add(i);
        }

        // A run belongs to the sequence started by the isolate initiator it closes.
        var runOfCharacter = new Dictionary<int, int>(runStart.Count);
        for (var run = 0; run < runStart.Count; run++)
            runOfCharacter[kept[runStart[run]]] = run;

        var used = new bool[runStart.Count];
        var sequence = new List<int>();

        for (var run = 0; run < runStart.Count; run++)
        {
            if (used[run])
                continue;

            var first = kept[runStart[run]];
            if (original[first] == BidiClass.PDI && matchingInitiator[first] >= 0)
                continue; // this run continues an earlier sequence

            sequence.Clear();
            var current = run;
            while (true)
            {
                used[current] = true;
                for (var i = runStart[current]; i < runEnd[current]; i++)
                    sequence.Add(kept[i]);

                var last = kept[runEnd[current] - 1];
                if (!IsIsolateInitiator(original[last]) || matchingPdi[last] >= original.Length)
                    break;

                if (!runOfCharacter.TryGetValue(matchingPdi[last], out var next) || used[next])
                    break;

                current = next;
            }

            ResolveSequence(text, original, classes, explicitLevels, levels, sequence, kept, paragraphLevel);
        }
    }

    private static void ResolveSequence(
        int[] text,
        BidiClass[] original,
        BidiClass[] classes,
        byte[] explicitLevels,
        Span<byte> levels,
        List<int> sequence,
        ReadOnlySpan<int> kept,
        byte paragraphLevel)
    {
        var level = explicitLevels[sequence[0]];

        // X10: the boundaries take the direction of the higher of the two levels that meet there.
        var before = PreviousKept(kept, sequence[0]);
        var startLevel = Math.Max(level, before < 0 ? paragraphLevel : explicitLevels[before]);
        var sos = (startLevel & 1) == 1 ? BidiClass.R : BidiClass.L;

        var lastCharacter = sequence[^1];
        byte endLevel;
        if (IsIsolateInitiator(original[lastCharacter]))
        {
            endLevel = Math.Max(level, paragraphLevel);
        }
        else
        {
            var after = NextKept(kept, lastCharacter);
            endLevel = Math.Max(level, after < 0 ? paragraphLevel : explicitLevels[after]);
        }

        var eos = (endLevel & 1) == 1 ? BidiClass.R : BidiClass.L;

        Weak(classes, sequence, sos);
        Brackets(text, original, classes, sequence, level, sos);
        Neutral(classes, sequence, level, sos, eos);
        Implicit(classes, levels, sequence, level);
    }

    private static int PreviousKept(ReadOnlySpan<int> kept, int position)
    {
        var index = kept.BinarySearch(position);
        return index > 0 ? kept[index - 1] : -1;
    }

    private static int NextKept(ReadOnlySpan<int> kept, int position)
    {
        var index = kept.BinarySearch(position);
        return index >= 0 && index + 1 < kept.Length ? kept[index + 1] : -1;
    }

    /// <summary>W1 to W7.</summary>
    private static void Weak(BidiClass[] classes, List<int> sequence, BidiClass sos)
    {
        // W1: a combining mark takes the class of the character it follows.
        var previous = sos;
        foreach (var i in sequence)
        {
            if (classes[i] == BidiClass.NSM)
                classes[i] = IsIsolateInitiator(previous) || previous == BidiClass.PDI ? BidiClass.ON : previous;

            previous = classes[i];
        }

        // W2: a European number after an Arabic letter is an Arabic number.
        var strong = sos;
        foreach (var i in sequence)
        {
            switch (classes[i])
            {
                case BidiClass.L or BidiClass.R or BidiClass.AL:
                    strong = classes[i];
                    break;
                case BidiClass.EN when strong == BidiClass.AL:
                    classes[i] = BidiClass.AN;
                    break;
            }
        }

        // W3: Arabic letters are right to left from here on.
        foreach (var i in sequence)
        {
            if (classes[i] == BidiClass.AL)
                classes[i] = BidiClass.R;
        }

        // W4: a single separator between two numbers of the same kind joins them.
        for (var index = 1; index + 1 < sequence.Count; index++)
        {
            var current = classes[sequence[index]];
            if (current is not (BidiClass.ES or BidiClass.CS))
                continue;

            var left = classes[sequence[index - 1]];
            var right = classes[sequence[index + 1]];
            if (left == BidiClass.EN && right == BidiClass.EN)
                classes[sequence[index]] = BidiClass.EN;
            else if (current == BidiClass.CS && left == BidiClass.AN && right == BidiClass.AN)
                classes[sequence[index]] = BidiClass.AN;
        }

        // W5: a run of terminators next to a European number becomes part of it.
        for (var index = 0; index < sequence.Count; index++)
        {
            if (classes[sequence[index]] != BidiClass.ET)
                continue;

            var end = index;
            while (end < sequence.Count && classes[sequence[end]] == BidiClass.ET)
                end++;

            var adjacent = (index > 0 && classes[sequence[index - 1]] == BidiClass.EN) ||
                (end < sequence.Count && classes[sequence[end]] == BidiClass.EN);

            if (adjacent)
            {
                for (var i = index; i < end; i++)
                    classes[sequence[i]] = BidiClass.EN;
            }

            index = end - 1;
        }

        // W6: whatever is left of the separators and terminators is neutral.
        foreach (var i in sequence)
        {
            if (classes[i] is BidiClass.ET or BidiClass.ES or BidiClass.CS)
                classes[i] = BidiClass.ON;
        }

        // W7: a European number after a left to right character is left to right.
        strong = sos;
        foreach (var i in sequence)
        {
            switch (classes[i])
            {
                case BidiClass.L or BidiClass.R:
                    strong = classes[i];
                    break;
                case BidiClass.EN when strong == BidiClass.L:
                    classes[i] = BidiClass.L;
                    break;
            }
        }
    }

    /// <summary>N0: brackets take the direction of the text between them (BD16).</summary>
    private static void Brackets(int[] text, BidiClass[] original, BidiClass[] classes, List<int> sequence, byte level, BidiClass sos)
    {
        Span<int> openPosition = stackalloc int[MaxBracketPairs];
        Span<int> openCharacter = stackalloc int[MaxBracketPairs];
        var open = 0;

        var pairs = new List<(int Open, int Close)>();
        for (var index = 0; index < sequence.Count; index++)
        {
            var character = sequence[index];
            if (classes[character] != BidiClass.ON)
                continue;

            var bracket = BracketOf(text[character], out var opens, out var paired);
            if (bracket == 0)
                continue;

            if (opens)
            {
                if (open == MaxBracketPairs)
                    break; // BD16: too deep to pair

                openPosition[open] = index;
                openCharacter[open] = paired;
                open++;
            }
            else
            {
                for (var candidate = open - 1; candidate >= 0; candidate--)
                {
                    if (openCharacter[candidate] != bracket)
                        continue;

                    pairs.Add((openPosition[candidate], index));
                    open = candidate;
                    break;
                }
            }
        }

        pairs.Sort((left, right) => left.Open.CompareTo(right.Open));
        var embedding = (level & 1) == 1 ? BidiClass.R : BidiClass.L;
        var opposite = embedding == BidiClass.L ? BidiClass.R : BidiClass.L;

        foreach (var (openIndex, closeIndex) in pairs)
        {
            var found = BidiClass.ON;
            for (var index = openIndex + 1; index < closeIndex; index++)
            {
                var strong = StrongDirection(classes[sequence[index]]);
                if (strong == BidiClass.ON)
                    continue;

                if (strong == embedding)
                {
                    found = embedding;
                    break;
                }

                found = opposite;
            }

            if (found == BidiClass.ON)
                continue; // no strong character inside: N1 and N2 decide

            if (found == opposite)
            {
                // The context before the pair decides whether the opposite direction is kept.
                var context = sos;
                for (var index = openIndex - 1; index >= 0; index--)
                {
                    var strong = StrongDirection(classes[sequence[index]]);
                    if (strong != BidiClass.ON)
                    {
                        context = strong;
                        break;
                    }
                }

                found = context == opposite ? opposite : embedding;
            }

            Set(openIndex, found);
            Set(closeIndex, found);
        }

        void Set(int index, BidiClass value)
        {
            classes[sequence[index]] = value;

            // Combining marks on a bracket follow the direction it was given.
            for (var next = index + 1; next < sequence.Count; next++)
            {
                if (original[sequence[next]] != BidiClass.NSM)
                    break;

                classes[sequence[next]] = value;
            }
        }
    }

    private static BidiClass StrongDirection(BidiClass value) => value switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };

    /// <summary>
    /// The bracket a character is, with the bracket it pairs with. Returns zero when it is not a bracket.
    /// Canonically equivalent brackets are matched as one, as BD16 requires.
    /// </summary>
    private static int BracketOf(int codepoint, out bool opens, out int paired)
    {
        opens = false;
        paired = 0;

        var index = UnicodeTables.BracketChars.BinarySearch(codepoint);
        if (index < 0)
            return 0;

        opens = UnicodeTables.BracketOpens[index];
        paired = Canonical(UnicodeTables.BracketPairs[index]);
        return Canonical(codepoint);

        // U+3008 and U+3009 are canonically equivalent to U+2329 and U+232A.
        static int Canonical(int value) => value switch
        {
            0x3008 => 0x2329,
            0x3009 => 0x232A,
            _ => value,
        };
    }

    /// <summary>N1 and N2: neutrals between two characters of the same direction join them.</summary>
    private static void Neutral(BidiClass[] classes, List<int> sequence, byte level, BidiClass sos, BidiClass eos)
    {
        var embedding = (level & 1) == 1 ? BidiClass.R : BidiClass.L;

        for (var index = 0; index < sequence.Count; index++)
        {
            if (!IsNeutral(classes[sequence[index]]))
                continue;

            var end = index;
            while (end < sequence.Count && IsNeutral(classes[sequence[end]]))
                end++;

            var before = index == 0 ? sos : StrongDirection(classes[sequence[index - 1]]);
            var after = end == sequence.Count ? eos : StrongDirection(classes[sequence[end]]);
            var resolved = before == after && before != BidiClass.ON ? before : embedding;

            for (var i = index; i < end; i++)
                classes[sequence[i]] = resolved;

            index = end - 1;
        }
    }

    /// <summary>I1 and I2: the levels the resolved classes imply.</summary>
    private static void Implicit(BidiClass[] classes, Span<byte> levels, List<int> sequence, byte level)
    {
        foreach (var character in sequence)
        {
            var value = classes[character];
            if ((level & 1) == 0)
            {
                levels[character] = value switch
                {
                    BidiClass.R => (byte)(level + 1),
                    BidiClass.AN or BidiClass.EN => (byte)(level + 2),
                    _ => level,
                };
            }
            else
            {
                levels[character] = value switch
                {
                    BidiClass.L or BidiClass.AN or BidiClass.EN => (byte)(level + 1),
                    _ => level,
                };
            }
        }
    }

    /// <summary>
    /// L1 and L2: resets the levels of trailing whitespace to the paragraph level and writes the order the
    /// characters of the line are drawn in into <paramref name="visual"/>. Returns how many characters are
    /// drawn: the explicit formatting characters are left out, as rule X9 removed them from the algorithm.
    /// </summary>
    public static int Reorder(ReadOnlySpan<int> text, Span<byte> levels, byte paragraphLevel, Span<int> visual)
    {
        var length = text.Length;

        // L1: separators, and whitespace that runs to the end of the line, return to the paragraph direction.
        var whitespaceFrom = length;
        for (var i = 0; i < length; i++)
        {
            var value = ClassOf(text[i]);
            switch (value)
            {
                case BidiClass.B or BidiClass.S:
                    levels[i] = paragraphLevel;
                    for (var j = whitespaceFrom; j < i; j++)
                        levels[j] = paragraphLevel;

                    whitespaceFrom = length;
                    break;

                case BidiClass.WS or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI:
                    if (whitespaceFrom == length)
                        whitespaceFrom = i;

                    break;

                default:
                    // Characters removed by X9 do not interrupt a run of whitespace.
                    if (!IsRemoved(value))
                        whitespaceFrom = length;

                    break;
            }
        }

        for (var i = whitespaceFrom; i < length; i++)
            levels[i] = paragraphLevel;

        var count = 0;
        for (var i = 0; i < length; i++)
        {
            if (!IsRemoved(ClassOf(text[i])))
                visual[count++] = i;
        }

        ReorderByLevel(levels, visual[..count]);
        return count;
    }

    /// <summary>
    /// Rule L2 on its own, for a line whose levels are already resolved: <paramref name="visual"/> holds the
    /// positions to draw and receives them in the order they are drawn, left to right.
    /// </summary>
    public static void ReorderByLevel(ReadOnlySpan<byte> levels, Span<int> visual)
    {
        byte highest = 0;
        var lowestOdd = (byte)(MaxDepth + 1);
        foreach (var position in visual)
        {
            highest = Math.Max(highest, levels[position]);
            if ((levels[position] & 1) == 1)
                lowestOdd = Math.Min(lowestOdd, levels[position]);
        }

        // From the deepest level up to the lowest odd one, every stretch at or above that level is reversed.
        for (var level = highest; level >= lowestOdd; level--)
        {
            for (var i = 0; i < visual.Length; i++)
            {
                if (levels[visual[i]] < level)
                    continue;

                var end = i;
                while (end < visual.Length && levels[visual[end]] >= level)
                    end++;

                visual[i..end].Reverse();
                i = end;
            }
        }
    }
}
