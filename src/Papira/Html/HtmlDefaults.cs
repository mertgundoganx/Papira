namespace Papira.Html;

/// <summary>
/// What each element looks like before any style sheet says otherwise: the part of a browser's own
/// style sheet that a printed document needs. Sizes are in em, so they follow the text around them.
/// </summary>
internal static class HtmlDefaults
{
    private static readonly Dictionary<string, (string Property, string Value)[]> Rules = new(StringComparer.Ordinal)
    {
        ["h1"] = [("font-size", "2em"), ("font-weight", "bold"), ("margin", "0.67em 0")],
        ["h2"] = [("font-size", "1.5em"), ("font-weight", "bold"), ("margin", "0.83em 0")],
        ["h3"] = [("font-size", "1.17em"), ("font-weight", "bold"), ("margin", "1em 0")],
        ["h4"] = [("font-weight", "bold"), ("margin", "1.33em 0")],
        ["h5"] = [("font-size", "0.83em"), ("font-weight", "bold"), ("margin", "1.67em 0")],
        ["h6"] = [("font-size", "0.67em"), ("font-weight", "bold"), ("margin", "2.33em 0")],
        ["p"] = [("margin", "1em 0")],
        ["blockquote"] = [("margin", "1em 40px")],
        ["figure"] = [("margin", "1em 40px")],
        ["dl"] = [("margin", "1em 0")],
        ["dd"] = [("margin-left", "40px")],
        ["dt"] = [("font-weight", "bold")],
        ["ul"] = [("margin", "1em 0"), ("padding-left", "24px")],
        ["ol"] = [("margin", "1em 0"), ("padding-left", "24px")],
        ["pre"] = [("margin", "1em 0"), ("font-family", "monospace"), ("white-space", "pre")],
        ["hr"] = [("margin", "0.5em 0")],
        ["b"] = [("font-weight", "bold")],
        ["strong"] = [("font-weight", "bold")],
        ["th"] = [("font-weight", "bold"), ("text-align", "center")],
        ["i"] = [("font-style", "italic")],
        ["em"] = [("font-style", "italic")],
        ["cite"] = [("font-style", "italic")],
        ["var"] = [("font-style", "italic")],
        ["address"] = [("font-style", "italic")],
        ["u"] = [("text-decoration", "underline")],
        ["ins"] = [("text-decoration", "underline")],
        ["s"] = [("text-decoration", "line-through")],
        ["strike"] = [("text-decoration", "line-through")],
        ["del"] = [("text-decoration", "line-through")],
        ["small"] = [("font-size", "0.83em")],
        ["big"] = [("font-size", "1.17em")],
        ["sub"] = [("font-size", "0.83em")],
        ["sup"] = [("font-size", "0.83em")],
        ["code"] = [("font-family", "monospace")],
        ["kbd"] = [("font-family", "monospace")],
        ["samp"] = [("font-family", "monospace")],
        ["tt"] = [("font-family", "monospace")],
        ["mark"] = [("background-color", "#ffff00")],
        ["center"] = [("text-align", "center")],
        ["a"] = [("color", "#0645ad"), ("text-decoration", "underline")],
    };

    public static (string Property, string Value)[] For(string tag) => Rules.GetValueOrDefault(tag, []);
}
