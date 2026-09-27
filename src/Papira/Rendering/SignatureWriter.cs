using System.Globalization;
using System.Security.Cryptography;
using Papira.Pdf;

namespace Papira.Rendering;

/// <summary>
/// Where in the file the signature goes. The signature covers the whole document except itself, so the
/// places it is written into are kept aside while the file is built and filled in once it is complete.
/// </summary>
internal sealed class SignaturePlaceholder
{
    /// <summary>The offset of the first digit of the byte range, which is only known at the end.</summary>
    public long RangeOffset { get; init; }

    /// <summary>The offset of the angle bracket that opens the signature itself.</summary>
    public long ContentsOffset { get; init; }

    /// <summary>How many hex digits are kept for the signature.</summary>
    public int ContentsLength { get; init; }
}

/// <summary>
/// Signs a finished document: the signature is computed over every byte of the file apart from the
/// place it is written into, which is what lets a reader tell that nothing has changed since.
/// </summary>
internal static class SignatureWriter
{
    /// <summary>How much room the signature is given, in bytes. A certificate chain rarely needs half of it.</summary>
    private const int SignatureBytes = 8192;

    /// <summary>The width of each number of the byte range, so that they can be filled in afterwards.</summary>
    private const int NumberWidth = 10;

    /// <summary>
    /// Writes the signature dictionary with room kept for what is only known once the file is finished:
    /// the ranges the signature covers, and the signature itself.
    /// </summary>
    public static SignaturePlaceholder Write(PdfWriter writer, int id, PdfSignatureSettings settings, DateTimeOffset date)
    {
        var o = writer.Out;
        writer.BeginObject(id);
        o.Ascii("<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached/ByteRange[0 ");

        var rangeOffset = writer.Position;
        o.Ascii(new string(' ', NumberWidth)).Space().Ascii(new string(' ', NumberWidth)).Space().Ascii(new string(' ', NumberWidth));
        o.Ascii("]/Contents");

        var contentsOffset = writer.Position;
        o.Byte((byte)'<');
        for (var i = 0; i < SignatureBytes * 2; i++)
            o.Byte((byte)'0');

        o.Byte((byte)'>');

        o.Ascii("/M");
        o.AsciiString(PdfDate(date));

        var name = settings.Name ?? Subject(settings);
        if (!string.IsNullOrEmpty(name))
        {
            o.Ascii("/Name");
            o.TextString(name);
        }

        if (!string.IsNullOrEmpty(settings.Reason))
        {
            o.Ascii("/Reason");
            o.TextString(settings.Reason);
        }

        if (!string.IsNullOrEmpty(settings.Location))
        {
            o.Ascii("/Location");
            o.TextString(settings.Location);
        }

        if (!string.IsNullOrEmpty(settings.ContactInfo))
        {
            o.Ascii("/ContactInfo");
            o.TextString(settings.ContactInfo);
        }

        o.Ascii(">>");
        writer.EndObject();

        return new SignaturePlaceholder
        {
            RangeOffset = rangeOffset,
            ContentsOffset = contentsOffset,
            ContentsLength = SignatureBytes * 2,
        };
    }

    /// <summary>The name the certificate was issued to, which stands in for a signer who gave none.</summary>
    private static string Subject(PdfSignatureSettings settings)
    {
        var common = settings.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        return string.IsNullOrEmpty(common) ? settings.Certificate.Subject : common;
    }

    /// <summary>
    /// Fills in what was kept aside: the two stretches of the file the signature covers, and the
    /// signature over them.
    /// </summary>
    public static void Sign(Stream file, SignaturePlaceholder placeholder, PdfSignatureSettings settings, DateTimeOffset date)
    {
        var length = file.Length;
        var first = placeholder.ContentsOffset;
        var second = placeholder.ContentsOffset + placeholder.ContentsLength + 2;

        // [0 first second (length - second)]: everything but the signature itself.
        WriteNumbers(file, placeholder.RangeOffset, first, second, length - second);

        var digest = Digest(file, first, second, length - second);
        var signature = CmsSignature.Create(digest, settings.Certificate, settings.Chain, date);
        if (signature.Length * 2 > placeholder.ContentsLength)
        {
            throw new InvalidOperationException(
                $"The signature needs {signature.Length} bytes, more than the {placeholder.ContentsLength / 2} Papira keeps for it. " +
                "A shorter certificate chain will fit.");
        }

        file.Position = placeholder.ContentsOffset + 1;
        Span<byte> hex = stackalloc byte[2];
        foreach (var b in signature)
        {
            hex[0] = HexDigit(b >> 4);
            hex[1] = HexDigit(b & 0xF);
            file.Write(hex);
        }
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);

    private static void WriteNumbers(Stream file, long offset, long first, long second, long secondLength)
    {
        file.Position = offset;
        foreach (var value in (long[])[first, second, secondLength])
        {
            var text = value.ToString(CultureInfo.InvariantCulture).PadLeft(NumberWidth);
            file.Write(System.Text.Encoding.ASCII.GetBytes(text));
            file.Position++;
        }
    }

    /// <summary>The digest of the file with the signature left out of it.</summary>
    private static byte[] Digest(Stream file, long firstLength, long secondStart, long secondLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];

        foreach (var (start, count) in ((long Start, long Count)[])[(0, firstLength), (secondStart, secondLength)])
        {
            file.Position = start;
            var left = count;
            while (left > 0)
            {
                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0)
                    throw new InvalidOperationException("The document ended before the signature could be computed over it.");

                hash.AppendData(buffer, 0, read);
                left -= read;
            }
        }

        return hash.GetHashAndReset();
    }

    /// <summary>A date as a PDF writes one.</summary>
    private static string PdfDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }
}
