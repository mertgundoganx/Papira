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
