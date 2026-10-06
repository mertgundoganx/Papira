using System.Text;
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
        >= 0x0900 and <= 0x0D7F => IndicShaper.ScriptOf(codepoint),
        >= 0x1CD0 and <= 0x1CFF => IndicShaper.ScriptOf(codepoint),
        >= 0xA8E0 and <= 0xA8FF => IndicShaper.ScriptOf(codepoint),
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
    public static void Shape(
        TrueTypeFont font,
        ReadOnlySpan<int> text,
        uint script,
        bool rightToLeft,
        ShapingBuffer buffer,
        bool ligatures = true,
        uint[]? features = null)
    {
        // The scripts of India are written in syllables, which are put in order before the font is
        // asked to draw them; that is a shaper of its own.
        if (IndicShaper.IsIndic(script))
        {
            IndicShaper.Shape(font, text, script, buffer);
            Position(font, text, buffer, script, rightToLeft);
            return;
        }

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

            // An accented letter is drawn as one glyph where the font has one, and as a letter with the
            // accent attached to it where it does not. Which of the two a font offers differs, so both
            // ways round are tried: a letter and its accent are put together, and one the font is
            // missing is taken apart.
            var merged = character;
            var consumed = 0;
            while (i + consumed + 1 < text.Length && IsCombining(text[i + consumed + 1]) &&
                Compose(merged, text[i + consumed + 1]) is { } composed && font.GetGlyph(composed) != 0)
            {
                merged = composed;
                consumed++;
            }

            if (consumed > 0)
            {
                buffer.Add(font.GetGlyph(merged), i, cursive ? forms[i] : JoiningForm.Isolated);
                buffer[buffer.Length - 1].Length = (ushort)(consumed + 1);
                i += consumed;
                continue;
            }

            var glyph = font.GetGlyph(character);
            if (glyph == 0 && Decompose(character) is { } parts && HasAll(font, parts))
            {
                foreach (var part in parts)
                    buffer.Add(font.GetGlyph(part), i, cursive ? forms[i] : JoiningForm.Isolated);

                continue;
            }

            // The joiners shape their neighbours and are never drawn. A font that has no glyph for one
            // is better off without it, so that the rules see the characters on either side as neighbours.
            var invisible = codepoint is 0x200C or 0x200D;
            if (invisible && glyph == 0)
                continue;

            buffer.Add(glyph, i, cursive ? forms[i] : JoiningForm.Isolated, invisible);
            buffer[buffer.Length - 1].JoinerKind = codepoint switch
            {
                0x200D => JoinerKind.Joiner,
                0x200C => JoinerKind.NonJoiner,
                _ => JoinerKind.None,
            };
        }

        var substitution = font.Substitution;
        if (!substitution.IsEmpty)
            substitution.Apply(ScriptTag(script), Stages(cursive, ligatures, features), buffer);

        buffer.RemoveInvisible();
        Position(font, text, buffer, script, rightToLeft);
    }

    /// <summary>
    /// What Papira asks the font for, with the features the text itself asks for added at the end. The
    /// stages of a run are looked up rather than built, because the font remembers what it worked out
    /// for a set of them and would have to work it out again for every new one.
    /// </summary>
    private static uint[][] Stages(bool cursive, bool ligatures, uint[]? features)
    {
        var stages = (cursive, ligatures) switch
        {
            (true, true) => GlyphSubstitution.CursiveStages,
            (true, false) => GlyphSubstitution.CursiveStagesWithoutLigatures,
            (false, true) => GlyphSubstitution.SimpleStages,
            _ => GlyphSubstitution.SimpleStagesWithoutLigatures,
        };

        if (features is not { Length: > 0 })
            return stages;

        var kind = (cursive ? 2 : 0) + (ligatures ? 1 : 0);
        return Extended.GetOrAdd((string.Join(',', features), kind), _ => [.. stages, features]);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Features, int Kind), uint[][]> Extended = new();

    /// <summary>
    /// Puts the glyphs the font chose in their places: the marks on the letters they belong to, the
    /// pairs kerned. What the font says about each glyph is only known once they have been chosen.
    /// </summary>
    private static void Position(TrueTypeFont font, ReadOnlySpan<int> text, ShapingBuffer buffer, uint script, bool rightToLeft)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            ref var shaped = ref buffer[i];
            shaped.BaseAdvance = (short)Math.Clamp(font.GetAdvance(shaped.Glyph), short.MinValue, short.MaxValue);
            shaped.Mark = font.HasGlyphClasses
                ? font.IsMark(shaped.Glyph)
                : IsCombining(text[Math.Clamp(shaped.Cluster, 0, text.Length - 1)]);
        }

        var positioning = font.Positioning;
        var tag = IndicShaper.IsIndic(script) ? IndicShaper.ScriptTag(font, script) : ScriptTag(script);
        if (!positioning.IsEmpty)
            positioning.Apply(tag, buffer, rightToLeft);

        // A font with no positioning table of its own leaves its marks to Papira.
        if (positioning.IsEmpty)
            GlyphPositioning.PlaceMarksWithoutRules(buffer, font);
    }

    /// <summary>The single character a letter and the mark after it stand for, if there is one.</summary>
    private static int? Compose(int first, int second)
    {
        if (second < 0x0300)
            return null;

        var pair = char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
        var composed = pair.Normalize(NormalizationForm.FormC);

        // Only a canonical composition of the two into exactly one character counts.
        if (composed.Length > 2 || (composed.Length == 2 && !char.IsSurrogatePair(composed[0], composed[1])))
            return null;

        var codepoint = char.ConvertToUtf32(composed, 0);
        return codepoint == first ? null : codepoint;
    }

    /// <summary>The letter and the marks a character is made of, for a font that has no glyph for it.</summary>
    private static int[]? Decompose(int codepoint)
    {
        if (codepoint < 0x00C0)
            return null;

        var text = char.ConvertFromUtf32(codepoint);
        var decomposed = text.Normalize(NormalizationForm.FormD);
        if (decomposed == text)
            return null;

        var parts = new List<int>(3);
        foreach (var rune in decomposed.EnumerateRunes())
            parts.Add(rune.Value);

        return parts.Count > 1 ? [.. parts] : null;
    }

    private static bool HasAll(TrueTypeFont font, int[] codepoints)
    {
        foreach (var codepoint in codepoints)
        {
            if (font.GetGlyph(codepoint) == 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True for the characters that are drawn on the one before them. Fonts normally say which of their
    /// glyphs are marks; this answers the same question for those that do not.
    /// </summary>
    public static bool IsCombining(int codepoint) =>
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(codepoint) is
            System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.EnclosingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark;
}
