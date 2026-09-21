using System.Buffers.Binary;
using System.IO.Compression;
using Papira.Pdf;

namespace Papira.Images;

/// <summary>
/// PNG support. Opaque, non-interlaced images are embedded without decoding: the zlib stream of the IDAT chunks
/// is valid PDF /FlateDecode data with PNG predictors. Images with transparency or interlacing are decoded,
/// and their alpha is split into a PDF soft mask.
/// </summary>
internal static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Largest accepted image (pixels). Protects against huge allocations from crafted headers.</summary>
    internal const long MaxPixels = 1L << 27;

    internal sealed class PngInfo
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public int ColorType;
        public bool Interlaced;
        public byte[]? Palette;
        public byte[]? Transparency;
        public byte[] Idat = [];

        public int Channels => ColorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Invalid PNG color type {ColorType}."),
        };
    }

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(Signature);

    public static PngInfo ReadInfo(byte[] data)
    {
        var info = new PngInfo();
        var idat = new MemoryStream();
        var position = 8;

        while (position + 8 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            var type = data.AsSpan(position + 4, 4);
            var body = position + 8;
            if (length < 0 || (long)body + length + 4 > data.Length)
                throw new InvalidDataException("Invalid PNG: truncated chunk.");

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13)
                    throw new InvalidDataException("Invalid PNG: IHDR chunk is too short.");
                info.Width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body));
                info.Height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4));
                info.BitDepth = data[body + 8];
                info.ColorType = data[body + 9];
                info.Interlaced = data[body + 12] == 1;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                info.Palette = data.AsSpan(body, length).ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                info.Transparency = data.AsSpan(body, length).ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data, body, length);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            position = body + length + 4; // skip CRC
        }

        info.Idat = idat.ToArray();
        Validate(info);
        return info;
    }

    private static void Validate(PngInfo info)
    {
        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("Invalid PNG: missing IHDR or zero size.");

        if ((long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException($"PNG is too large ({info.Width} x {info.Height} pixels).");

        var validDepth = info.ColorType switch
        {
            0 => info.BitDepth is 1 or 2 or 4 or 8 or 16,
            3 => info.BitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => info.BitDepth is 8 or 16,
            _ => false,
        };

        if (!validDepth)
            throw new InvalidDataException($"Invalid PNG: color type {info.ColorType} with bit depth {info.BitDepth}.");

        if (info.ColorType == 3)
        {
            var palette = info.Palette;
            if (palette == null || palette.Length == 0 || palette.Length % 3 != 0 || palette.Length > 256 * 3)
                throw new InvalidDataException("Invalid PNG: indexed image without a valid palette.");
        }

        if (info.Idat.Length == 0)
            throw new InvalidDataException("Invalid PNG: no image data.");
    }

    public static EncodedImage Encode(PngInfo info, CompressionLevel level)
    {
        var hasAlphaChannel = info.ColorType is 4 or 6;
        var paletteAlpha = info.ColorType == 3 && info.Transparency != null;
        var colorKey = info.ColorType is 0 or 2 && info.Transparency != null;

        if (!hasAlphaChannel && !paletteAlpha && !info.Interlaced)
            return Passthrough(info, colorKey);

        return Decode(info, level);
    }

    private static EncodedImage Passthrough(PngInfo info, bool colorKey)
    {
        var colors = info.ColorType == 2 ? 3 : 1;
        return new EncodedImage
        {
            Width = info.Width,
            Height = info.Height,
            Data = info.Idat,
            Filter = "FlateDecode",
            BitsPerComponent = info.BitDepth,
            ColorSpace = ColorSpaceWriter(info),
            ExtraEntries = b =>
            {
                b.Ascii("/DecodeParms<</Predictor 15/Colors ").Int(colors)
                    .Ascii("/BitsPerComponent ").Int(info.BitDepth)
                    .Ascii("/Columns ").Int(info.Width).Ascii(">>");

                if (colorKey)
                {
                    // tRNS holds one 16-bit sample per color channel.
                    b.Ascii("/Mask[");
                    for (var i = 0; i + 1 < info.Transparency!.Length && i < colors * 2; i += 2)
                    {
                        var value = (info.Transparency[i] << 8) | info.Transparency[i + 1];
                        b.Int(value).Space().Int(value).Space();
                    }

                    b.Ascii("]");
                }
            },
        };
    }

    private static Action<ByteBuffer> ColorSpaceWriter(PngInfo info)
    {
        if (info.ColorType == 3)
        {
            var palette = info.Palette!;
            var entries = palette.Length / 3;
            return b =>
            {
                b.Ascii("[/Indexed/DeviceRGB ").Int(entries - 1).Ascii("<");
                for (var i = 0; i < entries * 3; i++)
                    b.Hex8(palette[i]);
                b.Ascii(">]");
            };
        }

        var gray = info.ColorType is 0 or 4;
        return b => b.Ascii(gray ? "/DeviceGray" : "/DeviceRGB");
    }

    /// <summary>Decodes to 8-bit samples, separates color from alpha and re-compresses both.</summary>
    private static EncodedImage Decode(PngInfo info, CompressionLevel level)
    {
        var width = info.Width;
        var height = info.Height;
        var channels = info.Channels;
        var samples = DecodeSamples(info);

        var colorChannels = info.ColorType switch { 4 => 1, 6 => 3, 2 => 3, _ => 1 };
        var pixelCount = width * height;
        var color = new byte[pixelCount * colorChannels];
        byte[]? alpha = null;

        if (info.ColorType is 4 or 6)
        {
            alpha = new byte[pixelCount];
            for (int p = 0, s = 0, c = 0; p < pixelCount; p++, s += channels)
            {
                for (var k = 0; k < colorChannels; k++)
                    color[c++] = samples[s + k];
                alpha[p] = samples[s + colorChannels];
            }
        }
        else
        {
            Buffer.BlockCopy(samples, 0, color, 0, color.Length);

            if (info.Transparency is { } trns)
            {
                alpha = new byte[pixelCount];
                if (info.ColorType == 3)
                {
                    for (var p = 0; p < pixelCount; p++)
                    {
                        var index = color[p];
                        alpha[p] = index < trns.Length ? trns[index] : (byte)255;
                    }
                }
                else
                {
                    var key = new byte[colorChannels];
                    for (var k = 0; k < colorChannels && k * 2 + 1 < trns.Length; k++)
                    {
                        var value = (trns[k * 2] << 8) | trns[k * 2 + 1];
                        key[k] = (byte)(info.BitDepth == 16 ? value >> 8 : info.ColorType == 0 ? ScaleGray(value, info.BitDepth) : value);
                    }

                    for (var p = 0; p < pixelCount; p++)
                        alpha[p] = color.AsSpan(p * colorChannels, colorChannels).SequenceEqual(key) ? (byte)0 : (byte)255;
                }
            }
        }

        byte[]? softMask = null;
        byte[] colorData;
        if (alpha != null)
        {
            var colorTask = Task.Run(() => PdfWriter.Deflate(color, level));
            softMask = PdfWriter.Deflate(alpha, level);
            colorData = colorTask.GetAwaiter().GetResult();
        }
        else
        {
            colorData = PdfWriter.Deflate(color, level);
        }

        return new EncodedImage
        {
            Width = width,
            Height = height,
            Data = colorData,
            Filter = "FlateDecode",
            BitsPerComponent = 8,
            ColorSpace = ColorSpaceWriter(info),
            SoftMask = softMask,
        };
    }

    private static byte ScaleGray(int value, int bitDepth) =>
        bitDepth >= 8 ? (byte)value : (byte)(value * 255 / ((1 << bitDepth) - 1));

    /// <summary>Returns one byte per sample (16-bit samples are reduced to their high byte).</summary>
    private static byte[] DecodeSamples(PngInfo info)
    {
        var width = info.Width;
        var height = info.Height;
        var channels = info.Channels;
        var bitDepth = info.BitDepth;
        var bitsPerPixel = channels * bitDepth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);

        (int X, int Y, int DX, int DY)[] passes = info.Interlaced
            ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
            : [(0, 0, 1, 1)];

        long expected = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            long passWidth = (width - x0 + dx - 1) / dx, passHeight = (height - y0 + dy - 1) / dy;
            if (passWidth > 0 && passHeight > 0)
                expected += ((passWidth * bitsPerPixel + 7) / 8 + 1) * passHeight;
        }

        var raw = Inflate(info.Idat, checked((int)expected));
        var output = new byte[checked(width * height * channels)];
        var position = 0;

        foreach (var (x0, y0, dx, dy) in passes)
        {
            var passWidth = (width - x0 + dx - 1) / dx;
            var passHeight = (height - y0 + dy - 1) / dy;
            if (passWidth <= 0 || passHeight <= 0)
                continue;

            var stride = (passWidth * bitsPerPixel + 7) / 8;
            var previous = new byte[stride];
            var current = new byte[stride];

            for (var row = 0; row < passHeight; row++)
            {
                var filter = raw[position];
                raw.AsSpan(position + 1, stride).CopyTo(current);
                position += 1 + stride;
                Unfilter(filter, current, previous, bytesPerPixel);

                var y = y0 + row * dy;
                for (var col = 0; col < passWidth; col++)
                {
                    var target = ((y * width) + x0 + col * dx) * channels;
                    for (var k = 0; k < channels; k++)
                    {
                        var sampleIndex = col * channels + k;
                        byte value;
                        switch (bitDepth)
                        {
                            case 8:
                                value = current[sampleIndex];
                                break;
                            case 16:
                                value = current[sampleIndex * 2];
                                break;
                            default:
                                var bitOffset = sampleIndex * bitDepth;
                                var packed = (current[bitOffset >> 3] >> (8 - bitDepth - (bitOffset & 7))) & ((1 << bitDepth) - 1);
                                value = info.ColorType == 3 ? (byte)packed : ScaleGray(packed, bitDepth);
                                break;
                        }

                        output[target + k] = value;
                    }
                }

                (previous, current) = (current, previous);
            }
        }

        return output;
    }

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bpp; i < row.Length; i++)
                    row[i] += row[i - bpp];
                break;
            case 2:
                for (var i = 0; i < row.Length; i++)
                    row[i] += previous[i];
                break;
            case 3:
                for (var i = 0; i < row.Length; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + previous[i]) >> 1);
                break;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                break;
            default:
                throw new InvalidDataException($"Invalid PNG filter type {filter}.");
        }
    }

    /// <summary>Inflates at most <paramref name="expectedLength"/> bytes; extra data is ignored, so crafted streams cannot exhaust memory.</summary>
    private static byte[] Inflate(byte[] data, int expectedLength)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expectedLength];
        var total = 0;
        while (total < expectedLength)
        {
            var read = zlib.Read(output, total, expectedLength - total);
            if (read == 0)
                throw new InvalidDataException("Invalid PNG: image data is truncated.");
            total += read;
        }

        return output;
    }
}
