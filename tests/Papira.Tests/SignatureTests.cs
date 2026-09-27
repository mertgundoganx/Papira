using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Signed documents. The signatures these tests produce were checked with OpenSSL, which verifies them
/// against the certificate and reports the document as unchanged — and reports it as changed as soon as
/// a single bit of it is flipped. The structure of the message was read back with asn1crypto and matches
/// what RFC 5652 prescribes for a detached signature.
/// </summary>
public class SignatureTests : IDisposable
{
    private readonly X509Certificate2 _certificate = SelfSigned();
    private bool _disposed;

    /// <summary>A certificate made for the test, so that nothing has to be installed to run it.</summary>
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Papira Test, O=Papira", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // The key has to survive the certificate being written out and read back in.
        return Load(certificate.Export(X509ContentType.Pkcs12), withKey: true);
    }

    /// <summary>Reads a certificate, whichever way the runtime offers.</summary>
    private static X509Certificate2 Load(byte[] data, bool withKey)
    {
#if NET9_0_OR_GREATER
        return withKey ? X509CertificateLoader.LoadPkcs12(data, null) : X509CertificateLoader.LoadCertificate(data);
#else
        return new X509Certificate2(data);
#endif
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _certificate.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private byte[] Signed(Action<IContainer>? content = null, PdfSignatureSettings? signature = null) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A5).Margin(30);
            (content ?? (c => c.Text("İmzalanacak belge")))(page.Content());
        }))
        .WithSettings(new DocumentSettings
        {
            Signature = signature ?? new PdfSignatureSettings { Certificate = _certificate },
        })
        .GeneratePdf();

    /// <summary>The two stretches of the file the signature covers, as the document itself states them.</summary>
    private static (int First, int Second, int SecondLength) Ranges(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var match = Regex.Match(text, @"/ByteRange\[(\d+) +(\d+) +(\d+) +(\d+)\]");
        Assert.True(match.Success, "the signature says which bytes it covers");

        int Number(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        Assert.Equal(0, Number(1));
        return (Number(2), Number(3), Number(4));
    }

    [Fact]
    public void A_signed_document_says_how_it_was_signed()
    {
        var pdf = Inspect(Signed());

        Assert.Contains("/Type/Sig", pdf.Raw);
        Assert.Contains("/Filter/Adobe.PPKLite", pdf.Raw);
        Assert.Contains("/SubFilter/adbe.pkcs7.detached", pdf.Raw);
        Assert.Contains("/SigFlags 3", pdf.Raw);
        Assert.Contains("/FT/Sig", pdf.Raw);
    }

    [Fact]
    public void The_signature_covers_every_byte_of_the_file_except_itself()
    {
        var pdf = Signed();
        var (first, second, secondLength) = Ranges(pdf);

        // The gap between the two stretches is the signature, written as hex between angle brackets.
        Assert.Equal((byte)'<', pdf[first]);
        Assert.Equal((byte)'>', pdf[second - 1]);
        Assert.Equal(pdf.Length, second + secondLength);
    }

    [Fact]
    public void The_signature_is_a_message_that_carries_the_certificate_and_the_digest()
    {
        var pdf = Signed();
        var (first, second, secondLength) = Ranges(pdf);
        var signature = Signature(pdf, first, second);

        // Read back as CMS: the same digest the document has, and the certificate that signed it.
        var content = new byte[first + secondLength];
        pdf.AsSpan(0, first).CopyTo(content);
        pdf.AsSpan(second, secondLength).CopyTo(content.AsSpan(first));
        var digest = SHA256.HashData(content);

        Assert.Contains(digest, Windows(signature, digest.Length));
        Assert.Contains(_certificate.RawData, Windows(signature, _certificate.RawData.Length));
    }

    /// <summary>Every stretch of the given length, so that a structure can be looked for inside another.</summary>
    private static IEnumerable<byte[]> Windows(byte[] data, int length)
    {
        for (var i = 0; i + length <= data.Length; i++)
            yield return data.AsSpan(i, length).ToArray();
    }

    private static byte[] Signature(byte[] pdf, int first, int second)
    {
        var hex = System.Text.Encoding.ASCII.GetString(pdf, first + 1, second - first - 2).TrimEnd('0');
        if (hex.Length % 2 == 1)
            hex += "0";

        return Convert.FromHexString(hex);
    }

    [Fact]
    public void A_document_with_no_place_for_a_signature_is_still_signed()
    {
        // Nothing is drawn for it, but the field and the signature are there.
        var pdf = Inspect(Signed());

        Assert.Contains("/Type/Sig", pdf.Raw);
        Assert.Contains("Signature1", pdf.ExtractStrings());
    }

    [Fact]
    public void The_field_that_is_signed_can_be_chosen()
    {
        var pdf = Inspect(Signed(
            c => c.Column(column =>
            {
                column.Item().SignatureField("first").Tooltip("İlk");
                column.Item().SignatureField("second").Tooltip("İkinci");
            }),
            new PdfSignatureSettings { Certificate = _certificate, FieldName = "second" }));

        // Both fields are there, and exactly one of them carries the signature.
        Assert.Equal(2, Regex.Count(pdf.Raw, "/FT/Sig"));

        var signature = Regex.Match(pdf.Raw, @"(\d+) 0 obj\n<</Type/Sig").Groups[1].Value;
        var owners = Regex.Matches(pdf.Raw, @"/FT/Sig/T(?<name><[0-9A-F]+>).*?/V " + signature + " 0 R");
        Assert.Single(owners);

        // The name of that field, which a PDF writes as UTF-16 hex.
        var hex = owners[0].Groups["name"].Value.Trim('<', '>')[4..];
        var name = string.Concat(Enumerable.Range(0, hex.Length / 4)
            .Select(i => (char)Convert.ToUInt16(hex.Substring(i * 4, 4), 16)));
        Assert.Equal("second", name);
    }

    [Fact]
    public void A_field_that_does_not_exist_is_refused_with_a_clear_message()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Signed(c => c.Text("Metin"), new PdfSignatureSettings { Certificate = _certificate, FieldName = "yok" }));

        Assert.Contains("no signature field named 'yok'", exception.Message);
    }

    [Fact]
    public void What_the_signature_is_for_is_written_beside_it()
    {
        var pdf = Inspect(Signed(null, new PdfSignatureSettings
        {
            Certificate = _certificate,
            Reason = "Onaylıyorum",
            Location = "İstanbul",
            ContactInfo = "info@example.com",
            Name = "Yetkili",
            Date = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3)),
        }));

        var strings = pdf.ExtractStrings();
        Assert.Contains("Onaylıyorum", strings);
        Assert.Contains("İstanbul", strings);
        Assert.Contains("info@example.com", strings);
        Assert.Contains("Yetkili", strings);
        Assert.Contains("D:20260927120000+03'00'", strings);
    }

    [Fact]
    public void A_signed_document_cannot_be_encrypted()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Document.Create(d => d.Page(page =>
            {
                page.Size(PageSizes.A5).Margin(30);
                page.Content().Text("Metin");
            }))
            .WithSettings(new DocumentSettings
            {
                Signature = new PdfSignatureSettings { Certificate = _certificate },
                Encryption = new PdfEncryptionSettings { UserPassword = "secret" },
            })
            .GeneratePdf());

        Assert.Contains("cannot be encrypted", exception.Message);
    }

    [Fact]
    public void An_archivable_document_can_be_signed()
    {
        // PDF/A allows signatures, and CI checks the sample with veraPDF.
        var pdf = Inspect(Document.Create(d => d.Page(page =>
            {
                page.Size(PageSizes.A5).Margin(30);
                page.Content().Text("Arşivlik ve imzalı");
            }))
            .WithSettings(new DocumentSettings
            {
                Standard = PdfStandard.PdfA2b,
                Signature = new PdfSignatureSettings { Certificate = _certificate },
            })
            .GeneratePdf());

        Assert.Contains("/Type/Sig", pdf.Raw);
        Assert.Contains("pdfaid", pdf.Raw);
    }

    [Fact]
    public void A_certificate_without_its_key_says_what_is_missing()
    {
        using var publicOnly = Load(_certificate.RawData, withKey: false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => { _ = Signed(null, new PdfSignatureSettings { Certificate = publicOnly }); });

        Assert.Contains("no private key", exception.Message);
    }
}
