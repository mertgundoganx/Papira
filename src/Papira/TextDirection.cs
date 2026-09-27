namespace Papira;

/// <summary>The direction a paragraph of text is laid out in.</summary>
public enum TextDirection : byte
{
    /// <summary>
    /// Taken from the first strongly directional character of the paragraph, as a browser does:
    /// Arabic or Hebrew text becomes right to left, everything else left to right.
    /// </summary>
    Auto,

    LeftToRight,

    RightToLeft,
}
