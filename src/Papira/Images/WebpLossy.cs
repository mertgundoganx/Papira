namespace Papira.Images;

/// <summary>
/// The lossy picture inside a WebP file: a VP8 key frame. The bits are read with an arithmetic decoder,
/// every macroblock is predicted from its neighbours and corrected with the coefficients of an inverse DCT,
/// and a loop filter smooths the block edges at the end.
/// </summary>
/// <remarks>Follows RFC 6386, the VP8 specification. Only key frames appear in a still picture.</remarks>
internal sealed class WebpLossy
{
    private const int BlockTypes = 4;
    private const int CoefficientBands = 8;
    private const int PreviousContexts = 3;
    private const int EntropyNodes = 11;

    private readonly byte[] _data;
    private readonly byte[,,,] _coefficientProbabilities = new byte[BlockTypes, CoefficientBands, PreviousContexts, EntropyNodes];
    private readonly short[,] _dequant = new short[4, 6]; // per segment: y dc/ac, y2 dc/ac, uv dc/ac

    private int _width;
    private int _height;
    private int _mbCols;
    private int _mbRows;

    // Planes, with a border so that prediction can read past the edges of the picture.
    private byte[] _y = [];
    private byte[] _u = [];
    private byte[] _v = [];
    private int _yStride;
    private int _uvStride;
    private int _yOrigin;
    private int _uvOrigin;

    // Per macroblock: the modes it was coded with, and whether it has any coefficients.
    private byte[] _yModes = [];
    private byte[] _uvModes = [];
    private byte[] _segments = [];
    private bool[] _skips = [];
    private bool[] _hasCoefficients = [];
    private byte[] _blockModes = [];

    private bool _segmentation;
    private bool _segmentAbsolute;
    private bool _updateSegmentMap;
    private readonly byte[] _segmentTreeProbabilities = [255, 255, 255];
    private readonly int[] _segmentQuantizer = new int[4];
    private readonly int[] _segmentFilter = new int[4];

    private bool _simpleFilter;
    private int _filterLevel;
    private int _sharpness;
    private bool _skipEnabled;
    private byte _skipProbability;

    private WebpLossy(byte[] data) => _data = data;

    /// <summary>Decodes the picture and returns it as three bytes per pixel.</summary>
    public static byte[] DecodeRgb(byte[] data, int offset, int length, int width, int height)
    {
        var decoder = new WebpLossy(data);
        decoder.DecodeFrame(offset, length, width, height);
        return decoder.ToRgb();
    }

    private void DecodeFrame(int offset, int length, int width, int height)
    {
        if (length < 10)
            throw new InvalidDataException("The lossy picture is too short.");

        var tag = _data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16);
        if ((tag & 1) != 0)
            throw new InvalidDataException("A WebP still picture has to be a key frame.");

        var partitionSize = tag >> 5;
        var start = offset + 10; // the frame tag, the start code and the size
        if (partitionSize <= 0 || start + partitionSize > offset + length)
            throw new InvalidDataException("The first partition of the lossy picture runs past its end.");

        _width = width;
        _height = height;
        _mbCols = (width + 15) / 16;
        _mbRows = (height + 15) / 16;
        Allocate();

        var header = new BoolDecoder(_data, start, partitionSize);

        // The colour space and clamping bits are reserved and have to be zero.
        if (header.ReadLiteral(2) != 0)
            throw new NotSupportedException("The lossy picture uses a colour space Papira does not know.");

        ReadSegmentation(ref header);
        ReadLoopFilter(ref header);

        var partitions = ReadPartitions(ref header, start + partitionSize, offset + length, out var tokens);
        ReadQuantizers(ref header);

        header.ReadBit(); // refresh entropy probabilities, which a still picture has no use for
        ReadCoefficientProbabilities(ref header);

        _skipEnabled = header.ReadBit() != 0;
        if (_skipEnabled)
            _skipProbability = (byte)header.ReadLiteral(8);

        ReadModes(ref header);

        _aboveContext = new bool[_mbCols * 9];
        var coefficients = new short[25 * 16];

        for (var row = 0; row < _mbRows; row++)
        {
            Array.Clear(_leftContext);
            for (var col = 0; col < _mbCols; col++)
            {
                PrepareEdges(row, col);
                DecodeMacroblock(ref tokens[row & (partitions - 1)], coefficients, row, col);
            }

            // The rightmost column is extended so that the diagonal predictors of the next row find pixels.
            var last = _yOrigin + row * 16 * _yStride + _mbCols * 16;
            for (var i = 0; i < 4; i++)
                _y[last + 15 * _yStride + i] = _y[last - 1 + 15 * _yStride];
        }

        if (_filterLevel > 0)
            FilterFrame();
    }

    private void Allocate()
    {
        // One row above and one column to the left hold the values prediction uses outside the picture,
        // and the padding on the right holds the pixels the diagonal predictors read past a macroblock.
        _yStride = _mbCols * 16 + 16;
        _uvStride = _mbCols * 8 + 16;
        _y = new byte[_yStride * (_mbRows * 16 + 8)];
        _u = new byte[_uvStride * (_mbRows * 8 + 8)];
        _v = new byte[_uvStride * (_mbRows * 8 + 8)];
        _yOrigin = _yStride + 8;
        _uvOrigin = _uvStride + 8;

        var count = _mbCols * _mbRows;
        _yModes = new byte[count];
        _uvModes = new byte[count];
        _segments = new byte[count];
        _skips = new bool[count];
        _hasCoefficients = new bool[count];
        _blockModes = new byte[count * 16];
    }

    // ---- Header ------------------------------------------------------------------------------------

    private void ReadSegmentation(ref BoolDecoder bits)
    {
        _segmentation = bits.ReadBit() != 0;
        if (!_segmentation)
            return;

        _updateSegmentMap = bits.ReadBit() != 0;
        var updateData = bits.ReadBit() != 0;

        if (updateData)
        {
            _segmentAbsolute = bits.ReadBit() != 0;
            for (var i = 0; i < 4; i++)
                _segmentQuantizer[i] = bits.ReadSigned(7);

            for (var i = 0; i < 4; i++)
                _segmentFilter[i] = bits.ReadSigned(6);
        }

        if (_updateSegmentMap)
        {
            for (var i = 0; i < 3; i++)
                _segmentTreeProbabilities[i] = bits.ReadBit() != 0 ? (byte)bits.ReadLiteral(8) : (byte)255;
        }
    }

    private void ReadLoopFilter(ref BoolDecoder bits)
    {
        _simpleFilter = bits.ReadBit() != 0;
        _filterLevel = (int)bits.ReadLiteral(6);
        _sharpness = (int)bits.ReadLiteral(3);

        if (bits.ReadBit() != 0 && bits.ReadBit() != 0)
        {
            // Adjustments per reference frame and per mode, which a key frame does not use.
            for (var i = 0; i < 8; i++)
                bits.ReadSigned(6);
        }
    }

    private int ReadPartitions(ref BoolDecoder bits, int start, int end, out BoolDecoder[] partitions)
    {
        var count = 1 << (int)bits.ReadLiteral(2);
        partitions = new BoolDecoder[count];

        // The sizes of all but the last partition are written in three bytes each, before the data.
        var sizes = start + (count - 1) * 3;
        if (sizes > end)
            throw new InvalidDataException("The token partitions of the lossy picture run past its end.");

        var position = sizes;
        for (var i = 0; i < count; i++)
        {
            int size;
            if (i + 1 < count)
            {
                var record = start + i * 3;
                size = _data[record] | (_data[record + 1] << 8) | (_data[record + 2] << 16);
            }
            else
            {
                size = end - position;
            }

            if (size < 0 || position + size > end)
                throw new InvalidDataException("A token partition of the lossy picture runs past its end.");

            partitions[i] = new BoolDecoder(_data, position, size);
            position += size;
        }

        return count;
    }

    private void ReadQuantizers(ref BoolDecoder bits)
    {
        var baseIndex = (int)bits.ReadLiteral(7);
        var yDc = bits.ReadSigned(4);
        var y2Dc = bits.ReadSigned(4);
        var y2Ac = bits.ReadSigned(4);
        var uvDc = bits.ReadSigned(4);
        var uvAc = bits.ReadSigned(4);

        for (var segment = 0; segment < 4; segment++)
        {
            var index = baseIndex;
            if (_segmentation)
                index = _segmentAbsolute ? _segmentQuantizer[segment] : index + _segmentQuantizer[segment];

            _dequant[segment, 0] = DcQuantizer(index + yDc);
            _dequant[segment, 1] = AcQuantizer(index);
            _dequant[segment, 2] = (short)(DcQuantizer(index + y2Dc) * 2);
            _dequant[segment, 3] = (short)Math.Max(AcQuantizer(index + y2Ac) * 155 / 100, 8);
            _dequant[segment, 4] = (short)Math.Min((int)DcQuantizer(index + uvDc), 132);
            _dequant[segment, 5] = AcQuantizer(index + uvAc);
        }
    }

    private static short DcQuantizer(int index) => Vp8Tables.DcQuantizers[Math.Clamp(index, 0, 127)];

    private static short AcQuantizer(int index) => Vp8Tables.AcQuantizers[Math.Clamp(index, 0, 127)];

    private void ReadCoefficientProbabilities(ref BoolDecoder bits)
    {
        var defaults = Vp8Tables.DefaultCoefficientProbabilities;
        var updates = Vp8Tables.CoefficientUpdateProbabilities;
        var index = 0;

        for (var i = 0; i < BlockTypes; i++)
        {
            for (var j = 0; j < CoefficientBands; j++)
            {
                for (var k = 0; k < PreviousContexts; k++)
                {
                    for (var l = 0; l < EntropyNodes; l++, index++)
                    {
                        _coefficientProbabilities[i, j, k, l] = bits.ReadBool(updates[index]) != 0
                            ? (byte)bits.ReadLiteral(8)
                            : defaults[index];
                    }
                }
            }
        }
    }

    // ---- Modes -------------------------------------------------------------------------------------

    private void ReadModes(ref BoolDecoder bits)
    {
        for (var row = 0; row < _mbRows; row++)
        {
            for (var col = 0; col < _mbCols; col++)
            {
                var mb = row * _mbCols + col;

                if (_segmentation && _updateSegmentMap)
                {
                    _segments[mb] = (byte)(bits.ReadBool(_segmentTreeProbabilities[0]) != 0
                        ? 2 + bits.ReadBool(_segmentTreeProbabilities[2])
                        : bits.ReadBool(_segmentTreeProbabilities[1]));
                }

                _skips[mb] = _skipEnabled && bits.ReadBool(_skipProbability) != 0;

                var yMode = bits.ReadTree(Vp8Tables.KeyFrameYModeTree, Vp8Tables.KeyFrameYModeProbabilities);
                _yModes[mb] = (byte)yMode;

                if (yMode == 4)
                {
                    // Every one of the sixteen subblocks has a mode of its own, predicted from the
                    // subblocks above and to the left of it.
                    for (var b = 0; b < 16; b++)
                    {
                        var above = AboveBlockMode(row, col, b);
                        var left = LeftBlockMode(row, col, b);
                        var probabilities = Vp8Tables.KeyFrameBlockModeProbabilities.Slice((above * 10 + left) * 9, 9);
                        _blockModes[mb * 16 + b] = (byte)bits.ReadTree(Vp8Tables.BlockModeTree, probabilities);
                    }
                }
                else
                {
                    // The subblock modes of a whole-macroblock mode are what its neighbours will see.
                    var equivalent = (byte)(yMode switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 });
                    for (var b = 0; b < 16; b++)
                        _blockModes[mb * 16 + b] = equivalent;
                }

                _uvModes[mb] = (byte)bits.ReadTree(Vp8Tables.UvModeTree, Vp8Tables.KeyFrameUvModeProbabilities);
            }
        }
    }

    private int AboveBlockMode(int row, int col, int b)
    {
        if (b >= 4)
            return _blockModes[(row * _mbCols + col) * 16 + b - 4];

        // Outside the picture every subblock counts as predicted from its surroundings.
        return row == 0 ? 0 : _blockModes[((row - 1) * _mbCols + col) * 16 + b + 12];
    }

    private int LeftBlockMode(int row, int col, int b)
    {
        if ((b & 3) != 0)
            return _blockModes[(row * _mbCols + col) * 16 + b - 1];

        return col == 0 ? 0 : _blockModes[(row * _mbCols + col - 1) * 16 + b + 3];
    }

    // ---- Coefficients ------------------------------------------------------------------------------

    // Whether the blocks above and to the left of the current one held anything, which picks the
    // probabilities the next block is read with. Nine entries per macroblock: four luma columns or rows,
    // two per chroma plane, and one for the second order block.
    private bool[] _aboveContext = [];
    private readonly bool[] _leftContext = new bool[9];

    private static ReadOnlySpan<byte> LeftContextIndex => [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8];

    private static ReadOnlySpan<byte> AboveContextIndex => [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 8];

    /// <summary>The extra bits of the larger coefficients, with the probability of each.</summary>
    private static ReadOnlySpan<byte> Category1 => [159];

    private static ReadOnlySpan<byte> Category2 => [165, 145];

    private static ReadOnlySpan<byte> Category3 => [173, 148, 140];

    private static ReadOnlySpan<byte> Category4 => [176, 155, 140, 135];

    private static ReadOnlySpan<byte> Category5 => [180, 157, 141, 134, 130];

    private static ReadOnlySpan<byte> Category6 => [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    private void DecodeMacroblock(ref BoolDecoder tokens, short[] coefficients, int row, int col)
    {
        var mb = row * _mbCols + col;
        Array.Clear(coefficients);

        var hasSecondOrder = _yModes[mb] != 4;
        if (_skips[mb])
        {
            // Nothing was written for this macroblock, so its neighbours have nothing to predict from.
            for (var i = 0; i < 8; i++)
            {
                _leftContext[i] = false;
                _aboveContext[col * 9 + i] = false;
            }

            if (hasSecondOrder)
            {
                _leftContext[8] = false;
                _aboveContext[col * 9 + 8] = false;
            }

            _hasCoefficients[mb] = false;
        }
        else
        {
            _hasCoefficients[mb] = ReadCoefficients(ref tokens, coefficients, _segments[mb], hasSecondOrder, col);
        }

        PredictLuma(row, col, mb, coefficients);
        PredictChroma(row, col, mb, coefficients);
    }

    private bool ReadCoefficients(ref BoolDecoder tokens, short[] coefficients, int segment, bool hasSecondOrder, int col)
    {
        var any = false;

        // The second order block holds the direct current of the sixteen luma blocks, so it comes first.
        if (hasSecondOrder)
            any |= ReadBlock(ref tokens, coefficients, 24, 1, segment, 2, col);

        var lumaType = hasSecondOrder ? 0 : 3;
        for (var i = 0; i < 16; i++)
            any |= ReadBlock(ref tokens, coefficients, i, lumaType, segment, 0, col);

        for (var i = 16; i < 24; i++)
            any |= ReadBlock(ref tokens, coefficients, i, 2, segment, 4, col);

        return any;
    }

    /// <summary>
    /// Reads the coefficients of one block. They are written in the order the zigzag gives, each one
    /// read with the probabilities of its position and of what the previous coefficient was.
    /// </summary>
    private bool ReadBlock(ref BoolDecoder tokens, short[] coefficients, int block, int type, int segment, int factor, int col)
    {
        var leftIndex = LeftContextIndex[block];
        var aboveIndex = AboveContextIndex[block];
        var context = (_leftContext[leftIndex] ? 1 : 0) + (_aboveContext[col * 9 + aboveIndex] ? 1 : 0);

        // A block whose direct current came from the second order block starts at the next coefficient.
        var start = type == 0 ? 1 : 0;
        var at = block * 16;
        var position = start;
        var checkEnd = true;

        while (position < 16)
        {
            var band = Vp8Tables.CoefficientBands[position];
            var probabilities = Probabilities(type, band, context);

            if (checkEnd && tokens.ReadBool(probabilities[0]) == 0)
                break;

            if (tokens.ReadBool(probabilities[1]) == 0)
            {
                // A zero; the next coefficient is read without asking whether the block ended.
                position++;
                context = 0;
                checkEnd = false;
                continue;
            }

            var value = ReadMagnitude(ref tokens, probabilities);
            context = value == 1 ? 1 : 2;
            checkEnd = true;

            var dequantized = value * (position == 0 ? _dequant[segment, factor] : _dequant[segment, factor + 1]);
            coefficients[at + Vp8Tables.Zigzag[position]] = (short)(tokens.ReadBit() != 0 ? -dequantized : dequantized);
            position++;
        }

        var nonZero = position != start;
        _leftContext[leftIndex] = nonZero;
        _aboveContext[col * 9 + aboveIndex] = nonZero;
        return nonZero;
    }

    private byte[] Probabilities(int type, int band, int context)
    {
        var probabilities = _probabilityScratch;
        for (var i = 0; i < EntropyNodes; i++)
            probabilities[i] = _coefficientProbabilities[type, band, context, i];

        return probabilities;
    }

    private readonly byte[] _probabilityScratch = new byte[EntropyNodes];

    /// <summary>Reads how large a coefficient is, once it is known not to be zero.</summary>
    private static int ReadMagnitude(ref BoolDecoder tokens, byte[] probabilities)
    {
        if (tokens.ReadBool(probabilities[2]) == 0)
            return 1;

        if (tokens.ReadBool(probabilities[3]) == 0)
        {
            if (tokens.ReadBool(probabilities[4]) == 0)
                return 2;

            return tokens.ReadBool(probabilities[5]) == 0 ? 3 : 4;
        }

        if (tokens.ReadBool(probabilities[6]) == 0)
        {
            return tokens.ReadBool(probabilities[7]) == 0
                ? 5 + Extra(ref tokens, Category1)
                : 7 + Extra(ref tokens, Category2);
        }

        if (tokens.ReadBool(probabilities[8]) == 0)
        {
            return tokens.ReadBool(probabilities[9]) == 0
                ? 11 + Extra(ref tokens, Category3)
                : 19 + Extra(ref tokens, Category4);
        }

        return tokens.ReadBool(probabilities[10]) == 0
            ? 35 + Extra(ref tokens, Category5)
            : 67 + Extra(ref tokens, Category6);
    }

    private static int Extra(ref BoolDecoder tokens, ReadOnlySpan<byte> probabilities)
    {
        var value = 0;
        foreach (var probability in probabilities)
            value = (value << 1) | (int)tokens.ReadBool(probability);

        return value;
    }

    // ---- Prediction and reconstruction ---------------------------------------------------------------

    private void PredictLuma(int row, int col, int mb, short[] coefficients)
    {
        var at = _yOrigin + row * 16 * _yStride + col * 16;
        var mode = _yModes[mb];

        if (mode == 4)
        {
            PredictBlocks(at, mb, coefficients);
            return;
        }

        Predict(_y, at, _yStride, 16, mode);

        // The second order block holds the direct current of all sixteen luma blocks.
        InverseWalsh(coefficients);

        for (var i = 0; i < 16; i++)
        {
            var block = at + (i >> 2) * 4 * _yStride + (i & 3) * 4;
            InverseTransform(_y, block, _yStride, coefficients, i * 16);
        }
    }

    private void PredictBlocks(int at, int mb, short[] coefficients)
    {
        // The four pixels above and to the right of the macroblock are repeated down its right edge,
        // so that the diagonal predictors of the lower subblocks have something to read.
        for (var i = 1; i < 4; i++)
        {
            for (var x = 0; x < 4; x++)
                _y[at + i * 4 * _yStride - _yStride + 16 + x] = _y[at - _yStride + 16 + x];
        }

        for (var i = 0; i < 16; i++)
        {
            var block = at + (i >> 2) * 4 * _yStride + (i & 3) * 4;
            PredictSubblock(block, _blockModes[mb * 16 + i]);
            InverseTransform(_y, block, _yStride, coefficients, i * 16);
        }
    }

    private void PredictChroma(int row, int col, int mb, short[] coefficients)
    {
        var mode = _uvModes[mb];
        var u = _uvOrigin + row * 8 * _uvStride + col * 8;
        var v = _uvOrigin + row * 8 * _uvStride + col * 8;

        Predict(_u, u, _uvStride, 8, mode);
        Predict(_v, v, _uvStride, 8, mode);

        for (var i = 0; i < 4; i++)
        {
            var block = u + (i >> 1) * 4 * _uvStride + (i & 1) * 4;
            InverseTransform(_u, block, _uvStride, coefficients, (16 + i) * 16);
        }

        for (var i = 0; i < 4; i++)
        {
            var block = v + (i >> 1) * 4 * _uvStride + (i & 1) * 4;
            InverseTransform(_v, block, _uvStride, coefficients, (20 + i) * 16);
        }
    }

    /// <summary>The four modes that predict a whole 16x16 or 8x8 block from its neighbours.</summary>
    private static void Predict(byte[] plane, int at, int stride, int size, int mode)
    {
        switch (mode)
        {
            case 0:
            {
                var sum = 0;
                for (var i = 0; i < size; i++)
                    sum += plane[at - stride + i] + plane[at + i * stride - 1];

                var dc = (byte)((sum + size) >> (size == 16 ? 5 : 4));
                for (var y = 0; y < size; y++)
                    Array.Fill(plane, dc, at + y * stride, size);

                break;
            }

            case 1:
                for (var y = 0; y < size; y++)
                    Array.Copy(plane, at - stride, plane, at + y * stride, size);

                break;

            case 2:
                for (var y = 0; y < size; y++)
                    Array.Fill(plane, plane[at + y * stride - 1], at + y * stride, size);

                break;

            default:
            {
                // True motion: the difference between the left column and the corner, added to the row above.
                var corner = plane[at - stride - 1];
                for (var y = 0; y < size; y++)
                {
                    var left = plane[at + y * stride - 1];
                    for (var x = 0; x < size; x++)
                        plane[at + y * stride + x] = Clamp(left + plane[at - stride + x] - corner);
                }

                break;
            }
        }
    }

    /// <summary>The ten modes that predict one 4x4 subblock.</summary>
    private void PredictSubblock(int at, int mode)
    {
        var plane = _y;
        var stride = _yStride;

        // The corner, the eight pixels above (four of them to the right) and the four to the left.
        var corner = plane[at - stride - 1];
        Span<int> a = stackalloc int[8];
        for (var i = 0; i < 8; i++)
            a[i] = plane[at - stride + i];

        Span<int> l = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            l[i] = plane[at + i * stride - 1];

        Span<int> pixels = stackalloc int[16];
        switch (mode)
        {
            case 0:
            {
                var sum = 4;
                for (var i = 0; i < 4; i++)
                    sum += a[i] + l[i];

                pixels.Fill(sum >> 3);
                break;
            }

            case 1:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                        pixels[y * 4 + x] = Clamp(l[y] + a[x] - corner);
                }

                break;

            case 2:
            {
                Span<int> row = [Average3(corner, a[0], a[1]), Average3(a[0], a[1], a[2]), Average3(a[1], a[2], a[3]), Average3(a[2], a[3], a[4])];
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                        pixels[y * 4 + x] = row[x];
                }

                break;
            }

            case 3:
            {
                Span<int> column = [Average3(corner, l[0], l[1]), Average3(l[0], l[1], l[2]), Average3(l[1], l[2], l[3]), Average3(l[2], l[3], l[3])];
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                        pixels[y * 4 + x] = column[y];
                }

                break;
            }

            case 4:
            {
                Span<int> d =
                [
                    Average3(a[0], a[1], a[2]), Average3(a[1], a[2], a[3]), Average3(a[2], a[3], a[4]),
                    Average3(a[3], a[4], a[5]), Average3(a[4], a[5], a[6]), Average3(a[5], a[6], a[7]),
                    Average3(a[6], a[7], a[7]),
                ];

                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                        pixels[y * 4 + x] = d[x + y];
                }

                break;
            }

            case 5:
            {
                Span<int> d =
                [
                    Average3(l[3], l[2], l[1]), Average3(l[2], l[1], l[0]), Average3(l[1], l[0], corner),
                    Average3(l[0], corner, a[0]), Average3(corner, a[0], a[1]), Average3(a[0], a[1], a[2]),
                    Average3(a[1], a[2], a[3]),
                ];

                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                        pixels[y * 4 + x] = d[x - y + 3];
                }

                break;
            }

            case 6:
            {
                Span<int> d =
                [
                    Average3(l[2], l[1], l[0]), Average3(l[1], l[0], corner), Average3(l[0], corner, a[0]),
                    Average2(corner, a[0]), Average2(a[0], a[1]), Average2(a[1], a[2]), Average2(a[2], a[3]),
                    Average3(corner, a[0], a[1]), Average3(a[0], a[1], a[2]), Average3(a[1], a[2], a[3]),
                ];

                ReadOnlySpan<byte> order = [3, 4, 5, 6, 2, 7, 8, 9, 1, 3, 4, 5, 0, 2, 7, 8];
                for (var i = 0; i < 16; i++)
                    pixels[i] = d[order[i]];

                break;
            }

            case 7:
            {
                Span<int> d =
                [
                    Average2(a[0], a[1]), Average2(a[1], a[2]), Average2(a[2], a[3]), Average2(a[3], a[4]),
                    Average3(a[0], a[1], a[2]), Average3(a[1], a[2], a[3]), Average3(a[2], a[3], a[4]),
                    Average3(a[3], a[4], a[5]), Average3(a[4], a[5], a[6]), Average3(a[5], a[6], a[7]),
                ];

                ReadOnlySpan<byte> order = [0, 1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 8, 5, 6, 7, 9];
                for (var i = 0; i < 16; i++)
                    pixels[i] = d[order[i]];

                break;
            }

            case 8:
            {
                Span<int> d =
                [
                    Average2(l[0], corner), Average3(l[0], corner, a[0]), Average3(corner, a[0], a[1]),
                    Average3(a[0], a[1], a[2]), Average2(l[1], l[0]), Average3(l[1], l[0], corner),
                    Average2(l[2], l[1]), Average3(l[2], l[1], l[0]), Average2(l[3], l[2]),
                    Average3(l[3], l[2], l[1]),
                ];

                ReadOnlySpan<byte> order = [0, 1, 2, 3, 4, 5, 0, 1, 6, 7, 4, 5, 8, 9, 6, 7];
                for (var i = 0; i < 16; i++)
                    pixels[i] = d[order[i]];

                break;
            }

            default:
            {
                Span<int> d =
                [
                    Average2(l[0], l[1]), Average3(l[0], l[1], l[2]), Average2(l[1], l[2]),
                    Average3(l[1], l[2], l[3]), Average2(l[2], l[3]), Average3(l[2], l[3], l[3]), l[3],
                ];

                ReadOnlySpan<byte> order = [0, 1, 2, 3, 2, 3, 4, 5, 4, 5, 6, 6, 6, 6, 6, 6];
                for (var i = 0; i < 16; i++)
                    pixels[i] = d[order[i]];

                break;
            }
        }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
                plane[at + y * stride + x] = (byte)pixels[y * 4 + x];
        }
    }

    private static int Average2(int a, int b) => (a + b + 1) >> 1;

    private static int Average3(int a, int b, int c) => (a + 2 * b + c + 2) >> 2;

    // ---- Transforms --------------------------------------------------------------------------------

    private const int CosPi8Sqrt2Minus1 = 20091;
    private const int SinPi8Sqrt2 = 35468;

    /// <summary>
    /// The second order transform: the direct currents of the sixteen luma blocks are coded together
    /// and spread back over them here.
    /// </summary>
    private static void InverseWalsh(short[] coefficients)
    {
        Span<int> tmp = stackalloc int[16];
        var at = 24 * 16;

        for (var i = 0; i < 4; i++)
        {
            var a = coefficients[at + i] + coefficients[at + 12 + i];
            var b = coefficients[at + 4 + i] + coefficients[at + 8 + i];
            var c = coefficients[at + 4 + i] - coefficients[at + 8 + i];
            var d = coefficients[at + i] - coefficients[at + 12 + i];

            tmp[i] = a + b;
            tmp[4 + i] = c + d;
            tmp[8 + i] = a - b;
            tmp[12 + i] = d - c;
        }

        for (var i = 0; i < 4; i++)
        {
            var a = tmp[i * 4] + tmp[i * 4 + 3];
            var b = tmp[i * 4 + 1] + tmp[i * 4 + 2];
            var c = tmp[i * 4 + 1] - tmp[i * 4 + 2];
            var d = tmp[i * 4] - tmp[i * 4 + 3];

            coefficients[(i * 4 + 0) * 16] = (short)((a + b + 3) >> 3);
            coefficients[(i * 4 + 1) * 16] = (short)((c + d + 3) >> 3);
            coefficients[(i * 4 + 2) * 16] = (short)((a - b + 3) >> 3);
            coefficients[(i * 4 + 3) * 16] = (short)((d - c + 3) >> 3);
        }
    }

    /// <summary>The inverse discrete cosine transform of one 4x4 block, added to the prediction.</summary>
    private static void InverseTransform(byte[] plane, int at, int stride, short[] coefficients, int from)
    {
        Span<int> tmp = stackalloc int[16];

        for (var i = 0; i < 4; i++)
        {
            var a = coefficients[from + i] + coefficients[from + 8 + i];
            var b = coefficients[from + i] - coefficients[from + 8 + i];

            var c = ((coefficients[from + 4 + i] * SinPi8Sqrt2) >> 16)
                - (coefficients[from + 12 + i] + ((coefficients[from + 12 + i] * CosPi8Sqrt2Minus1) >> 16));

            var d = coefficients[from + 4 + i] + ((coefficients[from + 4 + i] * CosPi8Sqrt2Minus1) >> 16)
                + ((coefficients[from + 12 + i] * SinPi8Sqrt2) >> 16);

            tmp[i] = a + d;
            tmp[12 + i] = a - d;
            tmp[4 + i] = b + c;
            tmp[8 + i] = b - c;
        }

        for (var i = 0; i < 4; i++)
        {
            var a = tmp[i * 4] + tmp[i * 4 + 2];
            var b = tmp[i * 4] - tmp[i * 4 + 2];
            var c = ((tmp[i * 4 + 1] * SinPi8Sqrt2) >> 16) - (tmp[i * 4 + 3] + ((tmp[i * 4 + 3] * CosPi8Sqrt2Minus1) >> 16));
            var d = tmp[i * 4 + 1] + ((tmp[i * 4 + 1] * CosPi8Sqrt2Minus1) >> 16) + ((tmp[i * 4 + 3] * SinPi8Sqrt2) >> 16);

            var row = at + i * stride;
            plane[row] = Clamp(plane[row] + ((a + d + 4) >> 3));
            plane[row + 3] = Clamp(plane[row + 3] + ((a - d + 4) >> 3));
            plane[row + 1] = Clamp(plane[row + 1] + ((b + c + 4) >> 3));
            plane[row + 2] = Clamp(plane[row + 2] + ((b - c + 4) >> 3));
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    // ---- The pixels outside the picture --------------------------------------------------------------

    /// <summary>Fills in the pixels outside the picture that this macroblock will predict from.</summary>
    private void PrepareEdges(int row, int col)
    {
        var mb = row * _mbCols + col;
        var y = _yOrigin + row * 16 * _yStride + col * 16;
        var u = _uvOrigin + row * 8 * _uvStride + col * 8;
        var v = _uvOrigin + row * 8 * _uvStride + col * 8;

        if (col == 0)
        {
            FixupLeft(_y, y, 16, _yStride, row, _yModes[mb]);
            FixupLeft(_u, u, 8, _uvStride, row, _uvModes[mb]);
            FixupLeft(_v, v, 8, _uvStride, row, _uvModes[mb]);
            if (row == 0)
                _y[y - _yStride - 1] = 127;
        }

        if (row == 0)
        {
            FixupAbove(_y, y, 16, _yStride, col, _yModes[mb]);
            FixupAbove(_u, u, 8, _uvStride, col, _uvModes[mb]);
            FixupAbove(_v, v, 8, _uvStride, col, _uvModes[mb]);
        }
    }

    /// <summary>
    /// The column to the left of the picture is 129, except under the mode that averages its
    /// surroundings, where the row above is repeated into it so that the average comes out right.
    /// </summary>
    private static void FixupLeft(byte[] plane, int at, int size, int stride, int row, int mode)
    {
        if (mode == 0 && row > 0)
        {
            for (var i = 0; i < size; i++)
                plane[at + i * stride - 1] = plane[at - stride + i];
        }
        else
        {
            for (var i = -1; i < size; i++)
                plane[at + i * stride - 1] = 129;
        }
    }

    /// <summary>The row above the picture is 127, with the same exception for the averaging mode.</summary>
    private static void FixupAbove(byte[] plane, int at, int size, int stride, int col, int mode)
    {
        if (mode == 0 && col > 0)
        {
            for (var i = 0; i < size; i++)
                plane[at - stride + i] = plane[at + i * stride - 1];
        }
        else
        {
            for (var i = -1; i < size; i++)
                plane[at - stride + i] = 127;
        }

        // The diagonal predictors of the first row read four pixels past the macroblock.
        for (var i = 0; i < 4; i++)
            plane[at - stride + size + i] = 127;
    }

    // ---- The loop filter ---------------------------------------------------------------------------

    private void FilterFrame()
    {
        for (var row = 0; row < _mbRows; row++)
        {
            for (var col = 0; col < _mbCols; col++)
            {
                var mb = row * _mbCols + col;
                var level = FilterLevel(mb);
                if (level == 0)
                    continue;

                var interior = InteriorLimit(level);
                var hev = HighEdgeVarianceThreshold(level);
                var inner = _hasCoefficients[mb] || _yModes[mb] == 4;

                var y = _yOrigin + row * 16 * _yStride + col * 16;
                var u = _uvOrigin + row * 8 * _uvStride + col * 8;
                var v = _uvOrigin + row * 8 * _uvStride + col * 8;

                if (_simpleFilter)
                {
                    var edge = (level + 2) * 2 + interior;
                    var block = level * 2 + interior;

                    if (col > 0)
                        SimpleEdge(_y, y, 1, _yStride, 16, edge);

                    if (inner)
                    {
                        for (var x = 4; x < 16; x += 4)
                            SimpleEdge(_y, y + x, 1, _yStride, 16, block);
                    }

                    if (row > 0)
                        SimpleEdge(_y, y, _yStride, 1, 16, edge);

                    if (inner)
                    {
                        for (var line = 4; line < 16; line += 4)
                            SimpleEdge(_y, y + line * _yStride, _yStride, 1, 16, block);
                    }

                    continue;
                }

                if (col > 0)
                {
                    MacroblockEdge(_y, y, 1, _yStride, 16, level + 2, interior, hev);
                    MacroblockEdge(_u, u, 1, _uvStride, 8, level + 2, interior, hev);
                    MacroblockEdge(_v, v, 1, _uvStride, 8, level + 2, interior, hev);
                }

                if (inner)
                {
                    for (var x = 4; x < 16; x += 4)
                        SubblockEdge(_y, y + x, 1, _yStride, 16, level, interior, hev);

                    SubblockEdge(_u, u + 4, 1, _uvStride, 8, level, interior, hev);
                    SubblockEdge(_v, v + 4, 1, _uvStride, 8, level, interior, hev);
                }

                if (row > 0)
                {
                    MacroblockEdge(_y, y, _yStride, 1, 16, level + 2, interior, hev);
                    MacroblockEdge(_u, u, _uvStride, 1, 8, level + 2, interior, hev);
                    MacroblockEdge(_v, v, _uvStride, 1, 8, level + 2, interior, hev);
                }

                if (inner)
                {
                    for (var line = 4; line < 16; line += 4)
                        SubblockEdge(_y, y + line * _yStride, _yStride, 1, 16, level, interior, hev);

                    SubblockEdge(_u, u + 4 * _uvStride, _uvStride, 1, 8, level, interior, hev);
                    SubblockEdge(_v, v + 4 * _uvStride, _uvStride, 1, 8, level, interior, hev);
                }
            }
        }
    }

    private int FilterLevel(int mb)
    {
        var level = _filterLevel;
        if (_segmentation)
        {
            var segment = _segments[mb];
            level = _segmentAbsolute ? _segmentFilter[segment] : level + _segmentFilter[segment];
        }

        return Math.Clamp(level, 0, 63);
    }

    private int InteriorLimit(int level)
    {
        var interior = level;
        if (_sharpness > 0)
        {
            interior >>= _sharpness > 4 ? 2 : 1;
            interior = Math.Min(interior, 9 - _sharpness);
        }

        return Math.Max(interior, 1);
    }

    private static int HighEdgeVarianceThreshold(int level) => (level >= 40 ? 2 : level >= 15 ? 1 : 0);

    /// <summary>The edge between two macroblocks, which is filtered with the widest taps.</summary>
    private static void MacroblockEdge(byte[] plane, int at, int step, int stride, int length, int edge, int interior, int hev)
    {
        for (var i = 0; i < length; i++)
        {
            var p = at + i * stride;
            if (!NormalThreshold(plane, p, step, edge, interior))
                continue;

            if (HighEdgeVariance(plane, p, step, hev))
                CommonFilter(plane, p, step, true);
            else
                WideFilter(plane, p, step);
        }
    }

    /// <summary>An edge inside a macroblock, between two of its subblocks.</summary>
    private static void SubblockEdge(byte[] plane, int at, int step, int stride, int length, int edge, int interior, int hev)
    {
        for (var i = 0; i < length; i++)
        {
            var p = at + i * stride;
            if (NormalThreshold(plane, p, step, edge, interior))
                CommonFilter(plane, p, step, HighEdgeVariance(plane, p, step, hev));
        }
    }

    private static void SimpleEdge(byte[] plane, int at, int step, int stride, int length, int limit)
    {
        for (var i = 0; i < length; i++)
        {
            var p = at + i * stride;
            if (Math.Abs(plane[p - step] - plane[p]) * 2 + (Math.Abs(plane[p - 2 * step] - plane[p + step]) >> 1) <= limit)
                CommonFilter(plane, p, step, true);
        }
    }

    private static bool NormalThreshold(byte[] plane, int at, int step, int edge, int interior)
    {
        int P3 = plane[at - 4 * step], P2 = plane[at - 3 * step], P1 = plane[at - 2 * step], P0 = plane[at - step];
        int Q0 = plane[at], Q1 = plane[at + step], Q2 = plane[at + 2 * step], Q3 = plane[at + 3 * step];

        return Math.Abs(P0 - Q0) * 2 + (Math.Abs(P1 - Q1) >> 1) <= 2 * edge + interior
            && Math.Abs(P3 - P2) <= interior && Math.Abs(P2 - P1) <= interior && Math.Abs(P1 - P0) <= interior
            && Math.Abs(Q3 - Q2) <= interior && Math.Abs(Q2 - Q1) <= interior && Math.Abs(Q1 - Q0) <= interior;
    }

    private static bool HighEdgeVariance(byte[] plane, int at, int step, int threshold) =>
        Math.Abs(plane[at - 2 * step] - plane[at - step]) > threshold ||
        Math.Abs(plane[at + step] - plane[at]) > threshold;

    private static void CommonFilter(byte[] plane, int at, int step, bool useOuterTaps)
    {
        int P1 = plane[at - 2 * step], P0 = plane[at - step], Q0 = plane[at], Q1 = plane[at + step];

        var a = 3 * (Q0 - P0);
        if (useOuterTaps)
            a += Saturate(P1 - Q1);

        a = Saturate(a);
        var f1 = Math.Min(a + 4, 127) >> 3;
        var f2 = Math.Min(a + 3, 127) >> 3;

        plane[at - step] = Clamp(P0 + f2);
        plane[at] = Clamp(Q0 - f1);

        if (!useOuterTaps)
        {
            var b = (f1 + 1) >> 1;
            plane[at - 2 * step] = Clamp(P1 + b);
            plane[at + step] = Clamp(Q1 - b);
        }
    }

    private static void WideFilter(byte[] plane, int at, int step)
    {
        int P2 = plane[at - 3 * step], P1 = plane[at - 2 * step], P0 = plane[at - step];
        int Q0 = plane[at], Q1 = plane[at + step], Q2 = plane[at + 2 * step];

        var w = Saturate(Saturate(P1 - Q1) + 3 * (Q0 - P0));

        var a = (27 * w + 63) >> 7;
        plane[at - step] = Clamp(P0 + a);
        plane[at] = Clamp(Q0 - a);

        a = (18 * w + 63) >> 7;
        plane[at - 2 * step] = Clamp(P1 + a);
        plane[at + step] = Clamp(Q1 - a);

        a = (9 * w + 63) >> 7;
        plane[at - 3 * step] = Clamp(P2 + a);
        plane[at + 2 * step] = Clamp(Q2 + -a);
    }

    private static int Saturate(int value) => value < -128 ? -128 : value > 127 ? 127 : value;

    // ---- Colour ------------------------------------------------------------------------------------

    /// <summary>
    /// Turns the three planes into pixels. Colour is stored for every second pixel in each direction,
    /// so it is interpolated back up the way the reference decoder does: each output pixel takes a
    /// weighted average of the four colour samples around it.
    /// </summary>
    private byte[] ToRgb()
    {
        var rgb = new byte[_width * _height * 3];
        var chromaRows = (_height + 1) / 2;

        // The first row has no row of colour above it, so it uses its own twice.
        Upsample(0, -1, 0, 0, rgb);

        for (var y = 0; y + 2 < _height; y += 2)
            Upsample(y + 1, y + 2, y / 2, y / 2 + 1, rgb);

        // A picture with an even number of rows ends on a row without a pair.
        if (_height > 1 && (_height & 1) == 0)
            Upsample(_height - 1, -1, chromaRows - 1, chromaRows - 1, rgb);

        return rgb;
    }

    private void Upsample(int topRow, int bottomRow, int topChroma, int currentChroma, byte[] rgb)
    {
        var lastPair = (_width - 1) >> 1;
        var top = _uvOrigin + topChroma * _uvStride;
        var current = _uvOrigin + currentChroma * _uvStride;

        int tlU = _u[top], tlV = _v[top];
        int lU = _u[current], lV = _v[current];

        Write(topRow, 0, (3 * tlU + lU + 2) >> 2, (3 * tlV + lV + 2) >> 2, rgb);
        if (bottomRow >= 0)
            Write(bottomRow, 0, (3 * lU + tlU + 2) >> 2, (3 * lV + tlV + 2) >> 2, rgb);

        for (var x = 1; x <= lastPair; x++)
        {
            int tU = _u[top + x], tV = _v[top + x];
            int cU = _u[current + x], cV = _v[current + x];

            // The two diagonals of the square of colour samples around this pair of pixels.
            var sumU = tlU + tU + lU + cU + 8;
            var sumV = tlV + tV + lV + cV + 8;
            var diagonalU12 = (sumU + 2 * (tU + lU)) >> 3;
            var diagonalV12 = (sumV + 2 * (tV + lV)) >> 3;
            var diagonalU03 = (sumU + 2 * (tlU + cU)) >> 3;
            var diagonalV03 = (sumV + 2 * (tlV + cV)) >> 3;

            Write(topRow, 2 * x - 1, (diagonalU12 + tlU) >> 1, (diagonalV12 + tlV) >> 1, rgb);
            Write(topRow, 2 * x, (diagonalU03 + tU) >> 1, (diagonalV03 + tV) >> 1, rgb);

            if (bottomRow >= 0)
            {
                Write(bottomRow, 2 * x - 1, (diagonalU03 + lU) >> 1, (diagonalV03 + lV) >> 1, rgb);
                Write(bottomRow, 2 * x, (diagonalU12 + cU) >> 1, (diagonalV12 + cV) >> 1, rgb);
            }

            (tlU, tlV, lU, lV) = (tU, tV, cU, cV);
        }

        if ((_width & 1) == 0)
        {
            Write(topRow, _width - 1, (3 * tlU + lU + 2) >> 2, (3 * tlV + lV + 2) >> 2, rgb);
            if (bottomRow >= 0)
                Write(bottomRow, _width - 1, (3 * lU + tlU + 2) >> 2, (3 * lV + tlV + 2) >> 2, rgb);
        }
    }

    private void Write(int row, int column, int u, int v, byte[] rgb)
    {
        var luma = _y[_yOrigin + row * _yStride + column];
        var at = (row * _width + column) * 3;
        rgb[at] = ClampColor(MultHi(luma, 19077) + MultHi(v, 26149) - 14234);
        rgb[at + 1] = ClampColor(MultHi(luma, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        rgb[at + 2] = ClampColor(MultHi(luma, 19077) + MultHi(u, 33050) - 17685);
    }

    private static int MultHi(int value, int coefficient) => (value * coefficient) >> 8;

    private static byte ClampColor(int value) => (byte)Math.Clamp(value >> 6, 0, 255);

    // ---- The arithmetic decoder ----------------------------------------------------------------------

    /// <summary>
    /// Reads the bits of a VP8 partition. Every value is a decision between two outcomes whose
    /// probability the decoder is told, which is what lets the encoder spend a fraction of a bit on one.
    /// </summary>
    internal struct BoolDecoder
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;
        private uint _value;
        private uint _range;
        private int _shift;

        public BoolDecoder(byte[] data, int offset, int length)
        {
            _data = data;
            _end = offset + length;
            _position = offset;
            _value = (uint)((Next() << 8) | Next());
            _range = 255;
            _shift = 0;
        }

        private byte Next() => _position < _end ? _data[_position++] : (byte)0;

        public uint ReadBool(int probability)
        {
            var split = 1 + (((_range - 1) * (uint)probability) >> 8);
            var big = split << 8;
            uint result;

            if (_value >= big)
            {
                result = 1;
                _range -= split;
                _value -= big;
            }
            else
            {
                result = 0;
                _range = split;
            }

            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_shift == 8)
                {
                    _shift = 0;
                    _value |= Next();
                }
            }

            return result;
        }

        public uint ReadBit() => ReadBool(128);

        public uint ReadLiteral(int bits)
        {
            uint value = 0;
            while (bits-- > 0)
                value = (value << 1) | ReadBit();

            return value;
        }

        /// <summary>A value that is written only when it is not zero, with its sign after it.</summary>
        public int ReadSigned(int bits)
        {
            if (ReadBit() == 0)
                return 0;

            var value = (int)ReadLiteral(bits);
            return ReadBit() != 0 ? -value : value;
        }

        /// <summary>Walks a tree of decisions until it reaches one of the values at its leaves.</summary>
        public int ReadTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities)
        {
            var index = 0;
            do
            {
                index = tree[index + (int)ReadBool(probabilities[index >> 1])];
            }
            while (index > 0);

            return -index;
        }
    }
}
