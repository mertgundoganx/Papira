using System.Globalization;
using Papira.Elements;
using Papira.Infrastructure;
using Papira.Svg;

namespace Papira.Html;

/// <summary>The four sides of a box, in points.</summary>
internal readonly record struct Edges(float Left, float Top, float Right, float Bottom)
{
    public bool Any => Left != 0 || Top != 0 || Right != 0 || Bottom != 0;

}

/// <summary>
/// The properties an element passes down to the elements inside it. Everything here is inherited, as
/// CSS inherits it; the box properties of an element are computed separately, where it is laid out.
/// </summary>
internal sealed class HtmlStyle
{
    public float FontSize { get; set; } = 12;

    /// <summary>Whether the size was actually stated; when it was not, the surrounding document decides.</summary>
    public bool FontSizeSet { get; set; }

    public string? Family { get; set; }

    public FontWeight? Weight { get; set; }

    public bool? Italic { get; set; }

    public bool? Underline { get; set; }

    public bool? Strikethrough { get; set; }

    public Color? Color { get; set; }

    public float? LineHeight { get; set; }

    public float? LetterSpacing { get; set; }

    public TextAlignment? Align { get; set; }

    /// <summary>Inside a &lt;pre&gt;, spaces and line breaks are kept as they are written.</summary>
    public bool Preformatted { get; set; }

    public string? ListStyle { get; set; }

    /// <summary>The features the text asks the font for, by their four-letter OpenType names.</summary>
    public string? Features { get; set; }

    /// <summary>Whether the text is drawn in capital letters, in small ones, or as it was written.</summary>
    public string? Transform { get; set; }

    /// <summary>
    /// The language the text is written in, from the <c>lang</c> of the markup. Capital letters depend on
    /// it: the capital of a Turkish "i" is "İ", and of an English one "I".
    /// </summary>
    public System.Globalization.CultureInfo? Language { get; set; }

    public string? LinkUri { get; set; }

    public string? LinkSection { get; set; }

    public HtmlStyle Clone() => (HtmlStyle)MemberwiseClone();

    /// <summary>The text style a span of this text is drawn with.</summary>
    public TextStyle ToTextStyle()
    {
        var style = TextStyle.Default;
        if (FontSizeSet)
            style = style.FontSize(FontSize);
        if (Family != null)
            style = style.FontFamily(Family);
        if (Weight is { } weight)
            style = style.FontWeight(weight);
        if (Italic is { } italic)
            style = style.Italic(italic);
        if (Underline is { } underline)
            style = style.Underline(underline);
        if (Strikethrough is { } strikethrough)
            style = style.Strikethrough(strikethrough);
        if (Color is { } color)
            style = style.FontColor(color);
        if (LineHeight is { } lineHeight)
            style = style.LineHeight(lineHeight);
        if (LetterSpacing is { } spacing)
            style = style.LetterSpacing(spacing);
        if (Features != null)
            style = style.FontFeatures(Features);

        return style;
    }
}

/// <summary>What an element draws around its content: margins, borders, padding, a background, a size.</summary>
internal readonly record struct HtmlBox
{
    public Edges Margin { get; init; }

    public Edges Padding { get; init; }

    public Edges Border { get; init; }

    public Color BorderColor { get; init; }

    /// <summary>How far the corners are rounded, in points.</summary>
    public float CornerRadius { get; init; }

    public Color? Background { get; init; }

    public CssLength? Width { get; init; }

    public CssLength? Height { get; init; }

    public CssLength? MinWidth { get; init; }

    public CssLength? MaxWidth { get; init; }

    public CssLength? MinHeight { get; init; }

    public CssLength? MaxHeight { get; init; }

    /// <summary>What a <c>transform: scale()</c> magnifies the element by, horizontally and vertically.</summary>
    public (float X, float Y)? Scale { get; init; }

    /// <summary>True when the element is taken out of the flow and placed against its container.</summary>
    public bool Absolute { get; init; }

    /// <summary>True when the element is what the elements placed inside it are placed against.</summary>
    public bool Positioned { get; init; }

    public CssLength? Left { get; init; }

    public CssLength? Top { get; init; }

    public CssLength? Right { get; init; }

    public CssLength? Bottom { get; init; }

    public bool Hidden { get; init; }

    public bool BreakBefore { get; init; }

    public bool BreakAfter { get; init; }

    public bool KeepTogether { get; init; }

    public HorizontalAlignment? Align { get; init; }
}

/// <summary>Reads the values of the CSS properties Papira understands.</summary>
internal static class HtmlValues
{
    /// <summary>
    /// A length in points, for the places that need a number while the document is being composed. A value
    /// that depends on the space around it — a percentage, a viewport unit — is worked out against
    /// <paramref name="percentBasis"/> and against nothing at all; use <see cref="Measure"/> where the
    /// space is only known once the element is laid out.
    /// </summary>
    public static float? Length(string? text, float fontSize, float percentBasis = 0) =>
        Measure(text, fontSize)?.Resolve(percentBasis, default);

    /// <summary>
    /// A length as it is written: so many points, plus a share of the space around it, plus a share of the
    /// page. CSS pixels are the 96 dpi kind, so one of them is three quarters of a point.
    /// </summary>
    public static CssLength? Measure(string? text, float fontSize)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase) || text.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return null;

        if (text.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) || text.StartsWith("-webkit-calc(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = text[(text.IndexOf('(', StringComparison.Ordinal) + 1)..].TrimEnd();
            if (inner.EndsWith(')'))
                inner = inner[..^1];

            var position = 0;
            var result = Calculation.Sum(inner, ref position, fontSize);
            Calculation.SkipSpace(inner, ref position);

            // A sum Papira cannot read through to the end is not guessed at: the property is left unset.
            return position == inner.Length ? result?.Length : null;
        }

        return Term(text, fontSize);
    }

    /// <summary>One length with its unit, as it stands in the markup.</summary>
    private static CssLength? Term(string text, float fontSize)
    {
        var scanner = new SvgScanner(text);
        if (!scanner.TryReadNumber(out var value))
            return null;

        return Unit(text) switch
        {
            "%" => new CssLength(0, value),
            "vh" => new CssLength(0, 0, 0, value),
            "vw" => new CssLength(0, 0, value),
            "pt" => new CssLength(value),
            "pc" => new CssLength(value * 12),
            "in" => new CssLength(value * 72),
            "cm" => new CssLength(value * 72 / 2.54f),
            "mm" => new CssLength(value * 72 / 25.4f),
            "q" => new CssLength(value * 72 / 101.6f),
            "em" => new CssLength(value * fontSize),
            "rem" => new CssLength(value * fontSize),
            "ex" => new CssLength(value * fontSize / 2),
            _ => new CssLength(value * 0.75f),
        };
    }

    /// <summary>
    /// The sums a <c>calc()</c> is written as. A length can be added to a length and multiplied by a plain
    /// number, which is why each piece remembers whether it was a number or a length.
    /// </summary>
    private static class Calculation
    {
        /// <summary>A piece of a sum: a length, or a plain number that a length may be multiplied by.</summary>
        public readonly record struct Value(CssLength Length, float? Number)
        {
            public CssLength Scaled(float factor) => Number is { } number ? new CssLength(number * factor) : Length * factor;
        }

        public static Value? Sum(string text, ref int position, float fontSize)
        {
            if (Product(text, ref position, fontSize) is not { } total)
                return null;

            while (true)
            {
                SkipSpace(text, ref position);
                if (position >= text.Length || (text[position] != '+' && text[position] != '-'))
                    return total;

                var plus = text[position++] == '+';
                if (Product(text, ref position, fontSize) is not { } next)
                    return null;

                // A number and a length cannot be added; such a sum means nothing and is left unread.
                if (total.Number.HasValue != next.Number.HasValue)
                    return null;

                total = total.Number is { } left && next.Number is { } right
                    ? new Value(default, plus ? left + right : left - right)
                    : new Value(plus ? total.Length + next.Length : total.Length - next.Length, null);
            }
        }

        private static Value? Product(string text, ref int position, float fontSize)
        {
            if (Factor(text, ref position, fontSize) is not { } total)
                return null;

            while (true)
            {
                SkipSpace(text, ref position);
                if (position >= text.Length || (text[position] != '*' && text[position] != '/'))
                    return total;

                var times = text[position++] == '*';
                if (Factor(text, ref position, fontSize) is not { } next)
                    return null;

                if (times)
                {
                    // One side of a multiplication has to be a plain number, as CSS requires.
                    if (next.Number is { } factor)
                        total = new Value(total.Scaled(factor), total.Number is { } value ? value * factor : null);
                    else if (total.Number is { } scale)
                        total = new Value(next.Scaled(scale), null);
                    else
                        return null;
                }
                else
                {
                    if (next.Number is not { } divisor || divisor == 0)
                        return null;

                    total = total.Number is { } value ? new Value(default, value / divisor) : new Value(total.Length / divisor, null);
                }
            }
        }

        private static Value? Factor(string text, ref int position, float fontSize)
        {
            SkipSpace(text, ref position);
            if (position >= text.Length)
                return null;

            if (text[position] == '(')
            {
                position++;
                var inner = Sum(text, ref position, fontSize);
                SkipSpace(text, ref position);
                if (inner == null || position >= text.Length || text[position] != ')')
                    return null;

                position++;
                return inner;
            }

            var start = position;
            while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] is '.' or '-' or '+' or 'e' or 'E'))
                position++;

            // The unit sits straight after the number, e.g. the "px" of "10px".
            var number = position;
            while (position < text.Length && (char.IsAsciiLetter(text[position]) || text[position] == '%'))
                position++;

            if (position == start)
                return null;

            var piece = text[start..position];
            if (position == number)
            {
                return float.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)
                    ? new Value(default, plain)
                    : null;
            }

            return Term(piece, fontSize) is { } length ? new Value(length, null) : null;
        }

        public static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }
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

    public static Color? Color(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;

        return SvgValues.TryColor(text, out var color) ? color : (Color?)null;
    }

    /// <summary>
    /// The one to four lengths of a shorthand such as "margin: 1em 0 2em". Sides that the shorthand
    /// leaves out take the value of the side opposite them, as CSS says.
    /// </summary>
    public static Edges Sides(string text, float fontSize, Edges fallback)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return fallback;

        var values = new float[4];
        for (var i = 0; i < parts.Length && i < 4; i++)
            values[i] = Length(parts[i], fontSize) ?? 0;

        return parts.Length switch
        {
            1 => new Edges(values[0], values[0], values[0], values[0]),
            2 => new Edges(values[1], values[0], values[1], values[0]),
            3 => new Edges(values[1], values[0], values[1], values[2]),
            _ => new Edges(values[3], values[0], values[1], values[2]),
        };
    }

    /// <summary>The width and the colour of a border shorthand such as "1px solid #ccc".</summary>
    public static (float Width, Color? Color, bool None) Border(string text, float fontSize)
    {
        float? width = null;
        Color? color = null;
        var none = false;

        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "none" or "hidden":
                    none = true;
                    break;
                case "thin":
                    width = 0.75f;
                    break;
                case "medium":
                    width = 1.5f;
                    break;
                case "thick":
                    width = 3.75f;
                    break;
                case "solid" or "dashed" or "dotted" or "double" or "groove" or "ridge" or "inset" or "outset":
                    break;
                default:
                    if (Color(part) is { } parsed)
                        color = parsed;
                    else
                        width ??= Length(part, fontSize);

                    break;
            }
        }

        return (width ?? (none ? 0 : 0.75f), color, none);
    }

    /// <summary>
    /// What a <c>transform</c> magnifies the element by. Only scaling changes how much room an element
    /// needs, so that is the one transform a printed document takes from the markup.
    /// </summary>
    public static (float X, float Y)? Scale(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = text.IndexOf("scale(", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += "scale(".Length;
        var end = text.IndexOf(')', start);
        if (end < 0)
            return null;

        var parts = text[start..end].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || x <= 0)
            return null;

        if (parts.Length == 1)
            return (x, x);

        return float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && y > 0 ? (x, y) : null;
    }

    public static FontWeight? Weight(string text) => text.ToLowerInvariant() switch
    {
        "bold" or "bolder" => FontWeight.Bold,
        "normal" or "lighter" => FontWeight.Normal,
        _ => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value switch
            {
                <= 150 => FontWeight.Thin,
                <= 250 => FontWeight.ExtraLight,
                <= 350 => FontWeight.Light,
                <= 450 => FontWeight.Normal,
                <= 550 => FontWeight.Medium,
                <= 650 => FontWeight.SemiBold,
                <= 750 => FontWeight.Bold,
                <= 850 => FontWeight.ExtraBold,
                _ => FontWeight.Black,
            }
            : null,
    };

    /// <summary>
    /// The four-letter names of the features a <c>font-variant</c> asks for in words: figures of equal
    /// width, a slashed zero, small capitals and the rest.
    /// </summary>
    public static string? Variants(string text)
    {
        var tags = new List<string>(2);
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = word.ToLowerInvariant() switch
            {
                "tabular-nums" => "tnum",
                "proportional-nums" => "pnum",
                "lining-nums" => "lnum",
                "oldstyle-nums" => "onum",
                "slashed-zero" => "zero",
                "ordinal" => "ordn",
                "diagonal-fractions" => "frac",
                "stacked-fractions" => "afrc",
                "small-caps" => "smcp",
                "all-small-caps" => "c2sc",
                _ => null,
            };

            if (tag != null)
                tags.Add(tag);
        }

        return tags.Count > 0 ? string.Join(' ', tags) : null;
    }

    public static TextAlignment? Align(string text) => text.ToLowerInvariant() switch
    {
        "left" or "start" => TextAlignment.Left,
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.Right,
        "justify" => TextAlignment.Justify,
        _ => null,
    };

    /// <summary>The first family of a font-family list that is not a generic name.</summary>
    public static string? Family(string text)
    {
        foreach (var part in text.Split(','))
        {
            var family = part.Trim().Trim('"', '\'');
            if (family.Length == 0)
                continue;

            if (family is "serif" or "sans-serif" or "cursive" or "fantasy" or "system-ui" or "ui-sans-serif" or "ui-serif")
                continue;

            return family is "monospace" or "ui-monospace" ? "Courier New" : family;
        }

        return null;
    }
}
