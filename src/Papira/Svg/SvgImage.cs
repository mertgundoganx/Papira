using System.Text;
using Papira.Svg;

namespace Papira;

/// <summary>
/// A vector drawing read from SVG markup. Parse it once and draw it as often as you like; instances are
/// immutable and safe to share between documents and threads.
/// </summary>
/// <remarks>
/// Papira draws the part of SVG that logos, icons and charts are made of: paths, rectangles, circles,
/// ellipses, lines, polylines and polygons, in groups with transforms, clip paths, linear and radial
/// gradients, opacity and the usual stroke properties, styled by presentation attributes, a style attribute
/// or a &lt;style&gt; element. Text, embedded raster images, filters, masks and patterns are skipped:
/// convert text to outlines before exporting, and draw bitmaps with <see cref="Image"/>.
/// </remarks>
public sealed class SvgImage
{
    private const int MaxBytes = 64 * 1024 * 1024;

    private SvgImage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new InvalidDataException("The SVG document is empty.");

        if (data.Length > MaxBytes)
            throw new InvalidDataException($"The SVG document is larger than {MaxBytes / (1024 * 1024)} MB.");

        Document = SvgParser.Parse(data);
    }

    internal SvgDocument Document { get; }

    /// <summary>The intrinsic width in the user units of the drawing.</summary>
    public float Width => Document.Width;

    /// <summary>The intrinsic height in the user units of the drawing.</summary>
    public float Height => Document.Height;

    /// <summary>Height divided by width; the element keeps this ratio whatever size it is drawn at.</summary>
    public float AspectRatio => Document.Height / Document.Width;

    /// <summary>How many shapes and groups the drawing ended up with, once unsupported elements were skipped.</summary>
    public int ShapeCount => Document.ShapeCount;

    /// <summary>Reads SVG markup from its bytes; UTF-8, UTF-16 and a byte order mark are all understood.</summary>
    /// <exception cref="InvalidDataException">The markup is not a well-formed SVG document.</exception>
    public static SvgImage FromBytes(byte[] data) => new(data);

    /// <summary>Reads SVG markup from a string.</summary>
    public static SvgImage FromString(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        return new SvgImage(Encoding.UTF8.GetBytes(markup));
    }

    public static SvgImage FromFile(string path) => new(File.ReadAllBytes(path));

    public static SvgImage FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new SvgImage(buffer.ToArray());
    }
}
