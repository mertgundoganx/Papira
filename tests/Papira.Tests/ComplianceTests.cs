using System.Text;
using System.Text.RegularExpressions;
using Papira.Pdf;

namespace Papira.Tests;

/// <summary>
/// Attachments, PDF/A and encryption. The generated PDF/A files were checked with veraPDF (PASS for 2b and 3b)
/// and the encrypted ones were opened with pypdf; these tests guard the structure that made them valid.
/// </summary>
public class ComplianceTests
{
    private static readonly byte[] InvoiceXml = Encoding.UTF8.GetBytes("<Invoice><ID>PAP-1</ID></Invoice>");

    private static Document SimpleDocument() =>
        Document.Create(d => d.Page(p =>
        {
            p.Margin(40);
            p.Content().Column(col =>
            {
                col.Item().Text("Gizli belge: ĞÜŞİÖÇ ₺1.234,56");
                col.Item().Hyperlink("https://example.com").Text("bağlantı");
            });
        })).WithMetadata(new DocumentMetadata { Title = "Şifreli Fatura", Author = "Papira" });

    // ---- Attachments -----------------------------------------------------------------------------

    [Fact]
    public void Attachment_is_embedded_and_listed_in_the_name_tree()
    {
        var pdf = TestDocuments.Inspect(SimpleDocument()
            .WithAttachment(new DocumentAttachment("fatura.xml", InvoiceXml)
            {
                MediaType = "text/xml",
                Relationship = AttachmentRelationship.Data,
                Description = "Makine tarafından okunabilir fatura",
            })
            .GeneratePdf());

        Assert.Contains("/Type/EmbeddedFile/Subtype/text#2Fxml", pdf.Raw);
        Assert.Contains("/Type/Filespec", pdf.Raw);
        Assert.Contains("/AFRelationship/Data", pdf.Raw);
        Assert.Contains("/Names<</EmbeddedFiles<</Names[", pdf.Raw);
        Assert.Contains("/AF[", pdf.Raw);
        Assert.Contains(pdf.Streams(), s => s.Contains("<Invoice><ID>PAP-1</ID></Invoice>"));
    }

    [Fact]
    public void Attachments_are_sorted_by_name_as_the_name_tree_requires()
    {
        var pdf = TestDocuments.Inspect(SimpleDocument()
            .WithAttachment("zzz.txt", [1])
            .WithAttachment("aaa.txt", [2])
            .GeneratePdf());

        var names = Regex.Matches(pdf.Raw, @"/Names\[(.*?)\]", RegexOptions.Singleline)[0].Groups[1].Value;
        Assert.True(
            names.IndexOf("FEFF00610061", StringComparison.Ordinal) < names.IndexOf("FEFF007A007A", StringComparison.Ordinal),
            "aaa.txt must come before zzz.txt");
    }

    // ---- PDF/A -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(PdfStandard.PdfA2b, 2)]
    [InlineData(PdfStandard.PdfA3b, 3)]
    public void Pdf_a_documents_carry_metadata_and_an_output_intent(PdfStandard standard, int part)
    {
        var pdf = TestDocuments.Inspect(SimpleDocument()
            .WithSettings(new DocumentSettings { Standard = standard })
            .GeneratePdf());

        // The XMP packet is UTF-8; streams are read as raw bytes.
        var xmp = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(Assert.Single(pdf.Streams(), s => s.Contains("<x:xmpmeta"))));
        Assert.Contains($"<pdfaid:part>{part}</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Şifreli Fatura</rdf:li>", xmp);

        Assert.Contains("/Type/OutputIntent/S/GTS_PDFA1", pdf.Raw);
        Assert.Contains("/DestOutputProfile", pdf.Raw);
        Assert.Contains("/Type/Metadata/Subtype/XML", pdf.Raw);
    }

    [Fact]
    public void Pdf_a_links_get_an_appearance_stream()
    {
        var plain = TestDocuments.Inspect(SimpleDocument().GeneratePdf());
        var archived = TestDocuments.Inspect(SimpleDocument()
            .WithSettings(new DocumentSettings { Standard = PdfStandard.PdfA2b })
            .GeneratePdf());

        Assert.DoesNotContain("/AP<</N", plain.Raw);
        Assert.Contains("/AP<</N", archived.Raw);
        Assert.Contains("/Subtype/Form/Resources<<>>/BBox", archived.Raw);
    }

    [Fact]
    public void Generated_icc_profile_is_a_valid_rgb_display_profile()
    {
        var profile = IccProfile.CreateSrgb();

        Assert.Equal(profile.Length, (profile[0] << 24) | (profile[1] << 16) | (profile[2] << 8) | profile[3]);
        Assert.Equal("mntr", Encoding.ASCII.GetString(profile, 12, 4));
        Assert.Equal("RGB ", Encoding.ASCII.GetString(profile, 16, 4));
        Assert.Equal("XYZ ", Encoding.ASCII.GetString(profile, 20, 4));
        Assert.Equal("acsp", Encoding.ASCII.GetString(profile, 36, 4));

        foreach (var tag in new[] { "desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "cprt" })
            Assert.Contains(tag, Encoding.ASCII.GetString(profile));
    }

    [Fact]
    public void Pdf_a2b_rejects_attachments()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SimpleDocument()
            .WithAttachment("fatura.xml", InvoiceXml)
            .WithSettings(new DocumentSettings { Standard = PdfStandard.PdfA2b })
            .GeneratePdf());

        Assert.Contains("PdfA3b", ex.Message);
    }

    [Fact]
    public void Pdf_a_documents_cannot_be_encrypted()
    {
        Assert.Throws<InvalidOperationException>(() => SimpleDocument()
            .WithSettings(new DocumentSettings
            {
                Standard = PdfStandard.PdfA3b,
                Encryption = new PdfEncryptionSettings { UserPassword = "x" },
            })
            .GeneratePdf());
    }

    // ---- Encryption ------------------------------------------------------------------------------

    [Fact]
    public void Encrypted_documents_hide_their_content_and_strings()
    {
        var bytes = SimpleDocument()
            .WithSettings(new DocumentSettings { Encryption = new PdfEncryptionSettings { UserPassword = "gizli", OwnerPassword = "patron" } })
            .GeneratePdf();
        var raw = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-2.0", raw); // AES-256 is a PDF 2.0 feature
        Assert.Contains("/Filter/Standard/V 5/R 6/Length 256", raw);
        Assert.Contains("/CFM/AESV3", raw);
        Assert.Contains("/Encrypt ", raw);

        // The title is stored encrypted, so it must not appear as a plain UTF-16 hex string.
        Assert.DoesNotContain("FEFF015E0069006600720065006C0069", raw);
        Assert.DoesNotContain("example.com", raw);
    }

    [Fact]
    public void Encryption_keys_have_the_sizes_the_standard_requires()
    {
        var bytes = SimpleDocument()
            .WithSettings(new DocumentSettings { Encryption = new PdfEncryptionSettings { UserPassword = "gizli" } })
            .GeneratePdf();
        var raw = Encoding.Latin1.GetString(bytes);

        Assert.Equal(96, Regex.Match(raw, @"/O<([0-9A-F]+)>").Groups[1].Value.Length); // 48 bytes
        Assert.Equal(96, Regex.Match(raw, @"/U<([0-9A-F]+)>").Groups[1].Value.Length);
        Assert.Equal(64, Regex.Match(raw, @"/OE<([0-9A-F]+)>").Groups[1].Value.Length); // 32 bytes
        Assert.Equal(64, Regex.Match(raw, @"/UE<([0-9A-F]+)>").Groups[1].Value.Length);
        Assert.Equal(32, Regex.Match(raw, @"/Perms<([0-9A-F]+)>").Groups[1].Value.Length); // 16 bytes
    }

    [Theory]
    [InlineData(PdfPermissions.All, -4)]                      // every permission granted
    [InlineData(PdfPermissions.None, -3904)]                  // nothing but opening the file
    [InlineData(PdfPermissions.Print, -3900)]                 // bit 3 set
    [InlineData(PdfPermissions.Print | PdfPermissions.CopyContent, -3884)]
    public void Permission_bits_follow_the_specification(PdfPermissions permissions, int expected)
    {
        var bytes = SimpleDocument()
            .WithSettings(new DocumentSettings { Encryption = new PdfEncryptionSettings { Permissions = permissions } })
            .GeneratePdf();

        var raw = Encoding.Latin1.GetString(bytes);
        Assert.Contains($"/P {expected}/EncryptMetadata true", raw);
    }

    [Fact]
    public void Each_encrypted_file_uses_a_fresh_key()
    {
        var settings = new DocumentSettings { Encryption = new PdfEncryptionSettings { UserPassword = "gizli" } };
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        byte[] Render() => SimpleDocument().WithMetadata(new DocumentMetadata { CreationDate = date }).WithSettings(settings).GeneratePdf();

        Assert.NotEqual(Render(), Render());
    }
}
