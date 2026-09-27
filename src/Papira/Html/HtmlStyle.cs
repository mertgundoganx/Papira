using System.Globalization;
using Papira.Elements;
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

    public Color? Background { get; init; }

    public float? Width { get; init; }

    public float? Height { get; init; }

    public bool Hidden { get; init; }

    public bool BreakBefore { get; init; }

    public bool BreakAfter { get; init; }

    public bool KeepTogether { get; init; }

    public HorizontalAlignment? Align { get; init; }
}

/// <summary>Reads the values of the CSS properties Papira understands.</summary>
internal static class HtmlValues
{
    /// <summary>A length in points. CSS pixels are the 96 dpi kind, so one of them is three quarters of a point.</summary>
    public static float? Length(string? text, float fontSize, float percentBasis = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase) || text.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return null;

        var scanner = new SvgScanner(text);
        if (!scanner.TryReadNumber(out var value))
            return null;

        var unit = Unit(text);
        return unit switch
        {
            "%" => percentBasis * value / 100,
            "pt" => value,
            "pc" => value * 12,
            "in" => value * 72,
            "cm" => value * 72 / 2.54f,
            "mm" => value * 72 / 25.4f,
            "q" => value * 72 / 101.6f,
            "em" => value * fontSize,
            "rem" => value * fontSize,
            "ex" => value * fontSize / 2,
            _ => value * 0.75f,
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
