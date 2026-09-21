using System.IO.Compression;
using Papira.Images;

namespace Papira;

/// <summary>
/// A JPEG or PNG image. Create once and reuse: the PDF-ready encoding is computed lazily a single time
/// and shared by every document that uses this instance (thread-safe).
/// </summary>
public sealed class Image
{
    private readonly Lazy<EncodedImage> _encoded;

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
        else
        {
            throw new NotSupportedException("Unsupported image format. Papira supports JPEG and PNG.");
        }
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    internal float AspectRatio => (float)Height / Width;

    internal EncodedImage Encoded => _encoded.Value;

    /// <summary>Creates an image from JPEG or PNG data. The data is copied.</summary>
    /// <exception cref="NotSupportedException">The data is neither JPEG nor PNG, or uses an unsupported variant.</exception>
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
