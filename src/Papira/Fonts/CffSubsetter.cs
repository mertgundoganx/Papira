using System.Buffers.Binary;

namespace Papira.Fonts;

/// <summary>
/// Subsets the Compact Font Format outlines of an OpenType font (.otf). The result is a bare CFF font,
/// which is what a PDF embeds for a CID-keyed Type 0 font.
/// </summary>
/// <remarks>
/// The glyphs keep their numbers, so the glyph ids the content stream writes stay valid; the charstrings
/// of glyphs the document does not use are replaced by an empty one. The output is always CID-keyed, with
/// every character identifier equal to its glyph number, which is what the Identity encoding needs.
/// Subroutines are carried over unchanged, so the numbering the charstrings rely on still holds.
/// </remarks>
internal static class CffSubsetter
{
    private const int MaxGlyphs = 65536;

    public static byte[] Subset(TrueTypeFont font, IReadOnlyList<ushort> glyphs, string name)
    {
        if (!font.TryTable("CFF ", out var offset, out var length))
            throw new InvalidDataException("The font has no CFF table.");

        var reader = new CffReader(font.Data, offset, length);
        return reader.Subset(glyphs, font.GlyphCount, name);
    }

    /// <summary>Reads the parts of a CFF font Papira needs to build a subset of it.</summary>
    private sealed class CffReader
    {
        private readonly byte[] _data;
        private readonly int _start;
        private readonly int _end;

        public CffReader(byte[] data, int start, int length)
        {
            _data = data;
            _start = start;
            _end = start + length;

            if (length < 4 || data[start] != 1)
                throw new InvalidDataException("Only version 1 of the Compact Font Format is supported.");
        }

        public byte[] Subset(IReadOnlyList<ushort> glyphs, int glyphCount, string name)
        {
            var headerSize = _data[_start + 2];
            var names = ReadIndex(_start + headerSize);
            var topDicts = ReadIndex(names.End);
            var strings = ReadIndex(topDicts.End);
            var globalSubrs = ReadIndex(strings.End);

            if (topDicts.Count == 0)
                throw new InvalidDataException("The CFF font has no top dictionary.");

            var top = ReadDict(topDicts.Item(0));
            var charStrings = ReadIndex(_start + Operand(top, 17, "CharStrings"));
            if (charStrings.Count > MaxGlyphs)
                throw new InvalidDataException("The CFF font has more glyphs than Papira supports.");

            glyphCount = Math.Min(glyphCount, charStrings.Count);

            // Which glyphs to keep. A charstring that builds an accented letter from two others would need
            // those two as well, so such a font is embedded whole rather than risking a missing accent.
            var keep = new bool[glyphCount];
            keep[0] = true;
            foreach (var glyph in glyphs)
            {
                if (glyph < glyphCount)
                    keep[glyph] = true;
            }

            if (UsesSeac(charStrings, keep, glyphCount))
                Array.Fill(keep, true);

            var cid = top.ContainsKey(1230);
            var fonts = new List<PrivateFont>();
            byte[] select;

            if (cid)
            {
                var fdArray = ReadIndex(_start + Operand(top, 1236, "FDArray"));
                select = ReadFdSelect(_start + Operand(top, 1237, "FDSelect"), glyphCount, fdArray.Count);

                // Only the font dictionaries the kept glyphs use are carried over; the subroutines of the
                // others would otherwise make up most of the subset of a large font.
                var used = new bool[fdArray.Count];
                for (var glyph = 0; glyph < glyphCount; glyph++)
                {
                    if (keep[glyph])
                        used[select[glyph]] = true;
                }

                var renumbered = new byte[fdArray.Count];
                for (var i = 0; i < fdArray.Count; i++)
                {
                    if (!used[i] && fonts.Count > 0)
                        continue;

                    renumbered[i] = (byte)fonts.Count;
                    fonts.Add(ReadPrivate(ReadDict(fdArray.Item(i))));
                }

                for (var glyph = 0; glyph < glyphCount; glyph++)
                    select[glyph] = keep[glyph] ? renumbered[select[glyph]] : (byte)0;
            }
            else
            {
                fonts.Add(ReadPrivate(top));
                select = new byte[glyphCount];
            }

            var globals = globalSubrs.Items();
            var charStringData = new byte[glyphCount][];
            for (var glyph = 0; glyph < glyphCount; glyph++)
                charStringData[glyph] = keep[glyph] ? charStrings.Item(glyph).ToArray() : [14]; // endchar

            SubsetSubroutines(charStringData, keep, select, fonts, globals);

            return Write(
                name,
                top,
                WriteIndex([.. globals.Select(subr => subr.ToArray())]),
                charStringData,
                BuildFdSelect(select, glyphCount),
                fonts.Select(font => (font.Write(), WriteIndex([.. font.Subrs.Select(subr => subr.ToArray())]))).ToList(),
                glyphCount);
        }

        /// <summary>
        /// Drops the subroutines no kept glyph calls and renumbers the calls to the ones that stay. In a
        /// large font these hold most of the outlines, so a subset that keeps them all would hardly be one.
        /// A charstring that cannot be followed leaves every subroutine in place.
        /// </summary>
        private static void SubsetSubroutines(
            byte[][] charStrings,
            bool[] keep,
            byte[] select,
            List<PrivateFont> fonts,
            List<ReadOnlyMemory<byte>> globals)
        {
            var usages = new CffCharstrings.Usage[fonts.Count];

            for (var fd = 0; fd < fonts.Count; fd++)
            {
                usages[fd] = new CffCharstrings.Usage(fonts[fd].Subrs.Count, globals.Count);
                var kept = new List<ReadOnlyMemory<byte>>();
                for (var glyph = 0; glyph < charStrings.Length; glyph++)
                {
                    if (keep[glyph] && select[glyph] == fd)
                        kept.Add(charStrings[glyph]);
                }

                if (!CffCharstrings.TryMark(kept, fonts[fd].Subrs, globals, usages[fd]))
                    return;
            }

            // A global subroutine may be reached from any font dictionary, so the marks are merged.
            var globalUsed = new bool[globals.Count];
            foreach (var usage in usages)
            {
                for (var i = 0; i < globalUsed.Length; i++)
                    globalUsed[i] |= usage.GlobalUsed[i];
            }

            var globalMap = Renumber(globalUsed, out var newGlobals);
            var oldGlobalBias = CffCharstrings.Bias(globals.Count);
            var newGlobalBias = CffCharstrings.Bias(newGlobals.Count);
            var rewrittenGlobals = new ReadOnlyMemory<byte>[newGlobals.Count];
            var newSubrs = new List<ReadOnlyMemory<byte>>[fonts.Count];
            var newCharStrings = (byte[][])charStrings.Clone();

            for (var fd = 0; fd < fonts.Count; fd++)
            {
                var usage = usages[fd];
                var localMap = Renumber(usage.LocalUsed, out var newLocals);
                var oldLocalBias = CffCharstrings.Bias(fonts[fd].Subrs.Count);
                var newLocalBias = CffCharstrings.Bias(newLocals.Count);

                byte[] Rewrite(ReadOnlySpan<byte> code, int stems) => CffCharstrings.Rewrite(
                    code, localMap, globalMap, oldLocalBias, oldGlobalBias, newLocalBias, newGlobalBias, usage, stems);

                for (var glyph = 0; glyph < charStrings.Length; glyph++)
                {
                    if (keep[glyph] && select[glyph] == fd)
                        newCharStrings[glyph] = Rewrite(charStrings[glyph], 0);
                }

                var rewritten = new List<ReadOnlyMemory<byte>>(newLocals.Count);
                foreach (var index in newLocals)
                    rewritten.Add(Rewrite(fonts[fd].Subrs[index].Span, usage.LocalEntryStems[index]));

                newSubrs[fd] = rewritten;

                // A global subroutine is rewritten with the numbering of the font dictionary that reaches it.
                for (var i = 0; i < newGlobals.Count; i++)
                {
                    var index = newGlobals[i];
                    if (usage.GlobalUsed[index] && rewrittenGlobals[i].IsEmpty)
                        rewrittenGlobals[i] = Rewrite(globals[index].Span, usage.GlobalEntryStems[index]);
                }
            }

            // The rewritten font is read back the same way a viewer would, and only replaces the original
            // when it comes out whole. A font Papira could not follow exactly keeps all its subroutines.
            var newGlobalSubrs = new List<ReadOnlyMemory<byte>>(rewrittenGlobals);
            for (var fd = 0; fd < fonts.Count; fd++)
            {
                var kept = new List<ReadOnlyMemory<byte>>();
                for (var glyph = 0; glyph < newCharStrings.Length; glyph++)
                {
                    if (keep[glyph] && select[glyph] == fd)
                        kept.Add(newCharStrings[glyph]);
                }

                var check = new CffCharstrings.Usage(newSubrs[fd].Count, newGlobalSubrs.Count);
                if (!CffCharstrings.TryMark(kept, newSubrs[fd], newGlobalSubrs, check))
                    return;

                // Every subroutine that was kept has to still be reachable, or something was renumbered wrong.
                if (Array.IndexOf(check.LocalUsed, false) >= 0)
                    return;
            }

            for (var glyph = 0; glyph < charStrings.Length; glyph++)
                charStrings[glyph] = newCharStrings[glyph];

            for (var fd = 0; fd < fonts.Count; fd++)
                fonts[fd].Subrs = newSubrs[fd];

            globals.Clear();
            globals.AddRange(newGlobalSubrs);
        }

        /// <summary>Numbers the subroutines that are kept from zero, and lists them in that order.</summary>
        private static int[] Renumber(bool[] used, out List<int> kept)
        {
            var map = new int[used.Length];
            kept = [];
            for (var i = 0; i < used.Length; i++)
            {
                map[i] = kept.Count;
                if (used[i])
                    kept.Add(i);
            }

            return map;
        }

        /// <summary>True when a kept charstring builds its glyph from two others (the "seac" ending).</summary>
        private static bool UsesSeac(CffIndex charStrings, bool[] keep, int glyphCount)
        {
            for (var glyph = 0; glyph < glyphCount; glyph++)
            {
                if (!keep[glyph])
                    continue;

                var operands = 0;
                var data = charStrings.Item(glyph);
                for (var i = 0; i < data.Length;)
                {
                    var b = data[i];
                    if (b >= 32 || b == 28)
                    {
                        i += b switch
                        {
                            28 => 3,
                            < 247 => 1,
                            < 251 => 2,
                            < 255 => 2,
                            _ => 5,
                        };

                        operands++;
                        continue;
                    }

                    if (b == 14)
                        return operands >= 4; // endchar with the accent arguments

                    if (b == 12)
                        i += 2;
                    else if (b is 1 or 3 or 18 or 23)
                        i++;
                    else if (b == 19 || b == 20)
                        i += 1 + (operands + 7) / 8; // hintmask carries its bits inline
                    else
                        i++;

                    operands = 0;
                }
            }

            return false;
        }

        private PrivateFont ReadPrivate(Dictionary<int, double[]> dict)
        {
            if (!dict.TryGetValue(18, out var value) || value.Length < 2)
                return new PrivateFont([], []);

            var size = (int)value[0];
            var offset = _start + (int)value[1];
            if (size < 0 || offset < _start || offset + size > _end)
                throw new InvalidDataException("The CFF font has a private dictionary outside the table.");

            var privateDict = ReadDict(_data.AsSpan(offset, size));
            var subrs = privateDict.TryGetValue(19, out var subrsOffset)
                ? ReadIndex(offset + (int)subrsOffset[0]).Items()
                : [];

            privateDict.Remove(19);
            return new PrivateFont(privateDict, subrs);
        }

        /// <summary>A private dictionary with its local subroutines, before either is written out.</summary>
        private sealed class PrivateFont(Dictionary<int, double[]> dict, List<ReadOnlyMemory<byte>> subrs)
        {
            public Dictionary<int, double[]> Dict { get; } = dict;

            public List<ReadOnlyMemory<byte>> Subrs { get; set; } = subrs;

            /// <summary>
            /// The dictionary as bytes. The subroutines follow it and their offset is measured from its
            /// start, so it is written twice: once to learn its size, once with that size in it.
            /// </summary>
            public byte[] Write() => Subrs.Count == 0
                ? WriteDict(Dict)
                : WriteDict(Dict, (19, WriteDict(Dict, (19, 0)).Length));
        }

        private byte[] ReadFdSelect(int offset, int glyphCount, int fdCount)
        {
            var select = new byte[glyphCount];
            var format = _data[offset];
            if (format == 0)
            {
                for (var glyph = 0; glyph < glyphCount; glyph++)
                    select[glyph] = _data[offset + 1 + glyph];
            }
            else if (format == 3)
            {
                var ranges = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 1));
                var sentinel = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 3 + ranges * 3));
                for (var i = 0; i < ranges; i++)
                {
                    var first = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 3 + i * 3));
                    var fd = _data[offset + 5 + i * 3];
                    var next = i + 1 < ranges ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 6 + i * 3)) : sentinel;
                    for (var glyph = first; glyph < next && glyph < glyphCount; glyph++)
                        select[glyph] = fd;
                }
            }
            else
            {
                throw new InvalidDataException($"Unsupported CFF FDSelect format {format}.");
            }

            foreach (var fd in select)
            {
                if (fd >= fdCount)
                    throw new InvalidDataException("The CFF font selects a font dictionary that does not exist.");
            }

            return select;
        }

        private static byte[] BuildFdSelect(byte[] select, int glyphCount)
        {
            // Format 3: one entry per run of glyphs that share a font dictionary.
            var ranges = new List<(ushort First, byte Fd)>();
            for (var glyph = 0; glyph < glyphCount; glyph++)
            {
                if (ranges.Count == 0 || ranges[^1].Fd != select[glyph])
                    ranges.Add(((ushort)glyph, select[glyph]));
            }

            var output = new byte[3 + ranges.Count * 3 + 2];
            output[0] = 3;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(1), (ushort)ranges.Count);
            for (var i = 0; i < ranges.Count; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(3 + i * 3), ranges[i].First);
                output[5 + i * 3] = ranges[i].Fd;
            }

            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(3 + ranges.Count * 3), (ushort)glyphCount);
            return output;
        }

        private int Operand(Dictionary<int, double[]> dict, int key, string what)
        {
            if (!dict.TryGetValue(key, out var value) || value.Length == 0)
                throw new InvalidDataException($"The CFF font has no {what}.");

            var offset = (int)value[^1];
            if (_start + offset < _start || _start + offset >= _end)
                throw new InvalidDataException($"The CFF font places {what} outside the table.");

            return offset;
        }

        // ---- Reading ------------------------------------------------------------------------------

        private CffIndex ReadIndex(int offset)
        {
            if (offset + 2 > _end)
                throw new InvalidDataException("The CFF font ends inside an index.");

            var count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset));
            if (count == 0)
                return new CffIndex(_data, offset, offset + 2, [], 0);

            var offSize = _data[offset + 2];
            if (offSize is < 1 or > 4)
                throw new InvalidDataException("The CFF font has an index with an invalid offset size.");

            var offsets = new int[count + 1];
            var table = offset + 3;
            for (var i = 0; i <= count; i++)
            {
                var value = 0;
                for (var b = 0; b < offSize; b++)
                    value = (value << 8) | _data[table + i * offSize + b];

                offsets[i] = value;
            }

            var data = table + (count + 1) * offSize - 1;
            var end = data + offsets[count];
            if (end > _end || offsets[count] < 0)
                throw new InvalidDataException("The CFF font has an index that runs past the table.");

            return new CffIndex(_data, offset, end, offsets, data);
        }

        private static Dictionary<int, double[]> ReadDict(ReadOnlySpan<byte> data)
        {
            var dict = new Dictionary<int, double[]>();
            var operands = new List<double>(8);

            for (var i = 0; i < data.Length;)
            {
                var b = data[i];
                if (b <= 21)
                {
                    int key = b;
                    i++;
                    if (b == 12)
                    {
                        key = 1200 + data[i];
                        i++;
                    }

                    dict[key] = [.. operands];
                    operands.Clear();
                    continue;
                }

                switch (b)
                {
                    case 28:
                        operands.Add(BinaryPrimitives.ReadInt16BigEndian(data[(i + 1)..]));
                        i += 3;
                        break;
                    case 29:
                        operands.Add(BinaryPrimitives.ReadInt32BigEndian(data[(i + 1)..]));
                        i += 5;
                        break;
                    case 30:
                        i += ReadReal(data[i..], out var real);
                        operands.Add(real);
                        break;
                    case >= 32 and <= 246:
                        operands.Add(b - 139);
                        i++;
                        break;
                    case >= 247 and <= 250:
                        operands.Add((b - 247) * 256 + data[i + 1] + 108);
                        i += 2;
                        break;
                    case >= 251 and <= 254:
                        operands.Add(-((b - 251) * 256) - data[i + 1] - 108);
                        i += 2;
                        break;
                    default:
                        throw new InvalidDataException($"The CFF font has an invalid dictionary byte {b}.");
                }

                if (operands.Count > 48)
                    throw new InvalidDataException("The CFF font has a dictionary entry with too many operands.");
            }

            return dict;
        }

        /// <summary>A real number, written as nibbles: digits, a point, an exponent and a terminator.</summary>
        private static int ReadReal(ReadOnlySpan<byte> data, out double value)
        {
            var text = new System.Text.StringBuilder(16);
            var length = 1;
            for (var i = 1; i < data.Length && text.Length < 64; i++, length++)
            {
                foreach (var nibble in new[] { data[i] >> 4, data[i] & 0xF })
                {
                    switch (nibble)
                    {
                        case <= 9:
                            text.Append((char)('0' + nibble));
                            break;
                        case 0xA:
                            text.Append('.');
                            break;
                        case 0xB:
                            text.Append('E');
                            break;
                        case 0xC:
                            text.Append("E-");
                            break;
                        case 0xE:
                            text.Append('-');
                            break;
                        case 0xF:
                            value = Parse(text.ToString());
                            return length + 1;
                        default:
                            break;
                    }
                }
            }

            value = Parse(text.ToString());
            return length;

            static double Parse(string text) =>
                double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        // ---- Writing ------------------------------------------------------------------------------

        private static byte[] Write(
            string name,
            Dictionary<int, double[]> top,
            byte[] globalSubrs,
            byte[][] charStrings,
            byte[] fdSelect,
            List<(byte[] Dict, byte[] Subrs)> privateDicts,
            int glyphCount)
        {
            var nameIndex = WriteIndex([System.Text.Encoding.ASCII.GetBytes(name)]);

            // The only strings the subset needs name the character collection it belongs to.
            var stringIndex = WriteIndex([System.Text.Encoding.ASCII.GetBytes("Adobe"), System.Text.Encoding.ASCII.GetBytes("Identity")]);
            var charset = WriteIdentityCharset(glyphCount);
            var charStringIndex = WriteIndex(charStrings);

            var header = new byte[] { 1, 0, 4, 4 };

            // Every offset is written in its longest form, so the layout can be measured before it is filled in.
            byte[] BuildTop(int charsetOffset, int fdSelectOffset, int charStringsOffset, int fdArrayOffset)
            {
                var dict = new Dictionary<int, double[]>
                {
                    [1230] = [391, 392, 0],           // ROS: Adobe-Identity-0
                    [1234] = [glyphCount],            // CIDCount
                    [15] = [charsetOffset],
                    [17] = [charStringsOffset],
                    [1236] = [fdArrayOffset],
                    [1237] = [fdSelectOffset],
                };

                foreach (var key in (int[])[5, 1207])  // FontBBox and FontMatrix, if the font gives them
                {
                    if (top.TryGetValue(key, out var value))
                        dict[key] = value;
                }

                return WriteDict(dict);
            }

            byte[] BuildFdArray(int[] privateOffsets)
            {
                var dicts = new byte[privateDicts.Count][];
                for (var i = 0; i < privateDicts.Count; i++)
                {
                    dicts[i] = WriteDict(new Dictionary<int, double[]>
                    {
                        [18] = [privateDicts[i].Dict.Length, privateOffsets[i]],
                    });
                }

                return WriteIndex(dicts);
            }

            // First pass: measure with placeholder offsets, which are the same size as the real ones.
            var placeholders = new int[privateDicts.Count];
            var topSize = WriteIndex([BuildTop(0, 0, 0, 0)]).Length;
            var fdArraySize = BuildFdArray(placeholders).Length;

            var offset = header.Length + nameIndex.Length + topSize + stringIndex.Length + globalSubrs.Length;
            var charsetOffset = offset;
            offset += charset.Length;
            var fdSelectOffset = offset;
            offset += fdSelect.Length;
            var charStringsOffset = offset;
            offset += charStringIndex.Length;
            var fdArrayOffset = offset;
            offset += fdArraySize;

            var privateOffsets = new int[privateDicts.Count];
            for (var i = 0; i < privateDicts.Count; i++)
            {
                privateOffsets[i] = offset;
                offset += privateDicts[i].Dict.Length + privateDicts[i].Subrs.Length;
            }

            // Second pass: the same layout with the offsets filled in.
            var topIndex = WriteIndex([BuildTop(charsetOffset, fdSelectOffset, charStringsOffset, fdArrayOffset)]);
            var fdArray = BuildFdArray(privateOffsets);
            if (topIndex.Length != topSize || fdArray.Length != fdArraySize)
                throw new InvalidDataException("The CFF subset did not come out the size it was measured at.");

            using var output = new MemoryStream(offset);
            output.Write(header);
            output.Write(nameIndex);
            output.Write(topIndex);
            output.Write(stringIndex);
            output.Write(globalSubrs);
            output.Write(charset);
            output.Write(fdSelect);
            output.Write(charStringIndex);
            output.Write(fdArray);
            foreach (var (dict, subrs) in privateDicts)
            {
                output.Write(dict);
                output.Write(subrs);
            }

            return output.ToArray();
        }

        /// <summary>A charset that gives every glyph the identifier of its own glyph number.</summary>
        private static byte[] WriteIdentityCharset(int glyphCount)
        {
            var output = new byte[5];
            output[0] = 2; // format 2: ranges with a 16-bit count
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(1), 1);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(3), (ushort)Math.Max(glyphCount - 2, 0));
            return output;
        }

        private static byte[] WriteIndex(byte[][] items)
        {
            if (items.Length == 0)
                return [0, 0];

            var total = items.Sum(item => item.Length);
            var offSize = total + 1 <= 0xFF ? 1 : total + 1 <= 0xFFFF ? 2 : total + 1 <= 0xFFFFFF ? 3 : 4;
            var output = new byte[3 + (items.Length + 1) * offSize + total];

            BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)items.Length);
            output[2] = (byte)offSize;

            var offset = 1;
            var cursor = 3 + (items.Length + 1) * offSize;
            for (var i = 0; i <= items.Length; i++)
            {
                for (var b = 0; b < offSize; b++)
                    output[3 + i * offSize + b] = (byte)(offset >> ((offSize - 1 - b) * 8));

                if (i < items.Length)
                {
                    items[i].CopyTo(output, cursor);
                    cursor += items[i].Length;
                    offset += items[i].Length;
                }
            }

            return output;
        }

        /// <summary>
        /// Writes a dictionary. Every integer takes its five byte form, so the size of the result does not
        /// depend on the values — which is what lets the layout be measured before the offsets are known.
        /// </summary>
        private static byte[] WriteDict(Dictionary<int, double[]> dict, params (int Key, double Value)[] extra)
        {
            using var output = new MemoryStream(64);
            foreach (var (key, operands) in dict.OrderBy(entry => entry.Key))
            {
                foreach (var operand in operands)
                    WriteOperand(output, operand);

                WriteOperator(output, key);
            }

            foreach (var (key, value) in extra)
            {
                WriteOperand(output, value);
                WriteOperator(output, key);
            }

            return output.ToArray();
        }

        private static void WriteOperand(MemoryStream output, double value)
        {
            if (value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue)
            {
                Span<byte> buffer = stackalloc byte[5];
                buffer[0] = 29;
                BinaryPrimitives.WriteInt32BigEndian(buffer[1..], (int)value);
                output.Write(buffer);
                return;
            }

            // A number that is not an integer is written as the nibble encoded real the format defines.
            var text = value.ToString("G8", System.Globalization.CultureInfo.InvariantCulture);
            var nibbles = new List<int>(text.Length + 1);
            foreach (var c in text)
            {
                nibbles.Add(c switch
                {
                    >= '0' and <= '9' => c - '0',
                    '.' => 0xA,
                    'E' or 'e' => 0xB,
                    '-' => nibbles.Count == 0 ? 0xE : 0xC,
                    _ => 0xF,
                });
            }

            nibbles.Add(0xF);
            if (nibbles.Count % 2 == 1)
                nibbles.Add(0xF);

            output.WriteByte(30);
            for (var i = 0; i < nibbles.Count; i += 2)
                output.WriteByte((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }

        private static void WriteOperator(MemoryStream output, int key)
        {
            if (key >= 1200)
            {
                output.WriteByte(12);
                output.WriteByte((byte)(key - 1200));
            }
            else
            {
                output.WriteByte((byte)key);
            }
        }
    }

    /// <summary>One of the indexed blocks a CFF font is built from.</summary>
    private readonly struct CffIndex(byte[] data, int start, int end, int[] offsets, int items)
    {
        public int Count => Math.Max(offsets.Length - 1, 0);

        public int End => end;

        public ReadOnlySpan<byte> Item(int index) => Memory(index).Span;

        public ReadOnlyMemory<byte> Memory(int index)
        {
            var from = items + offsets[index];
            var to = items + offsets[index + 1];
            return to >= from && to <= end ? data.AsMemory(from, to - from) : ReadOnlyMemory<byte>.Empty;
        }

        public List<ReadOnlyMemory<byte>> Items()
        {
            var result = new List<ReadOnlyMemory<byte>>(Count);
            for (var i = 0; i < Count; i++)
                result.Add(Memory(i));

            return result;
        }

        /// <summary>The block as it stands, for the parts that are carried over unchanged.</summary>
        public byte[] Raw() => data.AsSpan(start, end - start).ToArray();
    }
}
