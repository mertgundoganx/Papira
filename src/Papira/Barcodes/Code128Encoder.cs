namespace Papira.Barcodes;

/// <summary>
/// Code 128 barcode encoder. Digits are packed two per symbol (code set C) where that is shorter,
/// the rest is encoded in code set B.
/// </summary>
internal static class Code128Encoder
{
    // Bar and space widths of the 107 symbols; each symbol is 11 modules, the stop symbol 13.
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int StartB = 104;
    private const int StartC = 105;
    private const int SwitchB = 100;
    private const int SwitchC = 99;
    private const int Stop = 106;

    /// <summary>Bar widths in modules; the first entry is a bar, then they alternate with spaces.</summary>
    public static int[] Encode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
                throw new ArgumentException("Code 128 supports printable ASCII characters only.", nameof(value));
        }

        var symbols = EncodeSymbols(value);

        var checksum = symbols[0];
        for (var i = 1; i < symbols.Count; i++)
            checksum += symbols[i] * i;
        symbols.Add(checksum % 103);
        symbols.Add(Stop);

        var widths = new List<int>(symbols.Count * 6 + 1);
        foreach (var symbol in symbols)
        {
            foreach (var width in Patterns[symbol])
                widths.Add(width - '0');
        }

        return [.. widths];
    }

    private static List<int> EncodeSymbols(string value)
    {
        var symbols = new List<int>();
        var position = 0;
        var inCodeC = DigitRun(value, 0) >= (value.Length == 2 ? 2 : 4);

        symbols.Add(inCodeC ? StartC : StartB);

        while (position < value.Length)
        {
            if (inCodeC)
            {
                if (position + 1 < value.Length && char.IsAsciiDigit(value[position]) && char.IsAsciiDigit(value[position + 1]))
                {
                    symbols.Add((value[position] - '0') * 10 + (value[position + 1] - '0'));
                    position += 2;
                    continue;
                }

                symbols.Add(SwitchB);
                inCodeC = false;
                continue;
            }

            // Switching to code set C pays off from four digits (six at the very end of the value).
            var run = DigitRun(value, position);
            if (run >= 4 && run % 2 == 0)
            {
                symbols.Add(SwitchC);
                inCodeC = true;
                continue;
            }

            symbols.Add(value[position] - ' ');
            position++;
        }

        return symbols;
    }

    private static int DigitRun(string value, int start)
    {
        var count = 0;
        while (start + count < value.Length && char.IsAsciiDigit(value[start + count]))
            count++;
        return count;
    }
}
