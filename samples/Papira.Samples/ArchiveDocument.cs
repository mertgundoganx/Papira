using System.Globalization;
using System.Text;
using Papira;

namespace Papira.Samples;

/// <summary>
/// An invoice in PDF/A-3b: the archive standard with the machine-readable invoice XML embedded,
/// as e-invoice archives and standards such as Factur-X require.
/// </summary>
public static class ArchiveDocument
{
    public static Document Create()
    {
        var data = InvoiceData.Sample(itemCount: 6);
        var xml = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Invoice>
              <ID>{data.Number}</ID>
              <IssueDate>{data.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}</IssueDate>
              <Supplier>{data.SellerName}</Supplier>
              <Customer>{data.CustomerName}</Customer>
              <PayableAmount currencyID="TRY">{data.Items.Sum(i => i.Quantity * i.UnitPrice) * 1.20m:F2}</PayableAmount>
            </Invoice>
            """);

        return InvoiceDocument.Create(data)
            .WithMetadata(new DocumentMetadata
            {
                Title = $"Fatura {data.Number}",
                Author = data.SellerName,
                Subject = "e-Arşiv fatura",
                Creator = "Papira Samples",
            })
            .WithAttachment(new DocumentAttachment("fatura.xml", xml)
            {
                MediaType = "text/xml",
                Relationship = AttachmentRelationship.Data,
                Description = "Makine tarafından okunabilir fatura",
            })
            .WithSettings(new DocumentSettings { Standard = PdfStandard.PdfA3b });
    }
}
