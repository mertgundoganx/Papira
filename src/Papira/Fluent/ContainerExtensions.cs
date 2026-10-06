using Papira.Elements;
using Papira.Infrastructure;

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

    /// <summary>
    /// Says that the padding just added belongs to the box as a whole: a box that carries on over a page
    /// is padded where it begins and where it ends, not again at the fold. This is how CSS treats it.
    /// </summary>
    internal static IContainer PadAtTheEnds(this IContainer container)
    {
        if (container is PaddingElement padding)
            padding.AtTheEnds = true;

        return container;
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

    public static IContainer Width(this IContainer container, float value) => container.Constrain(width: CssLength.FromPoints(value));
    public static IContainer MinWidth(this IContainer container, float value) => container.Constrain(minWidth: CssLength.FromPoints(value));
    public static IContainer MaxWidth(this IContainer container, float value) => container.Constrain(maxWidth: CssLength.FromPoints(value));
    public static IContainer Height(this IContainer container, float value) => container.Constrain(height: CssLength.FromPoints(value));
    public static IContainer MinHeight(this IContainer container, float value) => container.Constrain(minHeight: CssLength.FromPoints(value));
    public static IContainer MaxHeight(this IContainer container, float value) => container.Constrain(maxHeight: CssLength.FromPoints(value));

    internal static ConstrainedElement Constrain(
        this IContainer container,
        CssLength? width = null,
        CssLength? minWidth = null,
        CssLength? maxWidth = null,
        CssLength? height = null,
        CssLength? minHeight = null,
        CssLength? maxHeight = null)
    {
        var element = container is ConstrainedElement existing && ReferenceEquals(existing.Child, EmptyElement.Instance)
            ? existing
            : container.Assign(new ConstrainedElement());

        if (width is { } a) element.Width = a;
        if (minWidth is { } b) element.MinWidth = b;
        if (maxWidth is { } c) element.MaxWidth = c;
        if (height is { } d) element.Height = d;
        if (minHeight is { } e) element.MinHeight = e;
        if (maxHeight is { } f) element.MaxHeight = f;
        return element;
    }

    public static IContainer Extend(this IContainer container) =>
        container.Assign(new ExtendElement { Horizontal = true, Vertical = true });

    public static IContainer ExtendHorizontal(this IContainer container) =>
        container.Assign(new ExtendElement { Horizontal = true });

    public static IContainer ExtendVertical(this IContainer container) =>
        container.Assign(new ExtendElement { Vertical = true });

    // ---- Transforms and effects ------------------------------------------------------------------

    /// <summary>Rotates the content around the centre of its area. The layout size does not change.</summary>
    public static IContainer Rotate(this IContainer container, float degrees) =>
        container.Assign(new RotateElement(degrees));

    /// <summary>Turns the content a quarter turn clockwise, swapping its width and height.</summary>
    public static IContainer RotateRight(this IContainer container) => container.Assign(new QuarterTurnElement(true));

    /// <summary>Turns the content a quarter turn counter-clockwise, swapping its width and height.</summary>
    public static IContainer RotateLeft(this IContainer container) => container.Assign(new QuarterTurnElement(false));

    /// <summary>Scales the content; its layout size scales with it.</summary>
    public static IContainer Scale(this IContainer container, float factor) => container.Scale(factor, factor);

    public static IContainer Scale(this IContainer container, float scaleX, float scaleY) =>
        container.Assign(new ScaleElement(scaleX, scaleY));

    /// <summary>Draws the content with the given opacity, from 0 (invisible) to 1 (opaque).</summary>
    public static IContainer Opacity(this IContainer container, float opacity) =>
        container.Assign(new OpacityElement(opacity));

    /// <summary>
    /// Rounds the corners of the backgrounds, borders and gradients inside it, and clips the content
    /// to the rounded rectangle. Rounded borders need the same width on all four sides.
    /// </summary>
    public static IContainer CornerRadius(this IContainer container, float radius) =>
        container.Assign(new CornerRadiusElement(radius));

    /// <summary>
    /// Fills the area with a linear gradient through the given colors.
    /// <paramref name="angleDegrees"/> 0 goes left to right, 90 top to bottom.
    /// </summary>
    public static IContainer BackgroundLinearGradient(this IContainer container, float angleDegrees, params Color[] colors) =>
        container.Assign(new GradientElement(false, angleDegrees, Stops(colors)));

    /// <summary>Fills the area with a radial gradient from its centre outwards.</summary>
    public static IContainer BackgroundRadialGradient(this IContainer container, params Color[] colors) =>
        container.Assign(new GradientElement(true, 0, Stops(colors)));

    private static Color[] Stops(Color[] colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Length < 2)
            throw new ArgumentException("A gradient needs at least two colors.", nameof(colors));
        return [.. colors];
    }

    /// <summary>
    /// Draws custom vector graphics in the element's area. The delegate receives the canvas and the
    /// available width and height in points. The element takes all the space it is given, so constrain
    /// it with <c>Width</c>/<c>Height</c> when it should be smaller.
    /// </summary>
    public static void Canvas(this IContainer container, Action<IDrawingCanvas, float, float> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        container.Assign(new DrawingElement(draw));
    }

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

    /// <summary>
    /// Marks the content as decoration: it is there to be looked at, not read, so a reader for the blind
    /// passes over it. Use it for a rule, a watermark or a picture the text beside it already describes.
    /// </summary>
    public static IContainer Decoration(this IContainer container) => container.Assign(new DecorationElement());

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

    /// <summary>A bulleted list; each <see cref="ListDescriptor.Item"/> can hold any content, including nested lists.</summary>
    public static void List(this IContainer container, Action<ListDescriptor> content) => container.AddList(content, numbered: false);

    /// <summary>A numbered list ("1.", "2.", …); see <see cref="ListDescriptor.StartAt"/> and <see cref="ListDescriptor.Marker"/>.</summary>
    public static void NumberedList(this IContainer container, Action<ListDescriptor> content) => container.AddList(content, numbered: true);

    private static void AddList(this IContainer container, Action<ListDescriptor> content, bool numbered)
    {
        var descriptor = new ListDescriptor(numbered);
        content(descriptor);
        container.Assign(descriptor.Build());
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

    /// <summary>
    /// Draws a QR code that fills the shorter side of the area, including the quiet zone required for scanning.
    /// Constrain it with <c>Width</c> or <c>Height</c> to set its size.
    /// </summary>
    public static void QrCode(this IContainer container, string data, QrErrorCorrection correction = QrErrorCorrection.Medium, Color? color = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(data);
        container.Assign(new QrCodeElement(data, correction, color ?? Colors.Black));
    }

    /// <summary>
    /// Draws a Code 128 barcode across the available width (printable ASCII only).
    /// It is 40 pt high unless the area is shorter.
    /// </summary>
    public static void Barcode(this IContainer container, string value, Color? color = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        container.Assign(new BarcodeElement(value, color ?? Colors.Black));
    }

    public static ImageDescriptor Image(this IContainer container, Image image) =>
        new(container.Assign(new ImageElement(image)));

    public static ImageDescriptor Image(this IContainer container, byte[] data) => container.Image(Papira.Image.FromBytes(data));

    public static ImageDescriptor Image(this IContainer container, string path) => container.Image(Papira.Image.FromFile(path));

    /// <summary>Draws a vector drawing, scaled to the available width by default.</summary>
    public static SvgDescriptor Svg(this IContainer container, SvgImage image) =>
        new(container.Assign(new SvgElement(image)));

    /// <summary>Reads an SVG file from disk and draws it; use <see cref="SvgImage.FromString"/> for inline markup.</summary>
    public static SvgDescriptor Svg(this IContainer container, string path) => container.Svg(SvgImage.FromFile(path));

    /// <summary>
    /// A field a reader types into, spanning the available width. The name identifies the field in the
    /// filled-in form, so it has to be unique within the document.
    /// </summary>
    public static TextFieldDescriptor TextField(this IContainer container, string name) =>
        new(container.Assign(new FormFieldElement(new FormField(FormFieldKind.Text, Named(name)))).Field);

    /// <summary>A box a reader ticks, as tall as the text around it.</summary>
    public static CheckboxDescriptor Checkbox(this IContainer container, string name) =>
        new(container.Assign(new FormFieldElement(new FormField(FormFieldKind.Checkbox, Named(name)))).Field);

    /// <summary>
    /// One button of a group, of which a reader picks exactly one. All the buttons of a group share the
    /// name; <paramref name="value"/> is what this one stands for in the filled-in form.
    /// </summary>
    public static RadioDescriptor Radio(this IContainer container, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var field = new FormField(FormFieldKind.Radio, Named(name)) { Export = value };
        return new RadioDescriptor(container.Assign(new FormFieldElement(field)).Field);
    }

    /// <summary>
    /// A place for a signature. Left as it is, it is a line to sign by hand and a field a reader can
    /// sign on screen; <see cref="DocumentSettings.Signature"/> signs it as the document is written.
    /// </summary>
    public static SignatureFieldDescriptor SignatureField(this IContainer container, string name) =>
        new(container.Assign(new FormFieldElement(new FormField(FormFieldKind.Signature, Named(name)))).Field);

    /// <summary>A list a reader picks one of the given options from.</summary>
    public static DropdownDescriptor Dropdown(this IContainer container, string name, params string[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length == 0)
            throw new ArgumentException("A dropdown needs at least one option.", nameof(options));
        if (Array.IndexOf(options, null) >= 0)
            throw new ArgumentException("An option of a dropdown cannot be null.", nameof(options));

        var field = new FormField(FormFieldKind.Choice, Named(name)) { Options = [.. options] };
        return new DropdownDescriptor(container.Assign(new FormFieldElement(field)).Field);
    }

    /// <summary>The name of a form field, which a PDF stores as a path and so cannot contain a dot.</summary>
    private static string Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('.', StringComparison.Ordinal))
            throw new ArgumentException($"The form field name '{name}' cannot contain a dot; a PDF reads dots as the separator of nested field names.", nameof(name));

        return name;
    }

    public static LineDescriptor LineHorizontal(this IContainer container, float thickness = 1) =>
        new(container.Assign(new LineElement(vertical: false, thickness)));

    public static LineDescriptor LineVertical(this IContainer container, float thickness = 1) =>
        new(container.Assign(new LineElement(vertical: true, thickness)));

    /// <summary>
    /// Lays out a piece of HTML: headings, paragraphs, lists, tables, pictures and links, styled by a
    /// <c>&lt;style&gt;</c> element, a <c>style</c> attribute or a style sheet passed in the options.
    /// The markup becomes ordinary Papira elements, so it paginates, and takes part in a tagged document,
    /// like anything else in the layout.
    /// </summary>
    public static void Html(this IContainer container, string html, Action<HtmlOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        var settings = new HtmlOptions();
        options?.Invoke(settings);

        // Magnifying the document means laying it out in a page as much narrower and drawing that larger,
        // which is what the scale of a print dialog does.
        if (settings.ZoomFactor != 1)
            container = container.Scale(settings.ZoomFactor);

        new Papira.Html.HtmlComposer(settings).Compose(container, Papira.Html.HtmlParser.Parse(html));
    }

    /// <summary>Reads an HTML file from disk and lays it out; pictures are resolved against its folder.</summary>
    public static void HtmlFile(this IContainer container, string path, Action<HtmlOptions>? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        container.Html(File.ReadAllText(path), settings =>
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
                settings.BaseDirectory(directory);

            options?.Invoke(settings);
        });
    }

    /// <summary>Composes content with a delegate; handy for extracting parts of a layout into methods.</summary>
    public static void Element(this IContainer container, Action<IContainer> compose) => compose(container);

    public static void Component(this IContainer container, IComponent component) => component.Compose(container);
}
