using System.Buffers.Binary;
using System.IO.Compression;

namespace Papira.Images;

/// <summary>What a WebP file holds: its size and where the picture and its transparency are.</summary>
internal readonly record struct WebpInfo(byte[] Data, int Width, int Height, int Picture, int PictureLength, bool Lossless, int Alpha, int AlphaLength);

/// <summary>
/// The WebP container. A file is a RIFF form holding one picture, written either losslessly (VP8L) or
/// with the lossy codec (VP8), the latter with its transparency in a chunk of its own.
/// </summary>
internal static class WebpDecoder
{
    private const int MaxPixels = 64_000_000;

    public static bool IsWebp(ReadOnlySpan<byte> data) =>
        data.Length > 12 &&
        data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
        data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

    public static WebpInfo ReadInfo(byte[] data)
    {
        if (!IsWebp(data))
            throw new InvalidDataException("The data is not a WebP file.");

        var riffLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)), (uint)(data.Length - 8));
        var end = Math.Min(8 + riffLength, data.Length);

        int width = 0, height = 0, picture = 0, pictureLength = 0, alpha = 0, alphaLength = 0;
        var lossless = false;

        for (var position = 12; position + 8 <= end;)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            var payload = position + 8;
            if (length < 0 || payload + length > end)
                throw new InvalidDataException("A chunk of the WebP file runs past its end.");

            switch (tag)
            {
                case 0x56503858: // "VP8X": the extended header, which gives the size of the canvas
                    width = ReadUInt24(data, payload + 4) + 1;
                    height = ReadUInt24(data, payload + 7) + 1;
                    break;

                case 0x414C5048: // "ALPH": the transparency of a lossy picture
                    (alpha, alphaLength) = (payload, length);
                    break;

                case 0x56503820: // "VP8 ": a lossy picture
                    (picture, pictureLength, lossless) = (payload, length, false);
                    ReadLossySize(data, payload, length, ref width, ref height);
                    break;

                case 0x5650384C: // "VP8L": a lossless picture
                {
                    (picture, pictureLength, lossless) = (payload, length, true);
                    if (length < 5)
                        throw new InvalidDataException("The lossless picture of the WebP file is too short.");

                    var header = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload + 1));
                    width = (int)(header & 0x3FFF) + 1;
                    height = (int)((header >> 14) & 0x3FFF) + 1;
                    break;
                }

                case 0x414E4D46: // "ANMF": a frame of an animation
                    throw new NotSupportedException("Animated WebP files are not supported; use a single frame.");

                default:
                    break;
            }

            position = payload + length + (length & 1);
        }

        if (picture == 0)
            throw new InvalidDataException("The WebP file holds no picture.");

        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException($"The WebP file gives a size Papira will not decode ({width} x {height}).");

        return new WebpInfo(data, width, height, picture, pictureLength, lossless, alpha, alphaLength);
    }

    /// <summary>The size of a lossy picture comes from the header of its key frame.</summary>
    private static void ReadLossySize(byte[] data, int offset, int length, ref int width, ref int height)
    {
        if (length < 10)
            throw new InvalidDataException("The lossy picture of the WebP file is too short.");

        if (data[offset + 3] != 0x9D || data[offset + 4] != 0x01 || data[offset + 5] != 0x2A)
            throw new InvalidDataException("The lossy picture of the WebP file has no start code.");

        width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 6)) & 0x3FFF;
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 8)) & 0x3FFF;
    }

    private static int ReadUInt24(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

    public static EncodedImage Encode(WebpInfo info, CompressionLevel level)
    {
        var pixels = Decode(info);

        var rgb = new byte[info.Width * info.Height * 3];
        byte[]? alpha = null;

        for (var i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = (byte)(pixels[i] >> 16);
            rgb[i * 3 + 1] = (byte)(pixels[i] >> 8);
            rgb[i * 3 + 2] = (byte)pixels[i];

            var a = (byte)(pixels[i] >> 24);
            if (a == 255)
                continue;

            alpha ??= CreateOpaque(pixels.Length);
            alpha[i] = a;
        }

        return new EncodedImage
        {
            Width = info.Width,
            Height = info.Height,
            Data = Pdf.PdfWriter.Deflate(rgb, level),
            Filter = "FlateDecode",
            BitsPerComponent = 8,
            ColorSpace = b => b.Ascii("/DeviceRGB"),
            SoftMask = alpha == null ? null : Pdf.PdfWriter.Deflate(alpha, level),
        };
    }

    private static byte[] CreateOpaque(int length)
    {
        var alpha = new byte[length];
        Array.Fill(alpha, (byte)255);
        return alpha;
    }

    /// <summary>The pixels of the picture, one 32-bit value each as alpha, red, green and blue.</summary>
    private static uint[] Decode(WebpInfo info)
    {
        if (info.Lossless)
        {
            var lossless = WebpLossless.Decode(info.Data, info.Picture, info.PictureLength, out var width, out var height);
            if (width != info.Width || height != info.Height)
                throw new InvalidDataException("The WebP file disagrees with itself about the size of the picture.");

            return lossless;
        }

        var rgb = WebpLossy.DecodeRgb(info.Data, info.Picture, info.PictureLength, info.Width, info.Height);
        var alpha = info.AlphaLength > 0 ? DecodeAlpha(info) : null;

        var pixels = new uint[info.Width * info.Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var opacity = alpha?[i] ?? 255;
            pixels[i] = ((uint)opacity << 24) | ((uint)rgb[i * 3] << 16) | ((uint)rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];
        }

        return pixels;
    }

    /// <summary>
    /// The transparency of a lossy picture, which is kept in a chunk of its own: either as plain bytes
    /// or as a lossless image, and in either case predicted from the pixels around each one.
    /// </summary>
    private static byte[] DecodeAlpha(WebpInfo info)
    {
        var header = info.Data[info.Alpha];
        var compression = header & 3;
        var filter = (header >> 2) & 3;

        var count = info.Width * info.Height;
        byte[] alpha;

        if (compression == 0)
        {
            if (info.AlphaLength - 1 < count)
                throw new InvalidDataException("The transparency of the WebP file is shorter than its picture.");

            alpha = info.Data.AsSpan(info.Alpha + 1, count).ToArray();
        }
        else if (compression == 1)
        {
            alpha = WebpLossless.DecodeAlpha(info.Data, info.Alpha + 1, info.AlphaLength - 1, info.Width, info.Height);
        }
        else
        {
            throw new InvalidDataException("The WebP file stores its transparency in a way Papira does not know.");
        }

        Unfilter(alpha, info.Width, info.Height, filter);
        return alpha;
    }

    /// <summary>Adds back what the filter subtracted: the pixel to the left, above, or both.</summary>
    private static void Unfilter(byte[] alpha, int width, int height, int filter)
    {
        if (filter == 0)
            return;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            if (y == 0)
            {
                // The first row has nothing above it, so it is always predicted from the left.
                for (var x = 1; x < width; x++)
                    alpha[row + x] += alpha[row + x - 1];

                continue;
            }

            switch (filter)
            {
                case 1:
                {
                    var previous = alpha[row - width];
                    for (var x = 0; x < width; x++)
                    {
                        alpha[row + x] += previous;
                        previous = alpha[row + x];
                    }

                    break;
                }

                case 2:
                    for (var x = 0; x < width; x++)
                        alpha[row + x] += alpha[row - width + x];

                    break;

                default:
                {
                    int left = alpha[row - width];
                    var topLeft = left;
                    for (var x = 0; x < width; x++)
                    {
                        int top = alpha[row - width + x];
                        left = (byte)(alpha[row + x] + Math.Clamp(left + top - topLeft, 0, 255));
                        topLeft = top;
                        alpha[row + x] = (byte)left;
                    }

                    break;
                }
            }
        }
    }
}
