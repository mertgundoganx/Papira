namespace Papira.Text;

/// <summary>How a character joins to its neighbours in a cursive script, from the Unicode database.</summary>
internal enum JoiningType : byte
{
    /// <summary>Non joining.</summary>
    None,

    /// <summary>Joins only to the character on its left.</summary>
    Left,

    /// <summary>Joins only to the character on its right.</summary>
    Right,

    /// <summary>Joins on both sides.</summary>
    Dual,

    /// <summary>Joins its neighbours to each other without changing shape, like the tatweel.</summary>
    Causing,

    /// <summary>Skipped when neighbours are examined: combining marks and formatting characters.</summary>
    Transparent,
}

/// <summary>Which of the four cursive forms a letter takes.</summary>
internal enum JoiningForm : byte { Isolated, Initial, Medial, Final }

/// <summary>
/// Decides the cursive form of every character of a run, which the font then draws through its
/// isol, init, medi and fina features. The rules are those of the Unicode standard (section 9.2,
/// "Cursive Joining"): a character joins to a neighbour when both sides allow it.
/// </summary>
internal static class ArabicShaper
{
    /// <summary>True when the text contains a character of a script that joins cursively.</summary>
    public static bool NeedsShaping(ReadOnlySpan<int> text)
    {
        foreach (var codepoint in text)
        {
            if (codepoint >= 0x0600 && Bidi.JoiningOf(codepoint) is not (JoiningType.None or JoiningType.Transparent))
                return true;
        }

        return false;
    }

    /// <summary>Writes the form of each character of <paramref name="text"/> into <paramref name="forms"/>.</summary>
    public static void ComputeForms(ReadOnlySpan<int> text, Span<JoiningForm> forms)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var type = Bidi.JoiningOf(text[i]);
            if (type is JoiningType.Transparent)
            {
                // A mark keeps the form of the letter it sits on; it has no form of its own.
                forms[i] = JoiningForm.Isolated;
                continue;
            }

            var joinsPrevious = JoinsRight(Previous(text, i)) && JoinsLeft(type);
            var joinsNext = JoinsRight(type) && JoinsLeft(Next(text, i));

            forms[i] = (joinsPrevious, joinsNext) switch
            {
                (true, true) => JoiningForm.Medial,
                (true, false) => JoiningForm.Final,
                (false, true) => JoiningForm.Initial,
                _ => JoiningForm.Isolated,
            };
        }
    }

    /// <summary>The type of the previous character that is not transparent.</summary>
    private static JoiningType Previous(ReadOnlySpan<int> text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var type = Bidi.JoiningOf(text[i]);
            if (type != JoiningType.Transparent)
                return type;
        }

        return JoiningType.None;
    }

    private static JoiningType Next(ReadOnlySpan<int> text, int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            var type = Bidi.JoiningOf(text[i]);
            if (type != JoiningType.Transparent)
                return type;
        }

        return JoiningType.None;
    }

    /// <summary>True when the character can join the one that follows it in the text.</summary>
    private static bool JoinsRight(JoiningType type) => type is JoiningType.Dual or JoiningType.Left or JoiningType.Causing;

    /// <summary>True when the character can join the one that precedes it in the text.</summary>
    private static bool JoinsLeft(JoiningType type) => type is JoiningType.Dual or JoiningType.Right or JoiningType.Causing;
}
