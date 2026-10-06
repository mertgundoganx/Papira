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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        if (wait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");

        Remote = new Papira.Html.RemoteImages(wait, maximumBytes, allowPrivateNetworks);
        return this;
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

        if (TryDataUri(source, out var mediaType, out var data))
            return mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase) ? SvgImage.FromBytes(data) : null;

        if (Remote is { } remote && Papira.Html.RemoteImages.IsRemote(source))
            return remote.Get(source) is { } fetched && LooksLikeSvg(fetched) ? Read(() => SvgImage.FromBytes(fetched)) : null;

        return Path(source) is { } path && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? SvgImage.FromFile(path)
            : null;
    }

    /// <summary>
    /// The picture a source refers to: a registered one, a data URI, a file, or one fetched over the
    /// network. Null where it was to be fetched and could not be, which leaves it out of the document.
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

        if (TryDataUri(source, out _, out var data))
            return Image.FromBytes(data);

        if (Remote is { } remote && Papira.Html.RemoteImages.IsRemote(source))
            return remote.Get(source) is { } fetched ? Read(() => Image.FromBytes(fetched)) : null;

        if (Path(source) is not { } path)
        {
            throw new InvalidOperationException(
                $"The picture '{source}' cannot be read. Papira only fetches pictures over the network once " +
                "HtmlOptions.AllowRemoteImages says it may: give the picture as a data URI, register it with " +
                "HtmlOptions.Resource, or point it at a local file and set HtmlOptions.BaseDirectory.");
        }

        return Image.FromFile(path);
    }

    /// <summary>What was fetched may be anything at all, so a picture that cannot be read is left out.</summary>
    private static T? Read<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
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
