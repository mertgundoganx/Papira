using Papira.Elements;
using Papira.Rendering;

namespace Papira;

/// <summary>
/// A PDF document definition. The compose delegate is invoked on every generation, so a <see cref="Document"/>
/// can be generated many times, and different documents can be generated concurrently from multiple threads.
/// </summary>
public sealed class Document
{
    private readonly Action<DocumentDescriptor> _compose;
    private DocumentMetadata _metadata = new();
    private DocumentSettings _settings = new();

    private Document(Action<DocumentDescriptor> compose) => _compose = compose;

    public static Document Create(Action<DocumentDescriptor> compose) =>
        new(compose ?? throw new ArgumentNullException(nameof(compose)));

    public Document WithMetadata(DocumentMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        return this;
    }

    public Document WithSettings(DocumentSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        return this;
    }

    public byte[] GeneratePdf()
    {
        using var stream = new MemoryStream();
        GeneratePdf(stream);
        return stream.ToArray();
    }

    public void GeneratePdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        GeneratePdf(stream);
    }

    public void GeneratePdf(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var descriptor = new DocumentDescriptor();
        _compose(descriptor);
        if (descriptor.Pages.Count == 0)
            throw new DocumentComposeException("The document has no pages. Add one with document.Page(page => ...).");

        DocumentRenderer.Render(descriptor, _metadata, _settings, stream);
    }
}

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
