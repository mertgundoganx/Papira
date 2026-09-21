using Papira.Pdf;

namespace Papira.Images;

/// <summary>Image data ready to be written as a PDF image XObject.</summary>
internal sealed class EncodedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Data { get; init; }

    /// <summary>PDF filter name without slash, e.g. <c>DCTDecode</c> or <c>FlateDecode</c>.</summary>
    public required string Filter { get; init; }

    public required int BitsPerComponent { get; init; }

    /// <summary>Writes the /ColorSpace value.</summary>
    public required Action<ByteBuffer> ColorSpace { get; init; }

    /// <summary>Optional extra dictionary entries (e.g. /DecodeParms, /Decode, /Mask).</summary>
    public Action<ByteBuffer>? ExtraEntries { get; init; }

    /// <summary>Optional soft mask (alpha channel) as 8-bit grayscale, Flate encoded.</summary>
    public byte[]? SoftMask { get; init; }
}
