namespace Papira.Fonts;

/// <summary>
/// Walks Type 2 charstrings to find the subroutines they call, and rewrites the calls once the
/// subroutines have been renumbered. A charstring calls a subroutine by an index counted from a bias
/// that depends on how many subroutines there are, so dropping the unused ones means rewriting every call.
/// </summary>
/// <remarks>
/// Anything unexpected — an unknown operator, a call that leads nowhere, nesting deeper than the format
/// allows — makes the walk give up, and the caller then keeps every subroutine rather than risk a font
/// that cannot be drawn.
/// </remarks>
internal static class CffCharstrings
{
    private const int MaxDepth = 10;

    public static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <summary>
    /// What the walk found: which subroutines are reached, how many stem hints each one declares, and how
    /// many were already declared when it was first called. A mask of stem hints is as long as the number
    /// of hints so far, which is why both numbers are needed to rewrite a subroutine on its own.
    /// </summary>
    public sealed class Usage(int localCount, int globalCount)
    {
        public bool[] LocalUsed { get; } = new bool[localCount];

        public bool[] GlobalUsed { get; } = new bool[globalCount];

        public int[] LocalStems { get; } = new int[localCount];

        public int[] GlobalStems { get; } = new int[globalCount];

        public int[] LocalEntryStems { get; } = new int[localCount];

        public int[] GlobalEntryStems { get; } = new int[globalCount];

        /// <summary>How many values a subroutine leaves on the stack, or takes from it.</summary>
        public int[] LocalStack { get; } = new int[localCount];

        public int[] GlobalStack { get; } = new int[globalCount];
    }

    /// <summary>
    /// Marks the subroutines the given charstrings reach, directly or through other subroutines.
    /// Returns false when a charstring could not be followed.
    /// </summary>
    public static bool TryMark(
        IEnumerable<ReadOnlyMemory<byte>> charstrings,
        IReadOnlyList<ReadOnlyMemory<byte>> localSubrs,
        IReadOnlyList<ReadOnlyMemory<byte>> globalSubrs,
        Usage usage)
    {
        var walker = new Walker(localSubrs, globalSubrs, usage);
        foreach (var charstring in charstrings)
        {
            var stems = 0;
            if (!walker.Walk(charstring.Span, 0, ref stems))
                return false;
        }

        return true;
    }

    /// <summary>Rewrites the calls of a charstring to the numbers the subroutines have in the subset.</summary>
    public static byte[] Rewrite(
        ReadOnlySpan<byte> charstring,
        int[] localMap,
        int[] globalMap,
        int oldLocalBias,
        int oldGlobalBias,
        int newLocalBias,
        int newGlobalBias,
        Usage usage,
        int initialStems)
    {
        var output = new List<byte>(charstring.Length);
        var lastNumberStart = -1;
        var lastNumber = 0;
        var stems = initialStems;
        var stack = 0;

        for (var i = 0; i < charstring.Length;)
        {
            var b = charstring[i];
            if (b >= 32 || b == 28)
            {
                var (value, size) = Number(charstring, i);
                lastNumberStart = output.Count;
                lastNumber = value;
                for (var k = 0; k < size; k++)
                    output.Add(charstring[i + k]);

                i += size;
                stack++;
                continue;
            }

            switch (b)
            {
                case 10 or 29:
                {
                    var global = b == 29;
                    var index = lastNumber + (global ? oldGlobalBias : oldLocalBias);
                    var map = global ? globalMap : localMap;
                    var replacement = map[index] - (global ? newGlobalBias : newLocalBias);

                    // The index sits on the stack as the number just before the call, so it is rewritten there.
                    output.RemoveRange(lastNumberStart, output.Count - lastNumberStart);
                    WriteNumber(output, replacement);
                    output.Add(b);
                    i++;

                    // What the subroutine declares and leaves behind counts towards what follows it.
                    stems += global ? usage.GlobalStems[index] : usage.LocalStems[index];
                    stack += (global ? usage.GlobalStack[index] : usage.LocalStack[index]) - 1;
                    lastNumberStart = -1;
                    break;
                }

                case 1 or 3 or 18 or 23:
                    stems += stack / 2;
                    stack = 0;
                    output.Add(b);
                    i++;
                    break;

                case 19 or 20:
                {
                    stems += stack / 2;
                    stack = 0;
                    var mask = 1 + (Math.Max(stems, 1) + 7) / 8;
                    for (var k = 0; k < mask && i + k < charstring.Length; k++)
                        output.Add(charstring[i + k]);

                    i += mask;
                    break;
                }

                case 12:
                    output.Add(b);
                    if (i + 1 < charstring.Length)
                        output.Add(charstring[i + 1]);

                    i += 2;
                    stack = 0;
                    break;

                default:
                    output.Add(b);
                    i++;
                    stack = 0;
                    break;
            }
        }

        return [.. output];
    }

    private static (int Value, int Size) Number(ReadOnlySpan<byte> data, int index)
    {
        var b = data[index];
        return b switch
        {
            28 => ((short)((data[index + 1] << 8) | data[index + 2]), 3),
            < 247 => (b - 139, 1),
            < 251 => ((b - 247) * 256 + data[index + 1] + 108, 2),
            < 255 => (-((b - 251) * 256) - data[index + 1] - 108, 2),

            // A 16.16 fixed point number; only its integer part can be a subroutine index.
            _ => ((data[index + 1] << 8) | data[index + 2], 5),
        };
    }

    private static void WriteNumber(List<byte> output, int value)
    {
        switch (value)
        {
            case >= -107 and <= 107:
                output.Add((byte)(value + 139));
                break;
            case >= 108 and <= 1131:
                output.Add((byte)(((value - 108) >> 8) + 247));
                output.Add((byte)((value - 108) & 0xFF));
                break;
            case >= -1131 and <= -108:
                output.Add((byte)(((-value - 108) >> 8) + 251));
                output.Add((byte)((-value - 108) & 0xFF));
                break;
            default:
                output.Add(28);
                output.Add((byte)(value >> 8));
                output.Add((byte)value);
                break;
        }
    }

    private sealed class Walker(
        IReadOnlyList<ReadOnlyMemory<byte>> localSubrs,
        IReadOnlyList<ReadOnlyMemory<byte>> globalSubrs,
        Usage usage)
    {
        private readonly int _localBias = Bias(localSubrs.Count);
        private readonly int _globalBias = Bias(globalSubrs.Count);

        public bool Walk(ReadOnlySpan<byte> charstring, int depth, ref int stems)
        {
            var stack = 0;
            return Walk(charstring, depth, ref stems, ref stack);
        }

        /// <summary>
        /// Follows a charstring the way a font interpreter would, keeping count of the values on the stack
        /// and of the stem hints declared so far, because a mask of hints is as long as their number.
        /// </summary>
        private bool Walk(ReadOnlySpan<byte> charstring, int depth, ref int stems, ref int stack)
        {
            if (depth > MaxDepth)
                return false;

            var lastNumber = 0;
            var lastWasNumber = false;

            for (var i = 0; i < charstring.Length;)
            {
                var b = charstring[i];
                if (b >= 32 || b == 28)
                {
                    var (value, size) = Number(charstring, i);
                    lastNumber = value;
                    lastWasNumber = true;
                    i += size;
                    stack++;
                    if (stack > 96)
                        return false;

                    continue;
                }

                var previousWasNumber = lastWasNumber;
                lastWasNumber = false;

                switch (b)
                {
                    case 10 or 29:
                    {
                        // The number the call consumes has to be the one written just before it. A few fonts
                        // leave it on the stack from somewhere else, and those keep all their subroutines.
                        if (!previousWasNumber || stack == 0)
                            return false;

                        var global = b == 29;
                        var subrs = global ? globalSubrs : localSubrs;
                        var used = global ? usage.GlobalUsed : usage.LocalUsed;
                        var index = lastNumber + (global ? _globalBias : _localBias);
                        if ((uint)index >= (uint)subrs.Count)
                            return false;

                        stack--;
                        var beforeStems = stems;
                        var beforeStack = stack;
                        if (!used[index])
                        {
                            used[index] = true;
                            (global ? usage.GlobalEntryStems : usage.LocalEntryStems)[index] = stems;
                        }

                        if (!Walk(subrs[index].Span, depth + 1, ref stems, ref stack))
                            return false;

                        // What the subroutine declares counts at every call site, so it is recorded once
                        // and has to come out the same way each time.
                        var stemsAdded = stems - beforeStems;
                        var recorded = (global ? usage.GlobalStems : usage.LocalStems)[index];
                        if (recorded != 0 && recorded != stemsAdded)
                            return false;

                        (global ? usage.GlobalStems : usage.LocalStems)[index] = stemsAdded;
                        (global ? usage.GlobalStack : usage.LocalStack)[index] = stack - beforeStack;
                        i++;
                        break;
                    }

                    case 1 or 3 or 18 or 23:
                        stems += stack / 2;
                        stack = 0;
                        i++;
                        break;

                    case 19 or 20:
                        stems += stack / 2;
                        stack = 0;
                        i += 1 + (Math.Max(stems, 1) + 7) / 8;
                        break;

                    case 11:
                        return true;

                    case 14:
                        stack = 0;
                        return true;

                    case 12:
                        i += 2;
                        stack = 0;
                        break;

                    case 0 or 2 or 9 or 13 or 15 or 16 or 17:
                        return false; // reserved: this is not a charstring Papira can follow

                    default:
                        i++;
                        stack = 0;
                        break;
                }
            }

            return true;
        }
    }
}
