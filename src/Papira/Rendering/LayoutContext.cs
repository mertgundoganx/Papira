namespace Papira.Rendering;

/// <summary>State shared by all elements while a document is laid out.</summary>
internal sealed class LayoutContext(Canvas canvas)
{
    public Canvas Canvas { get; } = canvas;

    /// <summary>1-based number of the page being laid out.</summary>
    public int PageNumber { get; set; }

    /// <summary>Total page count; only known in the second pass of documents that display it.</summary>
    public int TotalPages { get; set; }

    /// <summary>Set by text elements that display the total page count; triggers a second layout pass.</summary>
    public bool TotalPagesRequested { get; set; }

    /// <summary>
    /// Set while drawing something that occupies space but has no content of its own: a cell whose content finished
    /// on a previous page while its row continues, or a fixed-size box without content. Backgrounds and borders
    /// are still drawn, so rows keep their frame and sized boxes stay visible.
    /// </summary>
    public bool DrawEmptyDecorations { get; set; }

    /// <summary>
    /// Set when the content of an otherwise empty page would not fit because of keep-together rules
    /// (<c>EnsureSpace</c>); the rules are then ignored for that page instead of failing the layout.
    /// </summary>
    public bool RelaxKeepTogether { get; set; }

    /// <summary>Height of the content area (between header and footer) of the current page.</summary>
    public float BodyHeight { get; set; } = float.MaxValue;

    /// <summary>Fully resolved default text style in effect for the element being laid out.</summary>
    public TextStyle DefaultStyle { get; set; } = TextStyle.BuiltIn;
}
