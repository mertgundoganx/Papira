namespace Papira.Html;

/// <summary>
/// One node of a parsed document: an element with its attributes and children, or a piece of text.
/// </summary>
internal sealed class HtmlNode
{
    /// <summary>The tag name in lower case; empty for a piece of text.</summary>
    public required string Tag { get; init; }

    public string Text { get; init; } = string.Empty;

    public Dictionary<string, string>? Attributes { get; set; }

    public List<HtmlNode> Children { get; } = [];

    /// <summary>
    /// The element as it was written, for the elements Papira does not read as markup — a drawing, whose
    /// own language tells capital letters from small ones where markup does not.
    /// </summary>
    public string? Raw { get; set; }

    /// <summary>The element this one stands in, so that a rule can ask where among the others it stands.</summary>
    public HtmlNode? Parent { get; set; }

    /// <summary>Which element of its parent this is, counting from one, and how many there are in all.</summary>
    public int Index { get; set; }

    public int SiblingCount { get; set; }

    /// <summary>True once the children of this element have been counted.</summary>
    public bool Numbered { get; set; }

    /// <summary>Adds a child and remembers where it was added, which a selector may ask about.</summary>
    public void Add(HtmlNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public bool IsText => Tag.Length == 0;

    public static HtmlNode Element(string tag) => new() { Tag = tag };

    public static HtmlNode TextNode(string text) => new() { Tag = string.Empty, Text = text };

    public string? Attribute(string name) =>
        Attributes != null && Attributes.TryGetValue(name, out var value) ? value : null;

    /// <summary>The classes of the element, for matching style rules.</summary>
    public string[] Classes =>
        Attribute("class") is { Length: > 0 } list
            ? list.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
}
