using System.Text;

namespace Papira.Barcodes;

/// <summary>
/// QR code encoder (ISO/IEC 18004): picks the smallest version and the best mask, encodes the data in
/// numeric, alphanumeric or byte mode and adds Reed–Solomon error correction.
/// </summary>
internal sealed class QrEncoder
{
    private const string AlphanumericCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // Error correction codewords per block and number of blocks, per version 1..40. Index 0 is unused.
    // Order of the outer index: Low, Medium, Quartile, High (ISO/IEC 18004 tables 13–22).
    private static readonly byte[][] EccCodewordsPerBlock =
    [
        [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [0, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [0, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly byte[][] BlockCount =
    [
        [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    private readonly bool[,] _modules;
    private readonly bool[,] _reserved;

    private QrEncoder(int size)
    {
        Size = size;
        _modules = new bool[size, size];
        _reserved = new bool[size, size];
    }

    /// <summary>Number of modules per side, without the quiet zone.</summary>
    public int Size { get; }

    /// <summary>True where the module is dark.</summary>
    public bool this[int x, int y] => _modules[x, y];

    public static QrEncoder Encode(string text, QrErrorCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(text);
        var level = (int)correction;
        var mode = ChooseMode(text);
        var data = mode == Mode.Byte ? Encoding.UTF8.GetBytes(text) : [];

        var version = ChooseVersion(text, data, mode, level);
        var codewords = BuildCodewords(text, data, mode, version, level);

        var qr = new QrEncoder(version * 4 + 17);
        qr.DrawFunctionPatterns(version, level);
        qr.DrawCodewords(codewords);
        qr.ApplyBestMask(level);
        return qr;
    }

    /// <summary>The final codeword stream (data and error correction, interleaved). Used by tests.</summary>
    internal static byte[] EncodeCodewords(string text, QrErrorCorrection correction)
    {
        var level = (int)correction;
        var mode = ChooseMode(text);
        var data = mode == Mode.Byte ? Encoding.UTF8.GetBytes(text) : [];
        return BuildCodewords(text, data, mode, ChooseVersion(text, data, mode, level), level);
    }

    private enum Mode { Numeric, Alphanumeric, Byte }

    private static Mode ChooseMode(string text)
    {
        if (text.All(char.IsAsciiDigit))
            return Mode.Numeric;
        if (text.All(c => AlphanumericCharset.Contains(c, StringComparison.Ordinal)))
            return Mode.Alphanumeric;
        return Mode.Byte;
    }

    private static int CharacterCountBits(Mode mode, int version) => mode switch
    {
        Mode.Numeric => version < 10 ? 10 : version < 27 ? 12 : 14,
        Mode.Alphanumeric => version < 10 ? 9 : version < 27 ? 11 : 13,
        _ => version < 10 ? 8 : 16,
    };

    private static int DataBits(string text, byte[] data, Mode mode, int version) => mode switch
    {
        Mode.Numeric => 10 * (text.Length / 3) + (text.Length % 3 == 2 ? 7 : text.Length % 3 == 1 ? 4 : 0),
        Mode.Alphanumeric => 11 * (text.Length / 2) + (text.Length % 2) * 6,
        _ => data.Length * 8,
    } + 4 + CharacterCountBits(mode, version);

    private static int ChooseVersion(string text, byte[] data, Mode mode, int level)
    {
        for (var version = 1; version <= 40; version++)
        {
            if (DataBits(text, data, mode, version) <= DataCodewords(version, level) * 8)
                return version;
        }

        throw new ArgumentException("The text is too long for a QR code.", nameof(text));
    }

    /// <summary>Data modules of a version, before error correction (ISO/IEC 18004 §7.3).</summary>
    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var alignment = version / 7 + 2;
            result -= (25 * alignment - 10) * alignment - 55;
            if (version >= 7)
                result -= 36;
        }

        return result;
    }

    private static int DataCodewords(int version, int level) =>
        RawDataModules(version) / 8 - EccCodewordsPerBlock[level][version] * BlockCount[level][version];

    // ---- Bit stream and error correction ---------------------------------------------------------

    private static byte[] BuildCodewords(string text, byte[] data, Mode mode, int version, int level)
    {
        var bits = new BitWriter();
        bits.Write(mode switch { Mode.Numeric => 1, Mode.Alphanumeric => 2, _ => 4 }, 4);
        bits.Write(mode == Mode.Byte ? data.Length : text.Length, CharacterCountBits(mode, version));

        switch (mode)
        {
            case Mode.Numeric:
                for (var i = 0; i < text.Length; i += 3)
                {
                    var chunk = text.Substring(i, Math.Min(3, text.Length - i));
                    bits.Write(int.Parse(chunk, System.Globalization.CultureInfo.InvariantCulture), chunk.Length * 3 + 1);
                }

                break;

            case Mode.Alphanumeric:
                for (var i = 0; i < text.Length; i += 2)
                {
                    var first = AlphanumericCharset.IndexOf(text[i], StringComparison.Ordinal);
                    if (i + 1 < text.Length)
                        bits.Write(first * 45 + AlphanumericCharset.IndexOf(text[i + 1], StringComparison.Ordinal), 11);
                    else
                        bits.Write(first, 6);
                }

                break;

            default:
                foreach (var b in data)
                    bits.Write(b, 8);
                break;
        }

        var capacity = DataCodewords(version, level) * 8;
        bits.Write(0, Math.Min(4, capacity - bits.Length));   // terminator
        bits.Write(0, (8 - bits.Length % 8) % 8);             // pad to a whole byte

        var padding = new byte[] { 0xEC, 0x11 };
        for (var i = 0; bits.Length < capacity; i++)
            bits.Write(padding[i % 2], 8);

        return Interleave(bits.ToArray(), version, level);
    }

    /// <summary>Splits the data into blocks, adds Reed–Solomon codewords and interleaves them.</summary>
    private static byte[] Interleave(byte[] data, int version, int level)
    {
        var blocks = BlockCount[level][version];
        var eccPerBlock = EccCodewordsPerBlock[level][version];
        var totalData = data.Length;
        var shortBlocks = blocks - totalData % blocks;
        var shortLength = totalData / blocks;

        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];
        var generator = ReedSolomon.Generator(eccPerBlock);

        for (int i = 0, offset = 0; i < blocks; i++)
        {
            var length = shortLength + (i < shortBlocks ? 0 : 1);
            dataBlocks[i] = data[offset..(offset + length)];
            eccBlocks[i] = ReedSolomon.Remainder(dataBlocks[i], generator);
            offset += length;
        }

        var result = new List<byte>(totalData + eccPerBlock * blocks);
        for (var i = 0; i <= shortLength; i++)
        {
            for (var b = 0; b < blocks; b++)
            {
                if (i < dataBlocks[b].Length)
                    result.Add(dataBlocks[b][i]);
            }
        }

        for (var i = 0; i < eccPerBlock; i++)
        {
            for (var b = 0; b < blocks; b++)
                result.Add(eccBlocks[b][i]);
        }

        return [.. result];
    }

    // ---- Module placement ------------------------------------------------------------------------

    private void Set(int x, int y, bool dark, bool reserved = false)
    {
        _modules[x, y] = dark;
        _reserved[x, y] = reserved;
    }

    private void DrawFunctionPatterns(int version, int level)
    {
        // Timing patterns.
        for (var i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0, true);
            Set(i, 6, i % 2 == 0, true);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var alignment = AlignmentPositions(version);
        for (var i = 0; i < alignment.Length; i++)
        {
            for (var j = 0; j < alignment.Length; j++)
            {
                // The three finder corners have no alignment pattern.
                var corner = (i == 0 && j == 0) || (i == 0 && j == alignment.Length - 1) || (i == alignment.Length - 1 && j == 0);
                if (!corner)
                    DrawAlignment(alignment[i], alignment[j]);
            }
        }

        DrawFormatBits(level, 0);
        DrawVersionBits(version);
    }

    private void DrawFinder(int centerX, int centerY)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                int x = centerX + dx, y = centerY + dy;
                if (x < 0 || x >= Size || y < 0 || y >= Size)
                    continue;

                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(x, y, distance != 2 && distance <= 3, true);
            }
        }
    }

    private void DrawAlignment(int centerX, int centerY)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
                Set(centerX + dx, centerY + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1, true);
        }
    }

    private static int[] AlignmentPositions(int version)
    {
        if (version == 1)
            return [];

        var count = version / 7 + 2;
        var step = version == 32 ? 26 : (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;

        var positions = new int[count];
        positions[0] = 6;
        for (int i = count - 1, pos = Size4(version) - 7; i >= 1; i--, pos -= step)
            positions[i] = pos;

        return positions;

        static int Size4(int version) => version * 4 + 17;
    }

    private void DrawFormatBits(int level, int mask)
    {
        // 5 data bits (error correction level and mask) with a BCH(15, 5) remainder, then masked.
        var levelBits = level switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };
        var data = levelBits << 3 | mask;
        var remainder = data;
        for (var i = 0; i < 10; i++)
            remainder = remainder << 1 ^ (remainder >> 9) * 0x537;

        var bits = (data << 10 | remainder) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
            Set(8, i, Bit(bits, i), true);
        Set(8, 7, Bit(bits, 6), true);
        Set(8, 8, Bit(bits, 7), true);
        Set(7, 8, Bit(bits, 8), true);
        for (var i = 9; i < 15; i++)
            Set(14 - i, 8, Bit(bits, i), true);

        for (var i = 0; i < 8; i++)
            Set(Size - 1 - i, 8, Bit(bits, i), true);
        for (var i = 8; i < 15; i++)
            Set(8, Size - 15 + i, Bit(bits, i), true);

        Set(8, Size - 8, true, true); // always dark
    }

    private void DrawVersionBits(int version)
    {
        if (version < 7)
            return;

        var remainder = version;
        for (var i = 0; i < 12; i++)
            remainder = remainder << 1 ^ (remainder >> 11) * 0x1F25;

        var bits = version << 12 | remainder;
        for (var i = 0; i < 18; i++)
        {
            var dark = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            Set(a, b, dark, true);
            Set(b, a, dark, true);
        }
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    /// <summary>Places the codewords in the zigzag pattern, skipping function modules.</summary>
    private void DrawCodewords(byte[] codewords)
    {
        var bit = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5; // the vertical timing pattern column is skipped

            for (var step = 0; step < Size; step++)
            {
                var upward = ((right + 1) & 2) == 0;
                var y = upward ? Size - 1 - step : step;

                for (var i = 0; i < 2; i++)
                {
                    var x = right - i;
                    if (_reserved[x, y])
                        continue;

                    var dark = bit < codewords.Length * 8 && Bit(codewords[bit >> 3], 7 - (bit & 7));
                    _modules[x, y] = dark;
                    bit++;
                }
            }
        }
    }

    private void ApplyBestMask(int level)
    {
        var best = 0;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(level, mask);
            var penalty = Penalty();
            ApplyMask(mask); // masking twice restores the original

            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = mask;
            }
        }

        ApplyMask(best);
        DrawFormatBits(level, best);
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (_reserved[x, y])
                    continue;

                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };

                _modules[x, y] ^= invert;
            }
        }
    }

    /// <summary>Penalty score of the current pattern (ISO/IEC 18004 §7.8.3); the lowest scoring mask wins.</summary>
    private int Penalty()
    {
        var penalty = 0;

        // Rule 1: runs of five or more modules of the same colour.
        for (var i = 0; i < Size; i++)
        {
            penalty += RunPenalty(i, horizontal: true) + RunPenalty(i, horizontal: false);
        }

        // Rule 2: 2x2 blocks of the same colour.
        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var value = _modules[x, y];
                if (value == _modules[x + 1, y] && value == _modules[x, y + 1] && value == _modules[x + 1, y + 1])
                    penalty += 3;
            }
        }

        // Rule 3: the 1:1:3:1:1 pattern with four light modules on one side,
        // i.e. 10111010000 or 00001011101 in a row or column.
        for (var line = 0; line < Size; line++)
        {
            for (var start = 0; start + 11 <= Size; start++)
            {
                if (MatchesPattern(line, start, horizontal: true))
                    penalty += 40;
                if (MatchesPattern(line, start, horizontal: false))
                    penalty += 40;
            }
        }

        // Rule 4: deviation from an even distribution of dark modules.
        var dark = 0;
        foreach (var module in _modules)
        {
            if (module)
                dark++;
        }

        var percent = dark * 100 / (Size * Size);
        penalty += Math.Abs(percent - 50) / 5 * 10;
        return penalty;
    }

    private int RunPenalty(int line, bool horizontal)
    {
        int penalty = 0, run = 1;
        for (var i = 1; i < Size; i++)
        {
            var current = horizontal ? _modules[i, line] : _modules[line, i];
            var previous = horizontal ? _modules[i - 1, line] : _modules[line, i - 1];

            if (current == previous)
            {
                run++;
                if (run == 5)
                    penalty += 3;
                else if (run > 5)
                    penalty++;
            }
            else
            {
                run = 1;
            }
        }

        return penalty;
    }

    private static ReadOnlySpan<bool> Pattern => [true, false, true, true, true, false, true, false, false, false, false];

    private bool MatchesPattern(int line, int start, bool horizontal)
    {
        // The pattern may appear in either direction.
        bool forward = true, backward = true;
        for (var i = 0; i < 11 && (forward || backward); i++)
        {
            var module = horizontal ? _modules[start + i, line] : _modules[line, start + i];
            forward &= module == Pattern[i];
            backward &= module == Pattern[10 - i];
        }

        return forward || backward;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _freeBitsInLast;

        public int Length => _bytes.Count * 8 - _freeBitsInLast;

        public void Write(int value, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                if (_freeBitsInLast == 0)
                {
                    _bytes.Add(0);
                    _freeBitsInLast = 8;
                }

                if (((value >> i) & 1) != 0)
                    _bytes[^1] |= (byte)(1 << (_freeBitsInLast - 1));
                _freeBitsInLast--;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
