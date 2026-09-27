using System.Globalization;
using System.Text;

namespace Papira.Pdf;

/// <summary>Builds the XMP packet that archivable and accessible files carry in the document catalog.</summary>
internal static class XmpMetadata
{
    /// <summary>
    /// The packet for a document: its title and dates, the part of PDF/A it conforms to (zero when it is
    /// not one) and, for a tagged document, that it declares itself accessible.
    /// </summary>
    public static byte[] Create(DocumentMetadata metadata, DateTimeOffset date, int part, bool accessible = false)
    {
        var timestamp = date.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.Append("""
            <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/">

            """);
        if (part > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"      <pdfaid:part>{part}</pdfaid:part>\n");
            builder.Append("      <pdfaid:conformance>B</pdfaid:conformance>\n");
        }

        if (accessible)
            builder.Append("      <pdfuaid:part>1</pdfuaid:part>\n");

        builder.Append("    </rdf:Description>\n");

        // An archivable file has to declare every property it uses that the standard does not define,
        // which is how the accessibility identifier is allowed to appear beside the archiving one.
        if (accessible && part > 0)
        {
            builder.Append("""
                    <rdf:Description rdf:about="" xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/" xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#" xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#">
                      <pdfaExtension:schemas>
                        <rdf:Bag>
                          <rdf:li rdf:parseType="Resource">
                            <pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/</pdfaSchema:namespaceURI>
                            <pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>
                            <pdfaSchema:schema>PDF/UA identification schema</pdfaSchema:schema>
                            <pdfaSchema:property>
                              <rdf:Seq>
                                <rdf:li rdf:parseType="Resource">
                                  <pdfaProperty:category>internal</pdfaProperty:category>
                                  <pdfaProperty:description>PDF/UA version identifier</pdfaProperty:description>
                                  <pdfaProperty:name>part</pdfaProperty:name>
                                  <pdfaProperty:valueType>Integer</pdfaProperty:valueType>
                                </rdf:li>
                              </rdf:Seq>
                            </pdfaSchema:property>
                          </rdf:li>
                        </rdf:Bag>
                      </pdfaExtension:schemas>
                    </rdf:Description>

                """);
        }

        builder.Append("""    <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">""").Append('\n');
        if (!string.IsNullOrEmpty(metadata.Title))
            builder.Append(CultureInfo.InvariantCulture, $"      <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{Escape(metadata.Title)}</rdf:li></rdf:Alt></dc:title>\n");
        if (!string.IsNullOrEmpty(metadata.Author))
            builder.Append(CultureInfo.InvariantCulture, $"      <dc:creator><rdf:Seq><rdf:li>{Escape(metadata.Author)}</rdf:li></rdf:Seq></dc:creator>\n");
        if (!string.IsNullOrEmpty(metadata.Subject))
            builder.Append(CultureInfo.InvariantCulture, $"      <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{Escape(metadata.Subject)}</rdf:li></rdf:Alt></dc:description>\n");
        builder.Append("    </rdf:Description>\n");

        builder.Append("""    <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/">""").Append('\n');
        if (!string.IsNullOrEmpty(metadata.Creator))
            builder.Append(CultureInfo.InvariantCulture, $"      <xmp:CreatorTool>{Escape(metadata.Creator)}</xmp:CreatorTool>\n");
        builder.Append(CultureInfo.InvariantCulture, $"      <xmp:CreateDate>{timestamp}</xmp:CreateDate>\n");
        builder.Append(CultureInfo.InvariantCulture, $"      <xmp:ModifyDate>{timestamp}</xmp:ModifyDate>\n");
        builder.Append("    </rdf:Description>\n");

        builder.Append("""    <rdf:Description rdf:about="" xmlns:pdf="http://ns.adobe.com/pdf/1.3/">""").Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"      <pdf:Producer>{Escape(metadata.Producer)}</pdf:Producer>\n");
        if (!string.IsNullOrEmpty(metadata.Keywords))
            builder.Append(CultureInfo.InvariantCulture, $"      <pdf:Keywords>{Escape(metadata.Keywords)}</pdf:Keywords>\n");
        builder.Append("    </rdf:Description>\n");

        builder.Append("  </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
