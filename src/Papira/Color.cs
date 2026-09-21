using System.Globalization;

namespace Papira;

/// <summary>An opaque RGB color.</summary>
public readonly record struct Color(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> (the leading <c>#</c> is optional).</summary>
    public static Color FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var s = hex.AsSpan().TrimStart('#');

        if (s.Length == 3)
            return new Color(Expand(s[0]), Expand(s[1]), Expand(s[2]));

        if (s.Length == 6)
            return new Color(
                byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        throw new FormatException($"'{hex}' is not a valid hex color. Use #RGB or #RRGGBB.");

        static byte Expand(char c)
        {
            var v = byte.Parse(stackalloc char[] { c }, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (byte)(v * 17);
        }
    }

    /// <summary>Converts a hex string such as <c>"#1E3A8A"</c>; throws <see cref="FormatException"/> when it is invalid.</summary>
    public static implicit operator Color(string hex) => FromHex(hex);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Frequently used colors, taken from the Material Design palette.</summary>
public static class Colors
{
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Red = new(229, 57, 53);
    public static readonly Color Green = new(67, 160, 71);
    public static readonly Color Blue = new(30, 136, 229);
    public static readonly Color Orange = new(251, 140, 0);
    public static readonly Color Yellow = new(253, 216, 53);
    public static readonly Color Purple = new(142, 36, 170);

    public static class Grey
    {
        public static readonly Color Lighten5 = new(250, 250, 250);
        public static readonly Color Lighten4 = new(245, 245, 245);
        public static readonly Color Lighten3 = new(238, 238, 238);
        public static readonly Color Lighten2 = new(224, 224, 224);
        public static readonly Color Lighten1 = new(189, 189, 189);
        public static readonly Color Medium = new(158, 158, 158);
        public static readonly Color Darken1 = new(117, 117, 117);
        public static readonly Color Darken2 = new(97, 97, 97);
        public static readonly Color Darken3 = new(66, 66, 66);
        public static readonly Color Darken4 = new(33, 33, 33);
    }
}
