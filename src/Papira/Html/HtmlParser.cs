using System.Globalization;
using System.Text;

namespace Papira.Html;

/// <summary>
/// Reads HTML into a tree. Real-world markup is rarely well formed, so the parser is forgiving in the
/// ways browsers are: tags may be left open, close tags that match nothing are dropped, and attribute
/// values need no quotes. It is not a full HTML5 tree builder — it does not move misplaced content
/// around, as a browser does for a table with text in it — but it reads the markup people write.
/// </summary>
internal static class HtmlParser
{
    private const int MaxNodes = 100_000;
    private const int MaxDepth = 64;
    private const int MaxLength = 16 * 1024 * 1024;

    /// <summary>Elements that never have content.</summary>
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Elements whose content is text, whatever it looks like.</summary>
    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "title", "textarea" };

    /// <summary>Elements that close an open paragraph when they start.</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "fieldset", "figcaption", "figure", "footer",
        "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section",
        "table", "ul",
    };

    /// <summary>Which open elements a start tag closes by itself.</summary>
    private static readonly Dictionary<string, string[]> ClosesOthers = new(StringComparer.Ordinal)
    {
        ["li"] = ["li"],
        ["dt"] = ["dt", "dd"],
        ["dd"] = ["dt", "dd"],
        ["tr"] = ["td", "th", "tr"],
        ["td"] = ["td", "th"],
        ["th"] = ["td", "th"],
        ["thead"] = ["td", "th", "tr"],
        ["tbody"] = ["td", "th", "tr", "thead"],
        ["tfoot"] = ["td", "th", "tr", "tbody", "thead"],
        ["option"] = ["option"],
    };

    /// <summary>Parses a document or a fragment; the result is always a single root node.</summary>
    public static HtmlNode Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > MaxLength)
            throw new InvalidDataException($"The HTML is {html.Length / (1024 * 1024)} MB; Papira reads at most {MaxLength / (1024 * 1024)} MB.");

        var root = HtmlNode.Element("#document");
        var open = new List<HtmlNode> { root };
        var nodes = 0;
        var i = 0;

        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                var next = html.IndexOf('<', i);
                var end = next < 0 ? html.Length : next;
                AddText(open[^1], html[i..end], ref nodes);
                i = end;
                continue;
            }

            if (Skip(html, ref i, "<!--", "-->") || Skip(html, ref i, "<![CDATA[", "]]>"))
                continue;

            if (i + 1 < html.Length && html[i + 1] == '!')
            {
                // A doctype or another declaration; it says nothing about the content.
                var close = html.IndexOf('>', i);
                i = close < 0 ? html.Length : close + 1;
                continue;
            }

            if (i + 1 < html.Length && html[i + 1] == '/')
            {
                var name = ReadName(html, i + 2, out var after);
                i = SkipToEnd(html, after);
                if (name.Length > 0)
                    Close(open, name);

                continue;
            }

            if (i + 1 >= html.Length || !char.IsAsciiLetter(html[i + 1]))
            {
                // A stray "<" that starts no tag is text.
                AddText(open[^1], "<", ref nodes);
                i++;
                continue;
            }

            var tag = ReadName(html, i + 1, out var attributeStart);
            var element = HtmlNode.Element(tag);
            i = ReadAttributes(html, attributeStart, element, out var selfClosing);

            if (++nodes > MaxNodes)
                throw new InvalidDataException($"The HTML has more than {MaxNodes} elements.");

            ImplicitClose(open, tag);
            open[^1].Children.Add(element);

            if (RawText.Contains(tag))
            {
                var text = ReadRawText(html, ref i, tag);
                if (tag is not ("script" or "textarea"))
                    element.Children.Add(HtmlNode.TextNode(text));

                continue;
            }

            if (selfClosing || Void.Contains(tag))
                continue;

            if (open.Count >= MaxDepth)
                throw new InvalidDataException($"The HTML is nested more than {MaxDepth} levels deep.");

            open.Add(element);
        }

        return root;
    }

    private static void AddText(HtmlNode parent, string text, ref int nodes)
    {
        if (text.Length == 0)
            return;

        if (++nodes > MaxNodes)
            throw new InvalidDataException($"The HTML has more than {MaxNodes} elements.");

        parent.Children.Add(HtmlNode.TextNode(Decode(text)));
    }

    private static bool Skip(string html, ref int i, string opening, string closing)
    {
        if (!html.AsSpan(i).StartsWith(opening, StringComparison.Ordinal))
            return false;

        var end = html.IndexOf(closing, i + opening.Length, StringComparison.Ordinal);
        i = end < 0 ? html.Length : end + closing.Length;
        return true;
    }

    private static string ReadName(string html, int start, out int after)
    {
        var i = start;
        while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is '-' or ':' or '_'))
            i++;

        after = i;
        return html[start..i].ToLowerInvariant();
    }

    private static int SkipToEnd(string html, int i)
    {
        var close = html.IndexOf('>', i);
        return close < 0 ? html.Length : close + 1;
    }

    private static int ReadAttributes(string html, int i, HtmlNode element, out bool selfClosing)
    {
        selfClosing = false;
        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;

            if (i >= html.Length)
                break;

            if (html[i] == '>')
                return i + 1;

            if (html[i] == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }

            var name = ReadName(html, i, out var after);
            if (name.Length == 0)
            {
                // Something that is not an attribute name; step over it rather than looping forever.
                i++;
                continue;
            }

            i = after;
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;

            var value = string.Empty;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                    i++;

                if (i < html.Length && (html[i] == '"' || html[i] == '\''))
                {
                    var quote = html[i++];
                    var end = html.IndexOf(quote, i);
                    if (end < 0)
                        end = html.Length;

                    value = html[i..end];
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    var start = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                        i++;

                    value = html[start..i];
                }
            }

            element.Attributes ??= new Dictionary<string, string>(StringComparer.Ordinal);
            element.Attributes[name] = Decode(value);
        }

        return i;
    }

    private static string ReadRawText(string html, ref int i, string tag)
    {
        var start = i;
        var closing = "</" + tag;
        var end = html.IndexOf(closing, i, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            i = html.Length;
            return html[start..];
        }

        i = SkipToEnd(html, end);
        return html[start..end];
    }

    /// <summary>Closes the elements a start tag ends by itself, such as one list item before the next.</summary>
    private static void ImplicitClose(List<HtmlNode> open, string tag)
    {
        if (ClosesParagraph.Contains(tag))
        {
            while (open.Count > 1 && open[^1].Tag == "p")
                open.RemoveAt(open.Count - 1);
        }

        if (!ClosesOthers.TryGetValue(tag, out var closes))
            return;

        while (open.Count > 1 && Array.IndexOf(closes, open[^1].Tag) >= 0)
            open.RemoveAt(open.Count - 1);
    }

    /// <summary>Closes an element and everything left open inside it; a close tag that matches nothing is dropped.</summary>
    private static void Close(List<HtmlNode> open, string tag)
    {
        for (var i = open.Count - 1; i > 0; i--)
        {
            if (open[i].Tag != tag)
                continue;

            open.RemoveRange(i, open.Count - i);
            return;
        }
    }

    /// <summary>The character references HTML documents use, by name and by number.</summary>
    private static readonly Dictionary<string, string> Entities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™", ["hellip"] = "…", ["mdash"] = "—", ["ndash"] = "–",
        ["lsquo"] = "‘", ["rsquo"] = "’", ["ldquo"] = "“", ["rdquo"] = "”", ["bull"] = "•",
        ["middot"] = "·", ["times"] = "×", ["divide"] = "÷", ["deg"] = "°", ["euro"] = "€", ["pound"] = "£",
        ["yen"] = "¥", ["cent"] = "¢", ["sect"] = "§", ["para"] = "¶", ["laquo"] = "«", ["raquo"] = "»",
        ["dagger"] = "†", ["permil"] = "‰", ["plusmn"] = "±", ["frac12"] = "½", ["frac14"] = "¼", ["sup2"] = "²",
        ["sup3"] = "³", ["micro"] = "µ", ["ordm"] = "º", ["shy"] = "­", ["ensp"] = " ", ["emsp"] = " ",
        ["thinsp"] = " ", ["larr"] = "←", ["rarr"] = "→", ["harr"] = "↔", ["check"] = "✓", ["star"] = "★",
    };

    /// <summary>Replaces character references; anything that is not one is left as it stands.</summary>
    public static string Decode(string text)
    {
        var start = text.IndexOf('&', StringComparison.Ordinal);
        if (start < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (start >= 0)
        {
            builder.Append(text, index, start - index);
            var end = text.IndexOf(';', start + 1);

            // A reference is short; an "&" with no semicolon near it is just an ampersand.
            if (end < 0 || end - start > 12)
            {
                builder.Append('&');
                index = start + 1;
            }
            else
            {
                var name = text[(start + 1)..end];
                if (Resolve(name) is { } resolved)
                {
                    builder.Append(resolved);
                    index = end + 1;
                }
                else
                {
                    builder.Append('&');
                    index = start + 1;
                }
            }

            start = text.IndexOf('&', index);
        }

        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    private static string? Resolve(string name)
    {
        if (name.Length == 0)
            return null;

        if (name[0] != '#')
            return Entities.GetValueOrDefault(name);

        var digits = name[1..];
        var hex = digits.Length > 0 && (digits[0] == 'x' || digits[0] == 'X');
        if (hex)
            digits = digits[1..];

        var style = hex ? NumberStyles.HexNumber : NumberStyles.None;
        if (!int.TryParse(digits, style, CultureInfo.InvariantCulture, out var codepoint) ||
            codepoint <= 0 || codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF))
        {
            return null;
        }

        return char.ConvertFromUtf32(codepoint);
    }
}
