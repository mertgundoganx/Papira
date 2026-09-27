namespace Papira.Images;

/// <summary>
/// The lossless image format inside a WebP file (VP8L). Pixels are entropy coded with Huffman codes,
/// repeated stretches are copied from earlier in the image, and up to four reversible transforms —
/// a predictor, a colour transform, subtract-green and a palette — are undone at the end.
/// </summary>
internal static class WebpLossless
{
    private const int MaxSize = 16384;
    private const int LengthCodes = 24;
    private const int GreenSymbols = 256 + LengthCodes;

    /// <summary>The order the lengths of the code length code are written in.</summary>
    private static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>Short distances are given as an offset from the current pixel, in this order.</summary>
    private static ReadOnlySpan<byte> DistanceMap =>
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39,
        0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a,
        0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
        0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e, 0x66, 0x6a, 0x22, 0x2e,
        0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
        0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x51, 0x5f, 0x73, 0x7d, 0x30, 0x40, 0x72,
        0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    /// <summary>Decodes a lossless image into pixels, one 32-bit value per pixel as alpha, red, green, blue.</summary>
    public static uint[] Decode(byte[] data, int offset, int length, out int width, out int height)
    {
        var reader = new BitReader(data, offset, length);
        if (reader.Read(8) != 0x2F)
            throw new InvalidDataException("The lossless image does not start with its signature.");

        width = (int)reader.Read(14) + 1;
        height = (int)reader.Read(14) + 1;
        reader.Read(1); // whether the image has transparency, which the pixels themselves say too
        if (reader.Read(3) != 0)
            throw new InvalidDataException("The lossless image uses a version Papira does not know.");

        if (width > MaxSize || height > MaxSize)
            throw new InvalidDataException($"The image is larger than {MaxSize} pixels on a side.");

        return DecodeImage(ref reader, width, height, true);
    }

    /// <summary>
    /// Decodes a stream that has no header of its own, which is how the transparency of a lossy picture
    /// is stored: a lossless image of the same size whose green channel holds the alpha values.
    /// </summary>
    public static byte[] DecodeAlpha(byte[] data, int offset, int length, int width, int height)
    {
        var reader = new BitReader(data, offset, length);
        var pixels = DecodeImage(ref reader, width, height, true);

        var alpha = new byte[width * height];
        for (var i = 0; i < alpha.Length && i < pixels.Length; i++)
            alpha[i] = (byte)(pixels[i] >> 8);

        return alpha;
    }

    /// <summary>Decodes an image and undoes the transforms it was written with.</summary>
    private static uint[] DecodeImage(ref BitReader reader, int width, int height, bool topLevel)
    {
        var transforms = new List<Transform>(4);
        var transformed = width;

        if (topLevel)
        {
            var seen = 0;
            while (reader.Read(1) != 0)
            {
                var transform = ReadTransform(ref reader, transformed, height, ref seen);
                if (transform.Kind == TransformKind.ColorIndexing)
                    transformed = SubSampleSize(transformed, transform.Bits);

                transforms.Add(transform);
            }
        }

        var pixels = DecodePixels(ref reader, transformed, height, topLevel);

        // The transforms are undone in the reverse of the order they were applied in.
        for (var i = transforms.Count - 1; i >= 0; i--)
            pixels = Undo(transforms[i], pixels, ref transformed, height, width);

        return pixels;
    }

    private enum TransformKind { Predictor, Color, SubtractGreen, ColorIndexing }

    private readonly record struct Transform(TransformKind Kind, int Bits, uint[] Data, int DataWidth);

    private static Transform ReadTransform(ref BitReader reader, int width, int height, ref int seen)
    {
        var kind = (TransformKind)reader.Read(2);
        if ((seen & (1 << (int)kind)) != 0)
            throw new InvalidDataException("The lossless image applies the same transform twice.");

        seen |= 1 << (int)kind;

        switch (kind)
        {
            case TransformKind.Predictor:
            case TransformKind.Color:
            {
                var bits = (int)reader.Read(3) + 2;
                var blocksWide = SubSampleSize(width, bits);
                var blocksHigh = SubSampleSize(height, bits);
                return new Transform(kind, bits, DecodeImage(ref reader, blocksWide, blocksHigh, false), blocksWide);
            }

            case TransformKind.SubtractGreen:
                return new Transform(kind, 0, [], 0);

            default:
            {
                var colors = (int)reader.Read(8) + 1;
                var palette = DecodeImage(ref reader, colors, 1, false);

                // The palette is written as differences between neighbouring entries.
                for (var i = 1; i < colors; i++)
                    palette[i] = AddPixels(palette[i], palette[i - 1]);

                var bits = colors switch { <= 2 => 3, <= 4 => 2, <= 16 => 1, _ => 0 };
                return new Transform(TransformKind.ColorIndexing, bits, palette, colors);
            }
        }
    }

    private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    // ---- Pixels ------------------------------------------------------------------------------------

    private static uint[] DecodePixels(ref BitReader reader, int width, int height, bool topLevel)
    {
        var cacheBits = 0;
        if (reader.Read(1) != 0)
        {
            cacheBits = (int)reader.Read(4);
            if (cacheBits is < 1 or > 11)
                throw new InvalidDataException("The lossless image asks for a colour cache Papira cannot make.");
        }

        // A meta image says which set of Huffman codes to use for each block of the picture.
        uint[] meta = [];
        var metaBits = 0;
        var metaWidth = 0;
        var groups = 1;

        if (topLevel && reader.Read(1) != 0)
        {
            metaBits = (int)reader.Read(3) + 2;
            metaWidth = SubSampleSize(width, metaBits);
            meta = DecodeImage(ref reader, metaWidth, SubSampleSize(height, metaBits), false);

            var highest = 0u;
            for (var i = 0; i < meta.Length; i++)
            {
                meta[i] = (meta[i] >> 8) & 0xFFFF;
                highest = Math.Max(highest, meta[i]);
            }

            groups = (int)highest + 1;
        }

        if ((long)groups * 5 > 100_000)
            throw new InvalidDataException("The lossless image uses more Huffman codes than Papira supports.");

        var trees = new HuffmanTree[groups * 5];
        var cacheSymbols = cacheBits > 0 ? 1 << cacheBits : 0;
        for (var group = 0; group < groups; group++)
        {
            trees[group * 5] = ReadTree(ref reader, GreenSymbols + cacheSymbols);
            trees[group * 5 + 1] = ReadTree(ref reader, 256);
            trees[group * 5 + 2] = ReadTree(ref reader, 256);
            trees[group * 5 + 3] = ReadTree(ref reader, 256);
            trees[group * 5 + 4] = ReadTree(ref reader, 40);
        }

        var pixels = new uint[(long)width * height <= 64_000_000 ? width * height : throw new InvalidDataException("The image has too many pixels.")];
        var cache = cacheBits > 0 ? new uint[1 << cacheBits] : [];
        var position = 0;
        var x = 0;
        var y = 0;
        var group0 = 0;

        while (position < pixels.Length)
        {
            if (meta.Length > 0)
            {
                // Which codes to use depends on the block the pixel is in, and a copy can land anywhere.
                var block = (y >> metaBits) * metaWidth + (x >> metaBits);
                group0 = (int)meta[Math.Min(block, meta.Length - 1)] * 5;
                if (group0 + 4 >= trees.Length)
                    throw new InvalidDataException("The lossless image points at a Huffman code that is not there.");
            }

            var code = trees[group0].Read(ref reader);
            if (code < 256)
            {
                var red = trees[group0 + 1].Read(ref reader);
                var blue = trees[group0 + 2].Read(ref reader);
                var alpha = trees[group0 + 3].Read(ref reader);
                var argb = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;
                pixels[position++] = argb;
                if (cacheBits > 0)
                    cache[Hash(argb, cacheBits)] = argb;

                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else if (code < GreenSymbols)
            {
                var length = PrefixValue(ref reader, code - 256);
                var distanceCode = trees[group0 + 4].Read(ref reader);
                var distance = MapDistance(PrefixValue(ref reader, distanceCode), width);
                if (distance > position || length > pixels.Length - position)
                    throw new InvalidDataException("The lossless image copies from outside the picture.");

                for (var i = 0; i < length; i++)
                {
                    var argb = pixels[position - distance];
                    pixels[position++] = argb;
                    if (cacheBits > 0)
                        cache[Hash(argb, cacheBits)] = argb;
                }

                x += length;
                while (x >= width)
                {
                    x -= width;
                    y++;
                }
            }
            else
            {
                var index = code - GreenSymbols;
                if (cacheBits == 0 || index >= cache.Length)
                    throw new InvalidDataException("The lossless image reads a colour that was never cached.");

                pixels[position++] = cache[index];
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
        }

        return pixels;
    }

    private static int Hash(uint argb, int bits) => (int)((0x1E35A7BDu * argb) >> (32 - bits));

    /// <summary>Lengths and distances are written as a prefix code with extra bits.</summary>
    private static int PrefixValue(ref BitReader reader, int code)
    {
        if (code < 4)
            return code + 1;

        var extra = (code - 2) >> 1;
        var offset = (2 + (code & 1)) << extra;
        return offset + (int)reader.Read(extra) + 1;
    }

    /// <summary>The nearest distances mean a pixel near the current one rather than a count backwards.</summary>
    private static int MapDistance(int distance, int width)
    {
        if (distance > DistanceMap.Length)
            return distance - DistanceMap.Length;

        var code = DistanceMap[distance - 1];
        var offsetX = 8 - (code & 0xF);
        var offsetY = code >> 4;
        return Math.Max(offsetY * width + offsetX, 1);
    }

    // ---- Transforms --------------------------------------------------------------------------------

    private static uint[] Undo(Transform transform, uint[] pixels, ref int width, int height, int fullWidth)
    {
        switch (transform.Kind)
        {
            case TransformKind.SubtractGreen:
                for (var i = 0; i < pixels.Length; i++)
                {
                    var green = (pixels[i] >> 8) & 0xFF;
                    var red = ((pixels[i] >> 16) + green) & 0xFF;
                    var blue = (pixels[i] + green) & 0xFF;
                    pixels[i] = (pixels[i] & 0xFF00FF00) | (red << 16) | blue;
                }

                return pixels;

            case TransformKind.Color:
                UndoColor(transform, pixels, width, height);
                return pixels;

            case TransformKind.Predictor:
                UndoPredictor(transform, pixels, width, height);
                return pixels;

            default:
            {
                var expanded = new uint[fullWidth * height];
                var perByte = 1 << transform.Bits;
                var mask = (1 << (8 >> transform.Bits)) - 1;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < fullWidth; x++)
                    {
                        int index;
                        if (transform.Bits == 0)
                        {
                            index = (int)((pixels[y * width + x] >> 8) & 0xFF);
                        }
                        else
                        {
                            var packed = pixels[y * width + (x / perByte)];
                            index = (int)((packed >> 8) & 0xFF) >> ((x % perByte) * (8 >> transform.Bits)) & mask;
                        }

                        expanded[y * fullWidth + x] = index < transform.DataWidth ? transform.Data[index] : 0;
                    }
                }

                width = fullWidth;
                return expanded;
            }
        }
    }

    private static void UndoColor(Transform transform, uint[] pixels, int width, int height)
    {
        var blocksWide = SubSampleSize(width, transform.Bits);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var block = transform.Data[(y >> transform.Bits) * blocksWide + (x >> transform.Bits)];
                var greenToRed = (sbyte)(block & 0xFF);
                var greenToBlue = (sbyte)((block >> 8) & 0xFF);
                var redToBlue = (sbyte)((block >> 16) & 0xFF);

                var argb = pixels[y * width + x];
                var green = (int)((argb >> 8) & 0xFF);
                var red = (int)((argb >> 16) & 0xFF);
                var blue = (int)(argb & 0xFF);

                red = (red + ((greenToRed * (sbyte)green) >> 5)) & 0xFF;
                blue = (blue + ((greenToBlue * (sbyte)green) >> 5) + ((redToBlue * (sbyte)red) >> 5)) & 0xFF;

                pixels[y * width + x] = (argb & 0xFF00FF00) | ((uint)red << 16) | (uint)blue;
            }
        }
    }

    private static void UndoPredictor(Transform transform, uint[] pixels, int width, int height)
    {
        var blocksWide = SubSampleSize(width, transform.Bits);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                uint predicted;

                if (x == 0 && y == 0)
                    predicted = 0xFF000000;
                else if (y == 0)
                    predicted = pixels[index - 1];
                else if (x == 0)
                    predicted = pixels[index - width];
                else
                    predicted = Predict(
                        (int)transform.Data[(y >> transform.Bits) * blocksWide + (x >> transform.Bits)] >> 8 & 0xFF,
                        pixels,
                        index,
                        width,
                        x);

                pixels[index] = AddPixels(pixels[index], predicted);
            }
        }
    }

    private static uint Predict(int mode, uint[] pixels, int index, int width, int x)
    {
        var left = pixels[index - 1];
        var top = pixels[index - width];
        var topLeft = pixels[index - width - 1];
        var topRight = x + 1 < width ? pixels[index - width + 1] : pixels[index - width];

        return mode switch
        {
            0 => 0xFF000000,
            1 => left,
            2 => top,
            3 => topRight,
            4 => topLeft,
            5 => Average(Average(left, topRight), top),
            6 => Average(left, topLeft),
            7 => Average(left, top),
            8 => Average(topLeft, top),
            9 => Average(top, topRight),
            10 => Average(Average(left, topLeft), Average(top, topRight)),
            11 => Select(top, left, topLeft),
            12 => ClampAddSubtract(left, top, topLeft),
            13 => ClampAddHalf(left, top, topLeft),
            _ => 0xFF000000,
        };
    }

    private static uint Average(uint a, uint b)
    {
        var result = 0u;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var channel = (((a >> shift) & 0xFF) + ((b >> shift) & 0xFF)) / 2;
            result |= channel << shift;
        }

        return result;
    }

    /// <summary>Picks whichever of the two neighbours is closer to the gradient through the corner.</summary>
    private static uint Select(uint top, uint left, uint topLeft)
    {
        var predictedSum = 0;
        var leftSum = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var t = (int)((top >> shift) & 0xFF);
            var l = (int)((left >> shift) & 0xFF);
            var tl = (int)((topLeft >> shift) & 0xFF);
            var predicted = l + t - tl;
            predictedSum += Math.Abs(predicted - t);
            leftSum += Math.Abs(predicted - l);
        }

        return predictedSum <= leftSum ? top : left;
    }

    private static uint ClampAddSubtract(uint a, uint b, uint c)
    {
        var result = 0u;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var value = (int)((a >> shift) & 0xFF) + (int)((b >> shift) & 0xFF) - (int)((c >> shift) & 0xFF);
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }

    private static uint ClampAddHalf(uint a, uint b, uint c)
    {
        var result = 0u;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var left = (int)((a >> shift) & 0xFF);
            var top = (int)((b >> shift) & 0xFF);
            var topLeft = (int)((c >> shift) & 0xFF);
            var average = (left + top) / 2;
            var value = average + (average - topLeft) / 2;
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }

    private static uint AddPixels(uint a, uint b)
    {
        var alpha = ((a >> 24) + (b >> 24)) & 0xFF;
        var red = (((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) & 0xFF;
        var green = (((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) & 0xFF;
        var blue = ((a & 0xFF) + (b & 0xFF)) & 0xFF;
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }

    // ---- Huffman -----------------------------------------------------------------------------------

    private static HuffmanTree ReadTree(ref BitReader reader, int symbols)
    {
        // A code for one or two symbols is written out directly.
        if (reader.Read(1) != 0)
        {
            var count = (int)reader.Read(1) + 1;
            var first = reader.Read(1) != 0 ? (int)reader.Read(8) : (int)reader.Read(1);
            if (count == 1)
                return HuffmanTree.Single(first);

            return HuffmanTree.Pair(first, (int)reader.Read(8));
        }

        var lengths = new byte[symbols];
        var codeLengthCount = (int)reader.Read(4) + 4;
        var codeLengths = new byte[CodeLengthOrder.Length];
        for (var i = 0; i < codeLengthCount; i++)
            codeLengths[CodeLengthOrder[i]] = (byte)reader.Read(3);

        var lengthTree = HuffmanTree.Build(codeLengths);

        var maxSymbol = symbols;
        if (reader.Read(1) != 0)
        {
            var bits = 2 + 2 * (int)reader.Read(3);
            maxSymbol = 2 + (int)reader.Read(bits);
        }

        var previous = 8;
        for (var i = 0; i < symbols && maxSymbol > 0;)
        {
            maxSymbol--;
            var code = lengthTree.Read(ref reader);
            switch (code)
            {
                case < 16:
                    lengths[i++] = (byte)code;
                    if (code != 0)
                        previous = code;

                    break;
                case 16:
                {
                    var repeat = 3 + (int)reader.Read(2);
                    for (var r = 0; r < repeat && i < symbols; r++)
                        lengths[i++] = (byte)previous;

                    break;
                }

                case 17:
                {
                    var repeat = 3 + (int)reader.Read(3);
                    i = Math.Min(i + repeat, symbols);
                    break;
                }

                default:
                {
                    var repeat = 11 + (int)reader.Read(7);
                    i = Math.Min(i + repeat, symbols);
                    break;
                }
            }
        }

        return HuffmanTree.Build(lengths);
    }

    /// <summary>A canonical Huffman code, read one bit at a time through a table of its lengths.</summary>
    private sealed class HuffmanTree
    {
        private int[] _counts = [];
        private int[] _symbols = [];
        private int _single = -1;

        public static HuffmanTree Single(int symbol) => new() { _single = symbol };

        public static HuffmanTree Pair(int first, int second)
        {
            var lengths = new byte[Math.Max(first, second) + 1];
            lengths[first] = 1;
            lengths[second] = 1;
            return Build(lengths);
        }

        public static HuffmanTree Build(byte[] lengths)
        {
            var tree = new HuffmanTree { _counts = new int[16], _symbols = new int[lengths.Length] };
            foreach (var length in lengths)
            {
                if (length > 15)
                    throw new InvalidDataException("The image has a Huffman code that is too long.");

                tree._counts[length]++;
            }

            tree._counts[0] = 0;
            var offsets = new int[16];
            for (var i = 1; i < 16; i++)
                offsets[i] = offsets[i - 1] + tree._counts[i - 1];

            var used = 0;
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] == 0)
                    continue;

                tree._symbols[offsets[lengths[symbol]]++] = symbol;
                used++;
            }

            if (used == 1)
            {
                // One symbol needs no bits at all.
                for (var symbol = 0; symbol < lengths.Length; symbol++)
                {
                    if (lengths[symbol] != 0)
                        return Single(symbol);
                }
            }

            return tree;
        }

        public int Read(ref BitReader reader)
        {
            if (_single >= 0)
                return _single;

            var code = 0;
            var first = 0;
            var index = 0;
            for (var length = 1; length < 16; length++)
            {
                code |= (int)reader.Read(1);
                var count = _counts[length];
                if (code - first < count)
                    return _symbols[index + (code - first)];

                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            throw new InvalidDataException("The image has a Huffman code Papira could not read.");
        }
    }

    /// <summary>Reads the bits of a lossless image, lowest bit of each byte first.</summary>
    internal ref struct BitReader(byte[] data, int offset, int length)
    {
        private readonly byte[] _data = data;
        private readonly int _end = offset + length;
        private int _position = offset;
        private ulong _bits;
        private int _count;

        public uint Read(int bits)
        {
            if (bits == 0)
                return 0;

            while (_count < bits)
            {
                _bits |= (ulong)(_position < _end ? _data[_position] : 0) << _count;
                _position++;
                _count += 8;
                if (_position > _end + 8)
                    throw new InvalidDataException("The image ends in the middle of its data.");
            }

            var value = (uint)(_bits & ((1UL << bits) - 1));
            _bits >>= bits;
            _count -= bits;
            return value;
        }
    }
}
