using Papira.Elements;
using Papira.Rendering;

namespace Papira;

/// <summary>
/// A document waiting to be written. What it holds is described by a delegate rather than built once, and
/// that delegate runs again on every generation — so the same document may be written as often as you
/// like, and from as many threads at once as you like, as long as the delegate itself can be run that way.
/// Nothing is written until one of the <c>GeneratePdf</c> methods is called.
/// </summary>
/// <example>
/// <code>
/// var pdf = Document.Create(document => document.Page(page =>
/// {
///     page.Size(PageSizes.A4).Margin(40);
///     page.Content().Text("Merhaba");
/// })).GeneratePdf();
/// </code>
/// </example>
public sealed class Document
{
    private readonly Action<DocumentDescriptor> _compose;
    private readonly List<DocumentAttachment> _attachments = [];
    private DocumentMetadata _metadata = new();
    private DocumentSettings _settings = new();

    private Document(Action<DocumentDescriptor> compose) => _compose = compose;

    /// <summary>Describes a document. The delegate runs again every time the document is written.</summary>
    public static Document Create(Action<DocumentDescriptor> compose) =>
        new(compose ?? throw new ArgumentNullException(nameof(compose)));

    /// <summary>What the file says about itself: its title, its author, the language it is written in.</summary>
    public Document WithMetadata(DocumentMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        return this;
    }

    /// <summary>
    /// Embeds a file in the document, for example the machine-readable XML of an invoice.
    /// Readers show attachments in their attachments panel.
    /// </summary>
    public Document WithAttachment(DocumentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _attachments.Add(attachment);
        return this;
    }

    /// <summary>Embeds a file with its contents read from <paramref name="data"/>.</summary>
    public Document WithAttachment(string fileName, byte[] data, string mediaType = "application/octet-stream") =>
        WithAttachment(new DocumentAttachment(fileName, data) { MediaType = mediaType });

    /// <summary>What the file is: the standard it conforms to, whether it is signed, locked or tagged.</summary>
    public Document WithSettings(DocumentSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        return this;
    }

    /// <summary>Writes the document and gives back the bytes of the file.</summary>
    /// <exception cref="DocumentComposeException">The document is described in a way that cannot be built.</exception>
    /// <exception cref="DocumentLayoutException">The content cannot be laid out on the pages it was given.</exception>
    public byte[] GeneratePdf()
    {
        using var stream = new MemoryStream();
        GeneratePdf(stream);
        return stream.ToArray();
    }

    /// <summary>Writes the document to a file, replacing whatever was there.</summary>
    /// <exception cref="DocumentComposeException">The document is described in a way that cannot be built.</exception>
    /// <exception cref="DocumentLayoutException">The content cannot be laid out on the pages it was given.</exception>
    public void GeneratePdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        GeneratePdf(stream);
    }

    /// <summary>Writes the document to <paramref name="stream"/>, which is not closed.</summary>
    /// <exception cref="DocumentComposeException">The document is described in a way that cannot be built.</exception>
    /// <exception cref="DocumentLayoutException">The content cannot be laid out on the pages it was given.</exception>
    public void GeneratePdf(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var descriptor = new DocumentDescriptor();
        _compose(descriptor);
        if (descriptor.Pages.Count == 0)
            throw new DocumentComposeException("The document has no pages. Add one with document.Page(page => ...).");

        DocumentRenderer.Render(descriptor, _metadata, _settings, _attachments, stream);
    }
}

/// <summary>
/// The document being described: the pages it holds and the text style they start from.
/// </summary>
public sealed class DocumentDescriptor
{
    internal List<PageDescriptor> Pages { get; } = [];
    internal TextStyle Style { get; private set; } = TextStyle.Default;

    /// <summary>Default text style for all pages.</summary>
    public DocumentDescriptor DefaultTextStyle(TextStyle style)
    {
        Style = style;
        return this;
    }

    public DocumentDescriptor DefaultTextStyle(Func<TextStyle, TextStyle> style) => DefaultTextStyle(style(TextStyle.Default));

    /// <summary>
    /// Adds a page template. Its content flows over as many physical pages as needed;
    /// call again to start a new section with a different page setup.
    /// </summary>
    public DocumentDescriptor Page(Action<PageDescriptor> page)
    {
        var descriptor = new PageDescriptor();
        page(descriptor);
        Pages.Add(descriptor);
        return this;
    }
}

/// <summary>
/// One page of a document, or rather one run of them: its size and margins, the content that flows
/// through as many pages as it needs, and the header, footer, background and foreground that are drawn
/// on each of them.
/// </summary>
public sealed class PageDescriptor
{
    internal PageSize PageSize { get; private set; } = PageSizes.A4;
    internal float LeftMargin, TopMargin, RightMargin, BottomMargin;
    internal Color? BackgroundColor { get; private set; }
    internal TextStyle Style { get; private set; } = TextStyle.Default;

    internal Slot BackgroundSlot { get; } = new();
    internal Slot ForegroundSlot { get; } = new();
    internal Slot HeaderSlot { get; } = new();
    internal Slot ContentSlot { get; } = new();
    internal Slot FooterSlot { get; } = new();

    public PageDescriptor Size(PageSize size)
    {
        PageSize = size;
        return this;
    }

    public PageDescriptor Size(float width, float height) => Size(new PageSize(width, height));

    public PageDescriptor Margin(float value) => Margin(value, value, value, value);
    public PageDescriptor MarginHorizontal(float value) => Margin(value, TopMargin, value, BottomMargin);
    public PageDescriptor MarginVertical(float value) => Margin(LeftMargin, value, RightMargin, value);
    public PageDescriptor MarginLeft(float value) => Margin(value, TopMargin, RightMargin, BottomMargin);
    public PageDescriptor MarginTop(float value) => Margin(LeftMargin, value, RightMargin, BottomMargin);
    public PageDescriptor MarginRight(float value) => Margin(LeftMargin, TopMargin, value, BottomMargin);
    public PageDescriptor MarginBottom(float value) => Margin(LeftMargin, TopMargin, RightMargin, value);

    public PageDescriptor Margin(float left, float top, float right, float bottom)
    {
        (LeftMargin, TopMargin, RightMargin, BottomMargin) = (left, top, right, bottom);
        return this;
    }

    public PageDescriptor PageColor(Color color)
    {
        BackgroundColor = color;
        return this;
    }

    public PageDescriptor DefaultTextStyle(TextStyle style)
    {
        Style = style;
        return this;
    }

    public PageDescriptor DefaultTextStyle(Func<TextStyle, TextStyle> style) => DefaultTextStyle(style(TextStyle.Default));

    /// <summary>Repeated at the top of every page.</summary>
    public IContainer Header() => HeaderSlot;

    /// <summary>Main content; flows across pages.</summary>
    public IContainer Content() => ContentSlot;

    /// <summary>Repeated at the bottom of every page.</summary>
    public IContainer Footer() => FooterSlot;

    /// <summary>Full-page layer drawn behind everything else on every page (ignores margins).</summary>
    public IContainer Background() => BackgroundSlot;

    /// <summary>Full-page layer drawn above everything else on every page, e.g. a watermark (ignores margins).</summary>
    public IContainer Foreground() => ForegroundSlot;
}
