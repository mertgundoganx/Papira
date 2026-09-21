using Papira.Elements;

namespace Papira;

/// <summary>Reusable piece of layout, e.g. an address block used by several documents.</summary>
public interface IComponent
{
    void Compose(IContainer container);
}

public static class ContainerExtensions
{
    internal static T Assign<T>(this IContainer container, T element) where T : Element
    {
        if (container is not ContainerElement target)
            throw new ArgumentException("Containers must be created by Papira; custom IContainer implementations are not supported.", nameof(container));
        target.Child = element;
        return element;
    }

    // ---- Spacing ---------------------------------------------------------------------------------

    public static IContainer Padding(this IContainer container, float all) =>
        container.Padding(all, all, all, all);

    public static IContainer PaddingHorizontal(this IContainer container, float value) =>
        container.Padding(value, 0, value, 0);

    public static IContainer PaddingVertical(this IContainer container, float value) =>
        container.Padding(0, value, 0, value);

    public static IContainer PaddingLeft(this IContainer container, float value) => container.Padding(value, 0, 0, 0);
    public static IContainer PaddingTop(this IContainer container, float value) => container.Padding(0, value, 0, 0);
    public static IContainer PaddingRight(this IContainer container, float value) => container.Padding(0, 0, value, 0);
    public static IContainer PaddingBottom(this IContainer container, float value) => container.Padding(0, 0, 0, value);

    public static IContainer Padding(this IContainer container, float left, float top, float right, float bottom)
    {
        // Consecutive padding calls on the same element are merged instead of nesting wrappers.
        if (container is PaddingElement existing && ReferenceEquals(existing.Child, EmptyElement.Instance))
        {
            existing.Left += left;
            existing.Top += top;
            existing.Right += right;
            existing.Bottom += bottom;
            return existing;
        }

        return container.Assign(new PaddingElement { Left = left, Top = top, Right = right, Bottom = bottom });
    }

    // ---- Decoration ------------------------------------------------------------------------------

    public static IContainer Background(this IContainer container, Color color) =>
        container.Assign(new BackgroundElement(color));

    public static IContainer Border(this IContainer container, float width) => container.Border(width, width, width, width);
    public static IContainer BorderLeft(this IContainer container, float width) => container.Border(width, 0, 0, 0);
    public static IContainer BorderTop(this IContainer container, float width) => container.Border(0, width, 0, 0);
    public static IContainer BorderRight(this IContainer container, float width) => container.Border(0, 0, width, 0);
    public static IContainer BorderBottom(this IContainer container, float width) => container.Border(0, 0, 0, width);
    public static IContainer BorderHorizontal(this IContainer container, float width) => container.Border(0, width, 0, width);
    public static IContainer BorderVertical(this IContainer container, float width) => container.Border(width, 0, width, 0);

    public static IContainer Border(this IContainer container, float left, float top, float right, float bottom)
    {
        if (container is BorderElement existing && ReferenceEquals(existing.Child, EmptyElement.Instance))
        {
            existing.Left = Math.Max(existing.Left, left);
            existing.Top = Math.Max(existing.Top, top);
            existing.Right = Math.Max(existing.Right, right);
            existing.Bottom = Math.Max(existing.Bottom, bottom);
            return existing;
        }

        return container.Assign(new BorderElement { Left = left, Top = top, Right = right, Bottom = bottom });
    }

    /// <summary>Sets the color of the border created by the preceding <c>Border*</c> call.</summary>
    public static IContainer BorderColor(this IContainer container, Color color)
    {
        if (container is not BorderElement border)
            throw new DocumentComposeException("BorderColor must directly follow a Border call, e.g. .Border(1).BorderColor(Colors.Grey.Medium).");
        border.Color = color;
        return border;
    }

    // ---- Size ------------------------------------------------------------------------------------

    public static IContainer Width(this IContainer container, float value) => container.Constrain(minWidth: value, maxWidth: value);
    public static IContainer MinWidth(this IContainer container, float value) => container.Constrain(minWidth: value);
    public static IContainer MaxWidth(this IContainer container, float value) => container.Constrain(maxWidth: value);
    public static IContainer Height(this IContainer container, float value) => container.Constrain(minHeight: value, maxHeight: value);
    public static IContainer MinHeight(this IContainer container, float value) => container.Constrain(minHeight: value);
    public static IContainer MaxHeight(this IContainer container, float value) => container.Constrain(maxHeight: value);

    private static ConstrainedElement Constrain(this IContainer container, float? minWidth = null, float? maxWidth = null, float? minHeight = null, float? maxHeight = null)
    {
        var element = container is ConstrainedElement existing && ReferenceEquals(existing.Child, EmptyElement.Instance)
            ? existing
            : container.Assign(new ConstrainedElement());

        if (minWidth is { } a) element.MinWidth = a;
        if (maxWidth is { } b) element.MaxWidth = b;
        if (minHeight is { } c) element.MinHeight = c;
        if (maxHeight is { } d) element.MaxHeight = d;
        return element;
    }

    public static IContainer Extend(this IContainer container) =>
        container.Assign(new ExtendElement { Horizontal = true, Vertical = true });

    public static IContainer ExtendHorizontal(this IContainer container) =>
        container.Assign(new ExtendElement { Horizontal = true });

    public static IContainer ExtendVertical(this IContainer container) =>
        container.Assign(new ExtendElement { Vertical = true });

    // ---- Alignment -------------------------------------------------------------------------------

    public static IContainer AlignLeft(this IContainer container) => container.Align(HorizontalAlignment.Left, null);
    public static IContainer AlignCenter(this IContainer container) => container.Align(HorizontalAlignment.Center, null);
    public static IContainer AlignRight(this IContainer container) => container.Align(HorizontalAlignment.Right, null);
    public static IContainer AlignTop(this IContainer container) => container.Align(null, VerticalAlignment.Top);
    public static IContainer AlignMiddle(this IContainer container) => container.Align(null, VerticalAlignment.Middle);
    public static IContainer AlignBottom(this IContainer container) => container.Align(null, VerticalAlignment.Bottom);

    private static AlignmentElement Align(this IContainer container, HorizontalAlignment? horizontal, VerticalAlignment? vertical)
    {
        var element = container is AlignmentElement existing && ReferenceEquals(existing.Child, EmptyElement.Instance)
            ? existing
            : container.Assign(new AlignmentElement());

        element.Horizontal = horizontal ?? element.Horizontal;
        element.Vertical = vertical ?? element.Vertical;
        return element;
    }

    // ---- Paging ----------------------------------------------------------------------------------

    /// <summary>Never splits the content across pages; moves it to the next page as a whole instead.</summary>
    public static IContainer ShowEntire(this IContainer container) => container.Assign(new ShowEntireElement());

    /// <summary>
    /// Moves the content to the next page when less than <paramref name="minHeight"/> points are left on the current one,
    /// or when only a fragment shorter than that would fit. Use it to keep a heading together with the content that follows.
    /// The rule is ignored on a page where nothing else would fit.
    /// </summary>
    public static IContainer EnsureSpace(this IContainer container, float minHeight = 150) =>
        container.Assign(new EnsureSpaceElement(minHeight));

    public static void PageBreak(this IContainer container) => container.Assign(new PageBreakElement());

    // ---- Navigation ------------------------------------------------------------------------------

    private static readonly string[] BlockedSchemes = ["javascript", "vbscript", "data"];

    /// <summary>
    /// Makes the content a link to <paramref name="url"/> (an absolute URI such as <c>https://…</c>, <c>mailto:</c> or <c>tel:</c>).
    /// If the content is split across pages, each part is clickable.
    /// </summary>
    public static IContainer Hyperlink(this IContainer container, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Require an explicit scheme: on Unix, .NET would otherwise accept "/path" as a file URI.
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !Uri.CheckSchemeName(url[..colon]) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{url}' is not an absolute URI with a scheme, such as https://example.com.", nameof(url));
        if (BlockedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Links with the '{uri.Scheme}:' scheme are not allowed.", nameof(url));

        return container.Assign(LinkElement.ToUri(uri.AbsoluteUri));
    }

    /// <summary>Marks where the content starts, so <see cref="SectionLink"/> can jump to it. The first section with a name wins.</summary>
    public static IContainer Section(this IContainer container, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return container.Assign(new SectionElement(name));
    }

    /// <summary>Makes the content a link to the <see cref="Section"/> with the given name. Links to unknown sections are ignored.</summary>
    public static IContainer SectionLink(this IContainer container, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return container.Assign(LinkElement.ToSection(name));
    }

    /// <summary>
    /// Adds an entry to the document outline (the bookmarks panel of PDF viewers) that jumps to the content.
    /// <paramref name="level"/> 0 is a top-level entry; 1 nests under the previous level-0 entry, and so on.
    /// </summary>
    public static IContainer Bookmark(this IContainer container, string title, int level = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        return container.Assign(new BookmarkElement(title, level));
    }

    // ---- Text style ------------------------------------------------------------------------------

    public static IContainer DefaultTextStyle(this IContainer container, TextStyle style) =>
        container.Assign(new DefaultTextStyleElement(style));

    public static IContainer DefaultTextStyle(this IContainer container, Func<TextStyle, TextStyle> style) =>
        container.DefaultTextStyle(style(TextStyle.Default));

    // ---- Content ---------------------------------------------------------------------------------

    public static void Column(this IContainer container, Action<ColumnDescriptor> content)
    {
        var descriptor = new ColumnDescriptor();
        content(descriptor);
        container.Assign(descriptor.Element);
    }

    public static void Row(this IContainer container, Action<RowDescriptor> content)
    {
        var descriptor = new RowDescriptor();
        content(descriptor);
        container.Assign(descriptor.Element);
    }

    public static void Table(this IContainer container, Action<TableDescriptor> content)
    {
        var descriptor = new TableDescriptor();
        content(descriptor);
        container.Assign(descriptor.Element);
    }

    public static TextSpanDescriptor Text(this IContainer container, string? text)
    {
        var element = container.Assign(new TextElement());
        var span = new TextSpan { Text = text ?? string.Empty };
        element.Spans.Add(span);
        return new TextSpanDescriptor(element, span);
    }

    /// <summary>Adds text from <paramref name="value"/>.<c>ToString()</c>, formatted with the current culture.</summary>
    public static TextSpanDescriptor Text(this IContainer container, object? value) =>
        container.Text(value?.ToString());

    public static void Text(this IContainer container, Action<TextDescriptor> content)
    {
        var element = container.Assign(new TextElement());
        content(new TextDescriptor(element));
    }

    public static ImageDescriptor Image(this IContainer container, Image image) =>
        new(container.Assign(new ImageElement(image)));

    public static ImageDescriptor Image(this IContainer container, byte[] data) => container.Image(Papira.Image.FromBytes(data));

    public static ImageDescriptor Image(this IContainer container, string path) => container.Image(Papira.Image.FromFile(path));

    public static LineDescriptor LineHorizontal(this IContainer container, float thickness = 1) =>
        new(container.Assign(new LineElement(vertical: false, thickness)));

    public static LineDescriptor LineVertical(this IContainer container, float thickness = 1) =>
        new(container.Assign(new LineElement(vertical: true, thickness)));

    /// <summary>Composes content with a delegate; handy for extracting parts of a layout into methods.</summary>
    public static void Element(this IContainer container, Action<IContainer> compose) => compose(container);

    public static void Component(this IContainer container, IComponent component) => component.Compose(container);
}
