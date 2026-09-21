using Papira.Pdf;

namespace Papira.Images;

/// <summary>Reads JPEG headers. The compressed data is embedded unchanged (PDF /DCTDecode).</summary>
internal static class JpegDecoder
{
    internal readonly record struct JpegInfo(int Width, int Height, int Components, int BitsPerComponent, bool AdobeInverted);

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static JpegInfo ReadInfo(byte[] data)
    {
        var position = 2;
        var adobe = false;

        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                position += 2;
                continue;
            }

            var length = (data[position + 2] << 8) | data[position + 3];

            // APP14 "Adobe" segment: CMYK JPEGs written by Adobe apps store inverted values.
            if (marker == 0xEE && length >= 12 && data.AsSpan(position + 4, 5).SequenceEqual("Adobe"u8))
                adobe = true;

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame && position + 9 < data.Length)
            {
                var bits = data[position + 4];
                var height = (data[position + 5] << 8) | data[position + 6];
                var width = (data[position + 7] << 8) | data[position + 8];
                var components = data[position + 9];
                if (width == 0 || height == 0)
                    break;

                // PDF viewers reliably support 8-bit baseline, extended and progressive Huffman JPEGs only.
                if (marker is not (0xC0 or 0xC1 or 0xC2) || bits != 8)
                    throw new NotSupportedException("Unsupported JPEG variant (lossless, arithmetic coding or 12-bit). Re-encode it as a standard 8-bit JPEG.");
                if (components is not (1 or 3 or 4))
                    throw new NotSupportedException($"JPEG with {components} color components is not supported.");

                return new JpegInfo(width, height, components, bits, adobe);
            }

            position += 2 + length;
        }

        throw new InvalidDataException("Invalid JPEG: frame header not found.");
    }

    public static EncodedImage Encode(byte[] data, JpegInfo info)
    {
        var colorSpace = info.Components switch
        {
            1 => "/DeviceGray",
            3 => "/DeviceRGB",
            _ => "/DeviceCMYK",
        };

        return new EncodedImage
        {
            Width = info.Width,
            Height = info.Height,
            Data = data,
            Filter = "DCTDecode",
            BitsPerComponent = info.BitsPerComponent,
            ColorSpace = b => b.Ascii(colorSpace),
            ExtraEntries = info.Components == 4 && info.AdobeInverted
                ? b => b.Ascii("/Decode[1 0 1 0 1 0 1 0]")
                : null,
        };
    }
}
