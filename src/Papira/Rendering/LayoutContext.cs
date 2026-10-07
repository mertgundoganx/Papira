namespace Papira.Rendering;

/// <summary>A position on a page, in PDF coordinates (origin at the bottom left).</summary>
internal readonly record struct Destination(int PageIndex, float X, float Y);

/// <summary>A clickable area on the current page that opens a URI or jumps to a section.</summary>
internal readonly record struct LinkArea(float Left, float Bottom, float Right, float Top, string? Uri, string? Section)
{
    /// <summary>The element of a tagged document this link belongs to, so that a reader can announce it.</summary>
    public StructureElement? Structure { get; init; }
}

internal readonly record struct Bookmark(string Title, int Level, Destination Destination);

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

    /// <summary>Corner radius applied to backgrounds, borders and gradients below a <c>CornerRadius</c> element.</summary>
    public float CornerRadius { get; set; }

    /// <summary>
    /// The structure of the document, when it is being tagged. Elements open a structure element around
    /// what they draw, so that a reader for the blind is given the meaning of the page and not its layout.
    /// </summary>
    public StructureTree? Structure { get; set; }

    /// <summary>
    /// Set while drawing page furniture — a header, a footer, a watermark. Such content is not part of
    /// what the document says, so it is marked as an artifact instead of being given a structure element.
    /// </summary>
    public bool Artifact { get; set; }

    /// <summary>
    /// Opens a structure element around what is drawn next, when the document is being tagged. With
    /// <paramref name="content"/> the element also holds the drawing itself; without it, it only holds
    /// the elements opened inside it, as a table holds its rows.
    /// </summary>
    public TagScope Tag(string role, string? alt = null, bool content = true) =>
        Structure is { } tree && !Artifact ? new TagScope(this, tree, role, alt, content) : default;

    /// <summary>The structure element that is open right now, if any.</summary>
    public StructureElement? CurrentStructure { get; set; }

    /// <summary>Height of the content area (between header and footer) of the current page.</summary>
    public float BodyHeight { get; set; } = float.MaxValue;

    /// <summary>
    /// Set while measuring something that is only as wide as what it holds. A box placed against its
    /// container is sized that way where nothing holds it to both sides, which is what CSS calls
    /// shrink-to-fit: a label in a corner is as wide as its words, not as wide as the corner.
    /// </summary>
    public bool ShrinkToFit { get; set; }

    /// <summary>
    /// The size of the whole page, which is what the viewport units of a style sheet refer to. In print
    /// a browser measures <c>vh</c> against the page box, not against a window.
    /// </summary>
    public Infrastructure.Size Viewport { get; set; } = new(595.28f, 841.89f);

    /// <summary>Link areas of the page being laid out; collected by the renderer after each page.</summary>
    public List<LinkArea> PageLinks { get; } = [];

    /// <summary>Form fields placed on the page being laid out; collected by the renderer after each page.</summary>
    public List<Elements.FormField> PageFormFields { get; } = [];

    /// <summary>Named sections (first occurrence wins), targets of internal links.</summary>
    public Dictionary<string, Destination> Sections { get; } = new(StringComparer.Ordinal);

    /// <summary>Document outline entries in document order.</summary>
    public List<Bookmark> Bookmarks { get; } = [];

    /// <summary>Fully resolved default text style in effect for the element being laid out.</summary>
    public TextStyle DefaultStyle { get; set; } = TextStyle.BuiltIn;
}

/// <summary>Keeps a structure element open for as long as the element that opened it is drawing.</summary>
internal readonly ref struct TagScope
{
    private readonly LayoutContext? _context;

    private readonly StructureElement? _previous;

    public TagScope(LayoutContext context, StructureTree tree, string role, string? alt, bool content)
    {
        _context = context;
        _previous = context.CurrentStructure;

        var element = tree.Push(role);
        element.Alt = alt;
        context.CurrentStructure = element;

        if (content)
            context.Canvas.BeginTagged(role, tree.NextMarkedContent(context.PageNumber - 1));
    }

    public void Dispose()
    {
        if (_context is not { Structure: { } tree })
            return;

        _context.Canvas.EndTagged();
        tree.Pop();
        _context.CurrentStructure = _previous;
    }
}
