using System.Text;
using Papira.Svg;

namespace Papira;

/// <summary>
/// How a piece of HTML is read: where its pictures are, which style sheets apply to it, and the text
/// it starts from.
/// </summary>
public sealed class HtmlOptions
{
    private readonly List<string> _styleSheets = [];
    private readonly Dictionary<string, object> _resources = new(StringComparer.Ordinal);

    /// <summary>
    /// What has already been read for a source. A template that shows the same picture several times —
    /// a logo at the top of every section — reads it once and embeds it once.
    /// </summary>
    private readonly Dictionary<string, object?> _loaded = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> StyleSheets => _styleSheets;

    internal string? Directory { get; private set; }

    internal float BaseFontSize { get; private set; } = 12;

    internal float ZoomFactor { get; private set; } = 1;

    /// <summary>How wide the page is, which is what a style sheet's "@media (min-width: …)" is answered with.</summary>
    internal float Width { get; private set; } = 595.28f;

    internal Papira.Html.RemoteImages? Remote { get; private set; }

    internal string? FontFamily { get; private set; }

    internal Color? TextColor { get; private set; }

    /// <summary>
    /// The folder the <c>src</c> of a picture is resolved against. Without it, only pictures given
    /// as a data URI or registered with <see cref="Resource(string, Image)"/> can be drawn.
    /// </summary>
    public HtmlOptions BaseDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory = path;
        return this;
    }

    /// <summary>
    /// Lets the pictures of the markup be fetched over http and https. They are all fetched at once,
    /// before the document is laid out, and a picture that does not arrive is left out — a page that
    /// cannot be reached never stops the document being written.
    /// <para>
    /// Addresses on the machine itself and on its own network are refused unless
    /// <paramref name="allowPrivateNetworks"/> says otherwise, so that markup from elsewhere cannot read
    /// what only this machine can reach. Turn this on for markup you trust.
    /// </para>
    /// </summary>
    /// <param name="timeout">How long one picture may take. Default: ten seconds.</param>
    /// <param name="maximumBytes">How large one picture may be. Default: 16 MB.</param>
    /// <param name="allowPrivateNetworks">Whether addresses inside the machine's own network may be fetched.</param>
    public HtmlOptions AllowRemoteImages(TimeSpan? timeout = null, int maximumBytes = 16 * 1024 * 1024, bool allowPrivateNetworks = false)
    {
        Fetcher(timeout, maximumBytes, allowPrivateNetworks).Images = true;
        return this;
    }

    /// <summary>
    /// Lets the style sheets a document links to be fetched over http and https, as
    /// <see cref="AllowRemoteImages"/> lets its pictures be. A sheet that does not arrive is left out and
    /// the document is laid out without it. Turn this on for markup you trust.
    /// </summary>
    /// <param name="timeout">How long one sheet may take. Default: ten seconds.</param>
    /// <param name="maximumBytes">How large one sheet may be. Default: 4 MB.</param>
    /// <param name="allowPrivateNetworks">Whether addresses inside the machine's own network may be fetched.</param>
    public HtmlOptions AllowRemoteStyleSheets(TimeSpan? timeout = null, int maximumBytes = 4 * 1024 * 1024, bool allowPrivateNetworks = false)
    {
        Fetcher(timeout, maximumBytes, allowPrivateNetworks).Styles = true;
        return this;
    }

    /// <summary>
    /// How wide the page the markup is laid out on is, in points. A style sheet may say that some of its
    /// rules only apply to a page of a certain width, and this is what such a rule is answered with; the
    /// zoom is taken into account by itself. Default: the width of an A4 page.
    /// </summary>
    public HtmlOptions PageWidth(float points)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(points);
        Width = points;
        return this;
    }

    private Papira.Html.RemoteImages Fetcher(TimeSpan? timeout, int maximumBytes, bool allowPrivateNetworks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        if (wait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");

        return Remote = new Papira.Html.RemoteImages(wait, maximumBytes, allowPrivateNetworks)
        {
            Images = Remote?.Images ?? false,
            Styles = Remote?.Styles ?? false,
        };
    }

    /// <summary>
    /// A style sheet that applies to the document, as if it stood in a <c>&lt;style&gt;</c> element
    /// before it. Can be called several times; later sheets win ties.
    /// </summary>
    public HtmlOptions StyleSheet(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        _styleSheets.Add(css);
        return this;
    }

    /// <summary>
    /// The size, in points, that <c>1em</c> refers to at the top of the document. Text that states no
    /// size of its own is left to the style of the document around it; this only scales relative sizes.
    /// Default: 12.
    /// </summary>
    public HtmlOptions FontSize(float points)
    {
        if (points <= 0)
            throw new ArgumentOutOfRangeException(nameof(points), "The font size must be positive.");

        BaseFontSize = points;
        return this;
    }

    /// <summary>
    /// Magnifies the whole document, as the scale of a browser's print dialog does: everything is drawn
    /// <paramref name="factor"/> times as large, and the markup is laid out in a page that much narrower,
    /// so the same template fills the page the same way. Default: 1.
    /// </summary>
    public HtmlOptions Zoom(float factor)
    {
        if (factor is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(factor), "The zoom factor must be greater than zero.");

        ZoomFactor = factor;
        return this;
    }

    /// <summary>The font family the document starts from, unless its own styles say otherwise.</summary>
    public HtmlOptions DefaultFontFamily(string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        FontFamily = family;
        return this;
    }

    /// <summary>The colour the text starts from, unless the document's own styles say otherwise.</summary>
    public HtmlOptions DefaultFontColor(Color color)
    {
        TextColor = color;
        return this;
    }

    /// <summary>
    /// A picture the markup refers to by name, so that a template can be filled with pictures that are
    /// already in memory: <c>&lt;img src="logo"&gt;</c> draws the image registered as "logo".
    /// </summary>
    public HtmlOptions Resource(string name, Image image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(image);
        _resources[name] = image;
        return this;
    }

    /// <summary>A vector drawing the markup refers to by name; see <see cref="Resource(string, Image)"/>.</summary>
    public HtmlOptions Resource(string name, SvgImage drawing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(drawing);
        _resources[name] = drawing;
        return this;
    }

    /// <summary>How large a style sheet beside the markup may be.</summary>
    private const int MaximumStyleSheet = 8 * 1024 * 1024;

    /// <summary>
    /// The style sheet a document links to: a file beside the markup, a data URI, or one fetched over
    /// the network where the document says it may be. Null where there is none to be had, which lays
    /// the document out without it rather than not at all.
    /// </summary>
    internal string? LoadStyleSheet(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        return Read<string>(() =>
        {
            if (TryDataUri(href, out _, out var data))
                return System.Text.Encoding.UTF8.GetString(data);

            if (Papira.Html.RemoteImages.IsRemote(href))
            {
                return Remote is { Styles: true } remote && remote.Get(href) is { } fetched
                    ? System.Text.Encoding.UTF8.GetString(fetched).TrimStart('\uFEFF')
                    : null;
            }

            return Path(href) is { } path && new FileInfo(path) is { Exists: true, Length: <= MaximumStyleSheet }
                ? File.ReadAllText(path)
                : null;
        });
    }

    /// <summary>The drawing a picture source refers to, if it is a vector drawing.</summary>
    internal SvgImage? LoadSvg(string source)
    {
        if (_loaded.TryGetValue("svg:" + source, out var already))
            return already as SvgImage;

        var drawing = ReadSvg(source);
        _loaded["svg:" + source] = drawing;
        return drawing;
    }

    private SvgImage? ReadSvg(string source)
    {
        if (_resources.TryGetValue(source, out var registered))
            return registered as SvgImage;

        return Read<SvgImage>(() =>
        {
            if (TryDataUri(source, out var mediaType, out var data))
                return mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase) ? SvgImage.FromBytes(data) : null;

            if (Remote is { Images: true } remote && Papira.Html.RemoteImages.IsRemote(source))
                return remote.Get(source) is { } fetched && LooksLikeSvg(fetched) ? SvgImage.FromBytes(fetched) : null;

            return Path(source) is { } path && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? SvgImage.FromFile(path)
                : null;
        });
    }

    /// <summary>
    /// The picture a source refers to: a registered one, a data URI, a file, or one fetched over the
    /// network. Null where it cannot be read, which leaves it out of the document — a picture that is
    /// missing, broken or pointed at by an address that leads nowhere is skipped, as a browser skips it,
    /// because the address of a picture is often data and data is often wrong.
    /// </summary>
    internal Image? LoadImage(string source)
    {
        if (_loaded.TryGetValue(source, out var already))
            return already as Image;

        var image = ReadImage(source);
        _loaded[source] = image;
        return image;
    }

    private Image? ReadImage(string source)
    {
        if (_resources.TryGetValue(source, out var registered))
        {
            return registered as Image ??
                throw new InvalidOperationException($"The resource '{source}' is a vector drawing, and is used where a picture is expected.");
        }

        return Read<Image>(() =>
        {
            if (TryDataUri(source, out _, out var data))
                return Image.FromBytes(data);

            if (Remote is { Images: true } remote && Papira.Html.RemoteImages.IsRemote(source))
                return remote.Get(source) is { } fetched ? Image.FromBytes(fetched) : null;

            return Path(source) is { } path ? Image.FromFile(path) : null;
        });
    }

    /// <summary>
    /// A picture may be anything at all — missing, truncated, in a format Papira does not read, or named
    /// by an address that leads nowhere — so one that cannot be read is left out of the document.
    /// </summary>
    private static T? Read<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException
            or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool LooksLikeSvg(byte[] data)
    {
        var start = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256)).TrimStart();
        return start.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<?xml", StringComparison.Ordinal);
    }

    /// <summary>The file a source names, or null when it does not name one Papira can read.</summary>
    private string? Path(string source)
    {
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return uri.LocalPath;

        if (System.IO.Path.IsPathRooted(source))
            return source;

        return Directory == null ? null : System.IO.Path.Combine(Directory, source);
    }

    private static bool TryDataUri(string source, out string mediaType, out byte[] data)
    {
        mediaType = string.Empty;
        data = [];
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
            throw new InvalidOperationException("A picture given as a data URI has no comma separating its data.");

        var header = source[5..comma];
        mediaType = header.Split(';')[0];
        var payload = source[(comma + 1)..];

        data = header.Contains("base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal))
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));

        return true;
    }
}
