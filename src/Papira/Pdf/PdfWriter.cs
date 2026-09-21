using System.IO.Compression;
using System.Security.Cryptography;

namespace Papira.Pdf;

/// <summary>
/// Sequential writer for the PDF file structure: header, indirect objects, cross-reference table and trailer.
/// Object numbers can be reserved up-front so objects may reference each other before they are written.
/// </summary>
internal sealed class PdfWriter : IDisposable
{
    private const int FlushThreshold = 128 * 1024;

    private readonly Stream _output;
    private readonly ByteBuffer _buffer = new(FlushThreshold + 16 * 1024);

    // Hash of everything written; the file identifier is derived from it, so identical input gives identical output.
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<long> _offsets = [0];
    private long _flushed;

    public PdfWriter(Stream output)
    {
        _output = output;
        // %âãÏÓ marker tells transfer tools the file is binary.
        _buffer.Ascii("%PDF-1.7\n%").Bytes([0xE2, 0xE3, 0xCF, 0xD3]).NewLine();
    }

    private long Position => _flushed + _buffer.Length;

    /// <summary>Buffer for writing the body of the object currently being emitted.</summary>
    public ByteBuffer Out => _buffer;

    public int ReserveObject()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    public void BeginObject(int id)
    {
        _offsets[id] = Position;
        _buffer.Int(id).Ascii(" 0 obj\n");
    }

    public void EndObject()
    {
        _buffer.Ascii("\nendobj\n");
        if (_buffer.Length >= FlushThreshold)
            Flush();
    }

    /// <summary>Writes a complete stream object. <paramref name="dictionaryEntries"/> must not contain /Length.</summary>
    public void WriteStream(int id, ReadOnlySpan<byte> data, Action<ByteBuffer>? dictionaryEntries, bool flateEncoded)
    {
        BeginObject(id);
        _buffer.Ascii("<</Length ").Int(data.Length);
        if (flateEncoded)
            _buffer.Ascii("/Filter/FlateDecode");
        dictionaryEntries?.Invoke(_buffer);
        _buffer.Ascii(">>\nstream\n");

        if (data.Length > FlushThreshold)
        {
            Flush();
            _output.Write(data);
            _hash.AppendData(data);
            _flushed += data.Length;
        }
        else
        {
            _buffer.Bytes(data);
        }

        _buffer.Ascii("\nendstream");
        EndObject();
    }

    public void WriteTrailer(int catalogId, int infoId)
    {
        for (var i = 1; i < _offsets.Count; i++)
        {
            if (_offsets[i] < 0)
                throw new InvalidOperationException($"PDF object {i} was reserved but never written.");
        }

        var xrefOffset = Position;
        _buffer.Ascii("xref\n0 ").Int(_offsets.Count).Ascii("\n0000000000 65535 f\r\n");

        Span<byte> entry = stackalloc byte[20];
        for (var i = 1; i < _offsets.Count; i++)
        {
            var offset = _offsets[i];
            for (var d = 9; d >= 0; d--)
            {
                entry[d] = (byte)('0' + offset % 10);
                offset /= 10;
            }

            "0000000000 00000 n\r\n"u8[10..].CopyTo(entry[10..]);
            _buffer.Bytes(entry);

            if (_buffer.Length >= FlushThreshold)
                Flush();
        }

        Flush();
        Span<byte> digest = stackalloc byte[32];
        _hash.GetHashAndReset(digest);
        var fileId = digest[..16];

        _buffer.Ascii("trailer\n<</Size ").Int(_offsets.Count)
            .Ascii("/Root ").Int(catalogId).Ascii(" 0 R")
            .Ascii("/Info ").Int(infoId).Ascii(" 0 R")
            .Ascii("/ID[<");
        foreach (var b in fileId) _buffer.Hex8(b);
        _buffer.Ascii("><");
        foreach (var b in fileId) _buffer.Hex8(b);
        _buffer.Ascii(">]>>\nstartxref\n").Int(xrefOffset).Ascii("\n%%EOF\n");
        Flush();
    }

    private void Flush()
    {
        _hash.AppendData(_buffer.Span);
        _buffer.CopyTo(_output);
        _flushed += _buffer.Length;
        _buffer.Clear();
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _hash.Dispose();
    }

    public static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new MemoryStream(data.Length / 3 + 64);
        using (var zlib = new ZLibStream(output, level, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }
}
