using System.Globalization;
using Papira.Infrastructure;

namespace Papira.Svg;

/// <summary>
/// Reads the numbers of an SVG attribute. They may be separated by whitespace, by commas, or by nothing
/// at all when the sign or the decimal point already marks the boundary ("10-5", "1.5.5").
/// </summary>
internal struct SvgScanner(string text)
{
    private readonly string _text = text;
    private int _index;

    public readonly bool AtEnd => _index >= _text.Length;

    public void SkipSeparators()
    {
        while (_index < _text.Length && (char.IsWhiteSpace(_text[_index]) || _text[_index] == ','))
            _index++;
    }

    /// <summary>Returns the next character when it is a command letter, without consuming it.</summary>
    public char? PeekLetter()
    {
        SkipSeparators();
        return !AtEnd && char.IsAsciiLetter(_text[_index]) ? _text[_index] : null;
    }

    public char ReadLetter()
    {
        SkipSeparators();
        return _text[_index++];
    }

    public bool TryReadNumber(out float value)
    {
        SkipSeparators();
        var start = _index;

        if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-'))
            _index++;

        var digits = ReadDigits();
        if (_index < _text.Length && _text[_index] == '.')
        {
            _index++;
            digits |= ReadDigits();
        }

        if (digits && _index < _text.Length && (_text[_index] == 'e' || _text[_index] == 'E'))
        {
            var beforeExponent = _index;
            _index++;
            if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-'))
                _index++;

            if (!ReadDigits())
                _index = beforeExponent;
        }

        if (!digits)
        {
            _index = start;
            value = 0;
            return false;
        }

        // A number can still overflow a float ("1e400"); such a coordinate is not drawable, so treat it as zero.
        value = float.TryParse(_text.AsSpan(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && float.IsFinite(parsed) ? parsed : 0;
        return true;
    }

    public float ReadNumber() => TryReadNumber(out var value) ? value : 0;

    /// <summary>Arc flags are a single "0" or "1" and need no separator.</summary>
    public bool ReadFlag()
    {
        SkipSeparators();
        if (_index < _text.Length && (_text[_index] == '0' || _text[_index] == '1'))
            return _text[_index++] == '1';

        return TryReadNumber(out var value) && value != 0;
    }

    private bool ReadDigits()
    {
        var start = _index;
        while (_index < _text.Length && char.IsAsciiDigit(_text[_index]))
            _index++;

        return _index > start;
    }
}

/// <summary>Attribute values of the SVG subset Papira draws: lengths, colors and transform lists.</summary>
internal static class SvgValues
{
    /// <summary>
    /// A length in user units. Absolute units are converted to the 96 dpi user space of SVG, percentages
    /// are taken from <paramref name="percentBasis"/>, and font-relative units use <paramref name="fontSize"/>.
    /// </summary>
    public static float Length(string? text, float percentBasis, float fallback = 0, float fontSize = 16)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var scanner = new SvgScanner(text);
        if (!scanner.TryReadNumber(out var value))
            return fallback;

        return Unit(text) switch
        {
            "%" => value / 100 * percentBasis,
            "pt" => value * 96 / 72,
            "pc" => value * 16,
            "in" => value * 96,
            "cm" => value * 96 / 2.54f,
            "mm" => value * 96 / 25.4f,
            "q" => value * 96 / 101.6f,
            "em" => value * fontSize,
            "ex" => value * fontSize / 2,
            _ => value,
        };
    }

    private static string Unit(string text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;

        var start = end;
        while (start > 0 && (char.IsAsciiLetter(text[start - 1]) || text[start - 1] == '%'))
            start--;

        return text[start..end].ToLowerInvariant();
    }

    public static float Number(string? text, float fallback = 0)
    {
        if (text == null)
            return fallback;

        var scanner = new SvgScanner(text);
        return scanner.TryReadNumber(out var value) ? value : fallback;
    }

    /// <summary>An opacity: a number or a percentage, clamped to 0–1.</summary>
    public static float Opacity(string? text, float fallback = 1)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var value = Number(text, fallback);
        if (text.Contains('%'))
            value /= 100;

        return Math.Clamp(value, 0, 1);
    }

    public static bool TryColor(string text, out Color color) => TryColor(text, out color, out _);

    /// <summary>
    /// The colour a value names, and how much of it shows through. In PDF transparency is a graphics
    /// state rather than part of a colour, so the two are read apart.
    /// </summary>
    public static bool TryColor(string text, out Color color, out float alpha)
    {
        color = default;
        alpha = 1;
        text = text.Trim();
        if (text.Length == 0)
            return false;

        if (text[0] == '#')
            return TryHex(text.AsSpan(1), out color, out alpha);

        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("rgb", StringComparison.Ordinal) || lower.StartsWith("hsl", StringComparison.Ordinal))
            return TryFunction(lower, out color, out alpha);

        if (lower == "transparent")
        {
            alpha = 0;
            return true;
        }

        if (Named(lower) is not { } named)
            return false;

        color = named;
        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> hex, out Color color, out float alpha)
    {
        color = default;
        alpha = 1;
        foreach (var c in hex)
        {
            if (Digit(c) < 0)
                return false;
        }

        switch (hex.Length)
        {
            case 3 or 4:
                color = new Color((byte)(Digit(hex[0]) * 17), (byte)(Digit(hex[1]) * 17), (byte)(Digit(hex[2]) * 17));
                if (hex.Length == 4)
                    alpha = Digit(hex[3]) * 17 / 255f;

                return true;
            case 6 or 8:
                color = new Color(
                    (byte)(Digit(hex[0]) * 16 + Digit(hex[1])),
                    (byte)(Digit(hex[2]) * 16 + Digit(hex[3])),
                    (byte)(Digit(hex[4]) * 16 + Digit(hex[5])));
                if (hex.Length == 8)
                    alpha = ((Digit(hex[6]) * 16) + Digit(hex[7])) / 255f;

                return true;
            default:
                return false;
        }

        static int Digit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    private static bool TryFunction(string text, out Color color, out float alpha)
    {
        color = default;
        alpha = 1;
        var open = text.IndexOf('(');
        if (open < 0)
            return false;

        // The percent signs only mark the unit; the scanner reads the numbers between them.
        var percent = text.Contains('%');
        var scanner = new SvgScanner(text[(open + 1)..].Replace('%', ' '));
        if (!scanner.TryReadNumber(out var first) || !scanner.TryReadNumber(out var second) || !scanner.TryReadNumber(out var third))
            return false;

        if (scanner.TryReadNumber(out var fourth))
            alpha = Math.Clamp(fourth > 1 && text.Contains('%') ? fourth / 100 : fourth, 0, 1);

        if (text.StartsWith("hsl", StringComparison.Ordinal))
        {
            color = FromHsl(first, second / 100, third / 100);
            return true;
        }

        var scale = percent ? 255 / 100f : 1;
        color = new Color(Channel(first * scale), Channel(second * scale), Channel(third * scale));
        return true;

        static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);
    }

    private static Color FromHsl(float hue, float saturation, float lightness)
    {
        hue = (hue % 360 + 360) % 360 / 60;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);

        var chroma = (1 - MathF.Abs(2 * lightness - 1)) * saturation;
        var second = chroma * (1 - MathF.Abs(hue % 2 - 1));
        var (r, g, b) = (int)hue switch
        {
            0 => (chroma, second, 0f),
            1 => (second, chroma, 0f),
            2 => (0f, chroma, second),
            3 => (0f, second, chroma),
            4 => (second, 0f, chroma),
            _ => (chroma, 0f, second),
        };

        var match = lightness - chroma / 2;
        return new Color(
            (byte)MathF.Round((r + match) * 255),
            (byte)MathF.Round((g + match) * 255),
            (byte)MathF.Round((b + match) * 255));
    }

    /// <summary>A transform list: matrix, translate, scale, rotate, skewX and skewY, applied left to right.</summary>
    public static Matrix Transform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Matrix.Identity;

        var result = Matrix.Identity;
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('(', index);
            if (open < 0)
                break;

            var close = text.IndexOf(')', open);
            if (close < 0)
                break;

            var name = text[index..open].Trim().Trim(',').Trim().ToLowerInvariant();
            var scanner = new SvgScanner(text[(open + 1)..close]);
            var numbers = new List<float>(6);
            while (scanner.TryReadNumber(out var number))
                numbers.Add(number);

            // The list reads left to right, and each transform applies inside the ones before it.
            result = Matrix.Multiply(Build(name, numbers), result);
            index = close + 1;
        }

        return result;
    }

    private static Matrix Build(string name, List<float> numbers)
    {
        float At(int index) => index < numbers.Count ? numbers[index] : 0;
        return name switch
        {
            "matrix" when numbers.Count >= 6 => new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]),
            "translate" => Matrix.Translation(At(0), At(1)),
            "scale" => Matrix.Scaling(At(0), numbers.Count >= 2 ? numbers[1] : At(0)),

            // A rotation around a point: move it to the origin, turn, move back.
            "rotate" when numbers.Count >= 3 => Matrix.Multiply(
                Matrix.Multiply(Matrix.Translation(-numbers[1], -numbers[2]), Matrix.Rotation(numbers[0])),
                Matrix.Translation(numbers[1], numbers[2])),
            "rotate" => Matrix.Rotation(At(0)),
            "skewx" => new Matrix(1, 0, MathF.Tan(At(0) * MathF.PI / 180), 1, 0, 0),
            "skewy" => new Matrix(1, MathF.Tan(At(0) * MathF.PI / 180), 0, 1, 0, 0),
            _ => Matrix.Identity,
        };
    }

    /// <summary>A stroke dash pattern; "none" and patterns that are all zero switch dashing off.</summary>
    public static float[]? Dashes(string? text, float percentBasis)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() is "none" or "inherit")
            return null;

        var scanner = new SvgScanner(text);
        var dashes = new List<float>(4);
        while (dashes.Count < 64 && scanner.TryReadNumber(out var value))
        {
            // Percentages are relative to the diagonal of the viewport.
            dashes.Add(Math.Max(text.Contains('%') ? value / 100 * percentBasis : value, 0));
        }

        if (dashes.Count == 0 || dashes.TrueForAll(dash => dash <= 0))
            return null;

        // An odd number of dashes repeats to become even, as the specification requires.
        if (dashes.Count % 2 == 1)
            dashes.AddRange(dashes);

        return [.. dashes];
    }

    /// <summary>The CSS named colors; "none", "transparent" and "currentColor" are handled by the caller.</summary>
    private static Color? Named(string name) => name switch
    {
        "aliceblue" => new Color(0xF0, 0xF8, 0xFF),
        "antiquewhite" => new Color(0xFA, 0xEB, 0xD7),
        "aqua" => new Color(0x00, 0xFF, 0xFF),
        "aquamarine" => new Color(0x7F, 0xFF, 0xD4),
        "azure" => new Color(0xF0, 0xFF, 0xFF),
        "beige" => new Color(0xF5, 0xF5, 0xDC),
        "bisque" => new Color(0xFF, 0xE4, 0xC4),
        "black" => new Color(0x00, 0x00, 0x00),
        "blanchedalmond" => new Color(0xFF, 0xEB, 0xCD),
        "blue" => new Color(0x00, 0x00, 0xFF),
        "blueviolet" => new Color(0x8A, 0x2B, 0xE2),
        "brown" => new Color(0xA5, 0x2A, 0x2A),
        "burlywood" => new Color(0xDE, 0xB8, 0x87),
        "cadetblue" => new Color(0x5F, 0x9E, 0xA0),
        "chartreuse" => new Color(0x7F, 0xFF, 0x00),
        "chocolate" => new Color(0xD2, 0x69, 0x1E),
        "coral" => new Color(0xFF, 0x7F, 0x50),
        "cornflowerblue" => new Color(0x64, 0x95, 0xED),
        "cornsilk" => new Color(0xFF, 0xF8, 0xDC),
        "crimson" => new Color(0xDC, 0x14, 0x3C),
        "cyan" => new Color(0x00, 0xFF, 0xFF),
        "darkblue" => new Color(0x00, 0x00, 0x8B),
        "darkcyan" => new Color(0x00, 0x8B, 0x8B),
        "darkgoldenrod" => new Color(0xB8, 0x86, 0x0B),
        "darkgray" => new Color(0xA9, 0xA9, 0xA9),
        "darkgreen" => new Color(0x00, 0x64, 0x00),
        "darkgrey" => new Color(0xA9, 0xA9, 0xA9),
        "darkkhaki" => new Color(0xBD, 0xB7, 0x6B),
        "darkmagenta" => new Color(0x8B, 0x00, 0x8B),
        "darkolivegreen" => new Color(0x55, 0x6B, 0x2F),
        "darkorange" => new Color(0xFF, 0x8C, 0x00),
        "darkorchid" => new Color(0x99, 0x32, 0xCC),
        "darkred" => new Color(0x8B, 0x00, 0x00),
        "darksalmon" => new Color(0xE9, 0x96, 0x7A),
        "darkseagreen" => new Color(0x8F, 0xBC, 0x8F),
        "darkslateblue" => new Color(0x48, 0x3D, 0x8B),
        "darkslategray" => new Color(0x2F, 0x4F, 0x4F),
        "darkslategrey" => new Color(0x2F, 0x4F, 0x4F),
        "darkturquoise" => new Color(0x00, 0xCE, 0xD1),
        "darkviolet" => new Color(0x94, 0x00, 0xD3),
        "deeppink" => new Color(0xFF, 0x14, 0x93),
        "deepskyblue" => new Color(0x00, 0xBF, 0xFF),
        "dimgray" => new Color(0x69, 0x69, 0x69),
        "dimgrey" => new Color(0x69, 0x69, 0x69),
        "dodgerblue" => new Color(0x1E, 0x90, 0xFF),
        "firebrick" => new Color(0xB2, 0x22, 0x22),
        "floralwhite" => new Color(0xFF, 0xFA, 0xF0),
        "forestgreen" => new Color(0x22, 0x8B, 0x22),
        "fuchsia" => new Color(0xFF, 0x00, 0xFF),
        "gainsboro" => new Color(0xDC, 0xDC, 0xDC),
        "ghostwhite" => new Color(0xF8, 0xF8, 0xFF),
        "gold" => new Color(0xFF, 0xD7, 0x00),
        "goldenrod" => new Color(0xDA, 0xA5, 0x20),
        "gray" => new Color(0x80, 0x80, 0x80),
        "grey" => new Color(0x80, 0x80, 0x80),
        "green" => new Color(0x00, 0x80, 0x00),
        "greenyellow" => new Color(0xAD, 0xFF, 0x2F),
        "honeydew" => new Color(0xF0, 0xFF, 0xF0),
        "hotpink" => new Color(0xFF, 0x69, 0xB4),
        "indianred" => new Color(0xCD, 0x5C, 0x5C),
        "indigo" => new Color(0x4B, 0x00, 0x82),
        "ivory" => new Color(0xFF, 0xFF, 0xF0),
        "khaki" => new Color(0xF0, 0xE6, 0x8C),
        "lavender" => new Color(0xE6, 0xE6, 0xFA),
        "lavenderblush" => new Color(0xFF, 0xF0, 0xF5),
        "lawngreen" => new Color(0x7C, 0xFC, 0x00),
        "lemonchiffon" => new Color(0xFF, 0xFA, 0xCD),
        "lightblue" => new Color(0xAD, 0xD8, 0xE6),
        "lightcoral" => new Color(0xF0, 0x80, 0x80),
        "lightcyan" => new Color(0xE0, 0xFF, 0xFF),
        "lightgoldenrodyellow" => new Color(0xFA, 0xFA, 0xD2),
        "lightgray" => new Color(0xD3, 0xD3, 0xD3),
        "lightgreen" => new Color(0x90, 0xEE, 0x90),
        "lightgrey" => new Color(0xD3, 0xD3, 0xD3),
        "lightpink" => new Color(0xFF, 0xB6, 0xC1),
        "lightsalmon" => new Color(0xFF, 0xA0, 0x7A),
        "lightseagreen" => new Color(0x20, 0xB2, 0xAA),
        "lightskyblue" => new Color(0x87, 0xCE, 0xFA),
        "lightslategray" => new Color(0x77, 0x88, 0x99),
        "lightslategrey" => new Color(0x77, 0x88, 0x99),
        "lightsteelblue" => new Color(0xB0, 0xC4, 0xDE),
        "lightyellow" => new Color(0xFF, 0xFF, 0xE0),
        "lime" => new Color(0x00, 0xFF, 0x00),
        "limegreen" => new Color(0x32, 0xCD, 0x32),
        "linen" => new Color(0xFA, 0xF0, 0xE6),
        "magenta" => new Color(0xFF, 0x00, 0xFF),
        "maroon" => new Color(0x80, 0x00, 0x00),
        "mediumaquamarine" => new Color(0x66, 0xCD, 0xAA),
        "mediumblue" => new Color(0x00, 0x00, 0xCD),
        "mediumorchid" => new Color(0xBA, 0x55, 0xD3),
        "mediumpurple" => new Color(0x93, 0x70, 0xDB),
        "mediumseagreen" => new Color(0x3C, 0xB3, 0x71),
        "mediumslateblue" => new Color(0x7B, 0x68, 0xEE),
        "mediumspringgreen" => new Color(0x00, 0xFA, 0x9A),
        "mediumturquoise" => new Color(0x48, 0xD1, 0xCC),
        "mediumvioletred" => new Color(0xC7, 0x15, 0x85),
        "midnightblue" => new Color(0x19, 0x19, 0x70),
        "mintcream" => new Color(0xF5, 0xFF, 0xFA),
        "mistyrose" => new Color(0xFF, 0xE4, 0xE1),
        "moccasin" => new Color(0xFF, 0xE4, 0xB5),
        "navajowhite" => new Color(0xFF, 0xDE, 0xAD),
        "navy" => new Color(0x00, 0x00, 0x80),
        "oldlace" => new Color(0xFD, 0xF5, 0xE6),
        "olive" => new Color(0x80, 0x80, 0x00),
        "olivedrab" => new Color(0x6B, 0x8E, 0x23),
        "orange" => new Color(0xFF, 0xA5, 0x00),
        "orangered" => new Color(0xFF, 0x45, 0x00),
        "orchid" => new Color(0xDA, 0x70, 0xD6),
        "palegoldenrod" => new Color(0xEE, 0xE8, 0xAA),
        "palegreen" => new Color(0x98, 0xFB, 0x98),
        "paleturquoise" => new Color(0xAF, 0xEE, 0xEE),
        "palevioletred" => new Color(0xDB, 0x70, 0x93),
        "papayawhip" => new Color(0xFF, 0xEF, 0xD5),
        "peachpuff" => new Color(0xFF, 0xDA, 0xB9),
        "peru" => new Color(0xCD, 0x85, 0x3F),
        "pink" => new Color(0xFF, 0xC0, 0xCB),
        "plum" => new Color(0xDD, 0xA0, 0xDD),
        "powderblue" => new Color(0xB0, 0xE0, 0xE6),
        "purple" => new Color(0x80, 0x00, 0x80),
        "rebeccapurple" => new Color(0x66, 0x33, 0x99),
        "red" => new Color(0xFF, 0x00, 0x00),
        "rosybrown" => new Color(0xBC, 0x8F, 0x8F),
        "royalblue" => new Color(0x41, 0x69, 0xE1),
        "saddlebrown" => new Color(0x8B, 0x45, 0x13),
        "salmon" => new Color(0xFA, 0x80, 0x72),
        "sandybrown" => new Color(0xF4, 0xA4, 0x60),
        "seagreen" => new Color(0x2E, 0x8B, 0x57),
        "seashell" => new Color(0xFF, 0xF5, 0xEE),
        "sienna" => new Color(0xA0, 0x52, 0x2D),
        "silver" => new Color(0xC0, 0xC0, 0xC0),
        "skyblue" => new Color(0x87, 0xCE, 0xEB),
        "slateblue" => new Color(0x6A, 0x5A, 0xCD),
        "slategray" => new Color(0x70, 0x80, 0x90),
        "slategrey" => new Color(0x70, 0x80, 0x90),
        "snow" => new Color(0xFF, 0xFA, 0xFA),
        "springgreen" => new Color(0x00, 0xFF, 0x7F),
        "steelblue" => new Color(0x46, 0x82, 0xB4),
        "tan" => new Color(0xD2, 0xB4, 0x8C),
        "teal" => new Color(0x00, 0x80, 0x80),
        "thistle" => new Color(0xD8, 0xBF, 0xD8),
        "tomato" => new Color(0xFF, 0x63, 0x47),
        "turquoise" => new Color(0x40, 0xE0, 0xD0),
        "violet" => new Color(0xEE, 0x82, 0xEE),
        "wheat" => new Color(0xF5, 0xDE, 0xB3),
        "white" => new Color(0xFF, 0xFF, 0xFF),
        "whitesmoke" => new Color(0xF5, 0xF5, 0xF5),
        "yellow" => new Color(0xFF, 0xFF, 0x00),
        "yellowgreen" => new Color(0x9A, 0xCD, 0x32),

        // Color converts implicitly from string, so the null must say which type it is.
        _ => (Color?)null,
    };
}
