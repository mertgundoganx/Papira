using Papira.Fonts;
using static Papira.Fonts.TrueTypeFont;

namespace Papira.Text;

/// <summary>
/// Turns a run of characters into the glyphs a font draws for them. For scripts whose letters change shape
/// depending on their neighbours — Arabic and its relatives — the cursive form of every letter is worked out
/// first, and the font's own rules then pick the glyph for it.
/// </summary>
internal static class TextShaper
{
    private static readonly uint Arabic = Tag("arab");
    private static readonly uint Hebrew = Tag("hebr");
    private static readonly uint Syriac = Tag("syrc");
    private static readonly uint Thaana = Tag("thaa");
    private static readonly uint Nko = Tag("nko ");
    private static readonly uint Latin = Tag("latn");

    /// <summary>The OpenType script a character belongs to, or zero when it does not decide one.</summary>
    public static uint ScriptOf(int codepoint) => codepoint switch
    {
        < 0x0590 => 0,
        >= 0x0590 and <= 0x05FF => Hebrew,
        >= 0x0600 and <= 0x06FF => Arabic,
        >= 0x0700 and <= 0x074F => Syriac,
        >= 0x0750 and <= 0x077F => Arabic,
        >= 0x0780 and <= 0x07BF => Thaana,
        >= 0x07C0 and <= 0x07FF => Nko,
        >= 0x0860 and <= 0x08FF => Arabic,
        >= 0xFB1D and <= 0xFB4F => Hebrew,
        >= 0xFB50 and <= 0xFDFF => Arabic,
        >= 0xFE70 and <= 0xFEFF => Arabic,
        _ => 0,
    };

    /// <summary>
    /// True for the characters a font may draw as a picture, and for those that build one together with
    /// the picture beside them — a flag out of two letters, a skin tone, a family joined by zero width
    /// joiners. The font composes them through its own rules, so such text has to be shaped.
    /// </summary>
    public static bool IsEmoji(int codepoint)
    {
        var ranges = UnicodeTables.EmojiRanges;
        var low = 0;
        var high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (codepoint < ranges[middle * 2])
                high = middle - 1;
            else if (codepoint > ranges[middle * 2 + 1])
                low = middle + 1;
            else
                return true;
        }

        return false;
    }

    /// <summary>True for the scripts whose letters join to their neighbours.</summary>
    public static bool IsCursive(uint script) => script == Arabic || script == Syriac || script == Nko;

    /// <summary>The script tag to ask the font for, falling back to Latin for everything else.</summary>
    public static uint ScriptTag(uint script) => script == 0 ? Latin : script;

    /// <summary>
    /// Fills <paramref name="buffer"/> with the glyphs for <paramref name="text"/>. The characters that
    /// only control joining are used for the shaping and then dropped, as they have nothing to draw.
    /// </summary>
    public static void Shape(TrueTypeFont font, ReadOnlySpan<int> text, uint script, bool rightToLeft, ShapingBuffer buffer)
    {
        buffer.Clear();

        var cursive = IsCursive(script);
        Span<JoiningForm> forms = text.Length <= 256 ? stackalloc JoiningForm[text.Length] : new JoiningForm[text.Length];
        if (cursive)
            ArabicShaper.ComputeForms(text, forms);

        for (var i = 0; i < text.Length; i++)
        {
            var codepoint = text[i];

            // A presentation selector only says whether to draw the character beside it as a picture; it
            // takes no part in the font's rules, and leaving it out lets those rules see what it separated.
            if (codepoint is 0xFE0E or 0xFE0F)
                continue;

            // In right to left text a bracket is drawn as its mirror image.
            var character = rightToLeft ? Bidi.Mirror(codepoint) : codepoint;
            var glyph = font.GetGlyph(character);

            // The joiners shape their neighbours and are never drawn. A font that has no glyph for one
            // is better off without it, so that the rules see the characters on either side as neighbours.
            var invisible = codepoint is 0x200C or 0x200D;
            if (invisible && glyph == 0)
                continue;

            buffer.Add(glyph, i, cursive ? forms[i] : JoiningForm.Isolated, invisible);
        }

        var substitution = font.Substitution;
        if (!substitution.IsEmpty)
            substitution.Apply(ScriptTag(script), cursive ? GlyphSubstitution.CursiveStages : GlyphSubstitution.SimpleStages, buffer);

        buffer.RemoveInvisible();
    }
}
