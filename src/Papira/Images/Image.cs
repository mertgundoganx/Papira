using System.IO.Compression;
using Papira.Images;

namespace Papira;

/// <summary>
/// A JPEG, PNG or WebP image. Create once and reuse: the PDF-ready encoding is computed lazily a single
/// time and shared by every document that uses this instance (thread-safe).
/// </summary>
public sealed class Image
{
    private readonly Lazy<EncodedImage> _encoded;

    private Image(int width, int height, EncodedImage encoded)
    {
        (Width, Height) = (width, height);
        _encoded = new Lazy<EncodedImage>(() => encoded);
    }

    private Image(byte[] data)
    {
        if (JpegDecoder.IsJpeg(data))
        {
            var info = JpegDecoder.ReadInfo(data);
            (Width, Height) = (info.Width, info.Height);
            _encoded = new Lazy<EncodedImage>(() => JpegDecoder.Encode(data, info));
        }
        else if (PngDecoder.IsPng(data))
        {
            var png = PngDecoder.ReadInfo(data);
            (Width, Height) = (png.Width, png.Height);
            _encoded = new Lazy<EncodedImage>(() => PngDecoder.Encode(png, CompressionLevel.Optimal));
        }
        else if (WebpDecoder.IsWebp(data))
        {
            var webp = WebpDecoder.ReadInfo(data);
            (Width, Height) = (webp.Width, webp.Height);
            _encoded = new Lazy<EncodedImage>(() => WebpDecoder.Encode(webp, CompressionLevel.Optimal));
        }
        else
        {
            throw new NotSupportedException("Unsupported image format. Papira supports JPEG, PNG and WebP.");
        }
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    internal float AspectRatio => (float)Height / Width;

    internal EncodedImage Encoded => _encoded.Value;

    /// <summary>
    /// A picture made of one grey value a pixel, for the shades Papira draws itself — the soft edge of
    /// a shadow, say. The samples run from the top left, a row at a time.
    /// </summary>
    internal static Image FromGrey(byte[] samples, int width, int height) => new(width, height, new EncodedImage
    {
        Width = width,
        Height = height,
        Data = Pdf.PdfWriter.Deflate(samples, CompressionLevel.Optimal),
        Filter = "FlateDecode",
        BitsPerComponent = 8,
        ColorSpace = buffer => buffer.Ascii("/DeviceGray"),
    });

    /// <summary>Creates an image from JPEG, PNG or WebP data. The data is copied.</summary>
    /// <exception cref="NotSupportedException">The format or a variant of it is not supported.</exception>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public static Image FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Image(data.AsSpan().ToArray());
    }

    public static Image FromFile(string path) => new(File.ReadAllBytes(path));

    public static Image FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new Image(copy.ToArray());
    }
}
