using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;

namespace Papira.Pdf;

/// <summary>
/// Growable, pooled byte buffer with allocation-free helpers for writing PDF syntax
/// (integers, reals, hex strings, text strings).
/// </summary>
internal sealed class ByteBuffer : IDisposable
{
    private static ReadOnlySpan<byte> HexDigits => "0123456789ABCDEF"u8;

    private byte[] _buffer;
    private int _length;

    public ByteBuffer(int initialCapacity = 4096)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 256));
    }

    public int Length => _length;
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public void Clear() => _length = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
            Grow(count);
        return _buffer.AsSpan(_length, count);
    }

    private void Grow(int count)
    {
        var newSize = Math.Max(_buffer.Length * 2, _length + count);
        var next = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _length).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteBuffer Byte(byte value)
    {
        if (_length == _buffer.Length)
            Grow(1);
        _buffer[_length++] = value;
        return this;
    }

    public ByteBuffer Bytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Reserve(value.Length));
        _length += value.Length;
        return this;
    }

    /// <summary>Writes an ASCII-only string (PDF keywords, names, numbers already formatted).</summary>
    public ByteBuffer Ascii(string value)
    {
        var span = Reserve(value.Length);
        for (var i = 0; i < value.Length; i++)
            span[i] = (byte)value[i];
        _length += value.Length;
        return this;
    }

    public ByteBuffer Space() => Byte((byte)' ');
    public ByteBuffer NewLine() => Byte((byte)'\n');

    public ByteBuffer Int(long value)
    {
        var span = Reserve(20);
        Utf8Formatter.TryFormat(value, span, out var written);
        _length += written;
        return this;
    }

    /// <summary>Writes a real number with at most 3 decimals, never in exponent notation.</summary>
    public ByteBuffer Real(double value)
    {
        if (!double.IsFinite(value))
            value = 0;

        var scaled = (long)Math.Round(value * 1000d, MidpointRounding.AwayFromZero);
        if (scaled < 0)
        {
            Byte((byte)'-');
            scaled = -scaled;
        }

        var integer = scaled / 1000;
        var fraction = (int)(scaled % 1000);
        Int(integer);

        if (fraction != 0)
        {
            var span = Reserve(4);
            span[0] = (byte)'.';
            span[1] = (byte)('0' + fraction / 100);
            span[2] = (byte)('0' + fraction / 10 % 10);
            span[3] = (byte)('0' + fraction % 10);
            var count = 4;
            while (span[count - 1] == '0')
                count--;
            _length += count;
        }

        return this;
    }

    public ByteBuffer Hex16(ushort value)
    {
        var span = Reserve(4);
        span[0] = HexDigits[value >> 12];
        span[1] = HexDigits[(value >> 8) & 0xF];
        span[2] = HexDigits[(value >> 4) & 0xF];
        span[3] = HexDigits[value & 0xF];
        _length += 4;
        return this;
    }

    public ByteBuffer Hex8(byte value)
    {
        var span = Reserve(2);
        span[0] = HexDigits[value >> 4];
        span[1] = HexDigits[value & 0xF];
        _length += 2;
        return this;
    }

    /// <summary>Writes a text string as UTF-16BE hex string with BOM, safe for any Unicode content.</summary>
    public ByteBuffer TextString(string value)
    {
        Ascii("<FEFF");
        foreach (var c in value)
            Hex16(c);
        return Byte((byte)'>');
    }

    /// <summary>
    /// Writes a literal string for byte-string values such as URIs. Characters outside printable ASCII
    /// are percent-encoded as UTF-8; parentheses and backslashes are escaped.
    /// </summary>
    public ByteBuffer AsciiString(string value)
    {
        Byte((byte)'(');
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is >= 0x20 and < 0x7F)
            {
                if (rune.Value is '(' or ')' or '\\')
                    Byte((byte)'\\');
                Byte((byte)rune.Value);
                continue;
            }

            var count = rune.EncodeToUtf8(utf8);
            for (var i = 0; i < count; i++)
                Byte((byte)'%').Hex8(utf8[i]);
        }

        return Byte((byte)')');
    }

    public void CopyTo(Stream stream) => stream.Write(_buffer, 0, _length);

    public byte[] ToArray() => Span.ToArray();

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _length = 0;
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
