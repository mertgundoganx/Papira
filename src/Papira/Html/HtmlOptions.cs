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

    internal IReadOnlyList<string> StyleSheets => _styleSheets;

    internal string? Directory { get; private set; }

    internal float BaseFontSize { get; private set; } = 12;

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
        if (_resources.TryGetValue(source, out var registered))
            return registered as SvgImage;

        if (TryDataUri(source, out var mediaType, out var data))
            return mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase) ? SvgImage.FromBytes(data) : null;

        return Path(source) is { } path && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? SvgImage.FromFile(path)
            : null;
    }

    /// <summary>The picture a source refers to: a registered one, a data URI, or a file.</summary>
    internal Image LoadImage(string source)
    {
        if (_resources.TryGetValue(source, out var registered))
        {
            return registered as Image ??
                throw new InvalidOperationException($"The resource '{source}' is a vector drawing, and is used where a picture is expected.");
        }

        if (TryDataUri(source, out _, out var data))
            return Image.FromBytes(data);

        if (Path(source) is not { } path)
        {
            throw new InvalidOperationException(
                $"The picture '{source}' cannot be read. Papira does not fetch anything over the network: give the picture as a data URI, " +
                "register it with HtmlOptions.Resource, or point it at a local file and set HtmlOptions.BaseDirectory.");
        }

        return Image.FromFile(path);
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
