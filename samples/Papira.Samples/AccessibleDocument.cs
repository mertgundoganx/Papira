using Papira;

namespace Papira.Samples;

/// <summary>
/// A report that carries its own structure, for readers used by people who are blind or partially
/// sighted. The file conforms to PDF/UA-1, which CI checks with veraPDF.
/// </summary>
public static class AccessibleDocument
{
    public static Document Create()
    {
        var data = InvoiceData.Sample(itemCount: 8);

        return Document.Create(document =>
        {
            document.DefaultTextStyle(style => style.FontSize(11));

            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(Unit.Centimetre(2));

                // Headers and footers are furniture: a reader for the blind skips them.
                page.Header().PaddingBottom(8).Text("Papira — erişilebilir rapor").FontSize(9).FontColor(Colors.Grey.Darken2);
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Sayfa ").FontSize(9);
                    text.CurrentPageNumber().FontSize(9);
                    text.Span(" / ").FontSize(9);
                    text.TotalPages().FontSize(9);
                });

                page.Content().Column(column =>
                {
                    column.Spacing(12);

                    column.Item().Text("Yıllık Faaliyet Raporu").FontSize(22).Bold().Heading(1);
                    column.Item().Text(
                        "Bu belge etiketlidir: başlıklar, paragraflar, listeler, tablolar ve görseller yapısal olarak " +
                        "işaretlenmiştir. Ekran okuyucular içeriği sayfadaki yerleşimine göre değil, bu yapıya göre okur.");

                    column.Item().Text("1. Özet").FontSize(15).SemiBold().Heading(2);
                    column.Item().Text(
                        "Yıl boyunca üretilen belge sayısı arttı ve ortalama üretim süresi düştü. Aşağıdaki tabloda " +
                        "kalemler ve tutarlar yer alıyor.");

                    column.Item().Text("2. Öne çıkanlar").FontSize(15).SemiBold().Heading(2);
                    column.Item().List(list =>
                    {
                        list.Item().Text("Belgeler artık ekran okuyucularla tam uyumlu.");
                        list.Item().Text("Tablo başlıkları sütunlarıyla birlikte duyuruluyor.");
                        list.Item().Text("Her görselin metin karşılığı var.");
                    });

                    column.Item().Text("3. Kalemler").FontSize(15).SemiBold().Heading(2);
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(4);
                            columns.ConstantColumn(60);
                            columns.ConstantColumn(90);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Background(Colors.Grey.Lighten3).Padding(5).Text("Açıklama").SemiBold();
                            header.Cell().Background(Colors.Grey.Lighten3).Padding(5).AlignRight().Text("Adet").SemiBold();
                            header.Cell().Background(Colors.Grey.Lighten3).Padding(5).AlignRight().Text("Tutar").SemiBold();
                        });

                        foreach (var item in data.Items)
                        {
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(item.Name);
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).AlignRight().Text(item.Quantity.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")));
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).AlignRight().Text((item.Quantity * item.UnitPrice).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) + " ₺");
                        }
                    });

                    column.Item().Text("4. Görsel").FontSize(15).SemiBold().Heading(2);
                    column.Item().Width(220).Image(Asset("photo.jpg"))
                        .Alt("Bir masanın üzerinde duran dizüstü bilgisayar ve not defteri");

                    column.Item().Hyperlink("https://github.com/mertgundoganx/Papira")
                        .Text("Papira'nın kaynak kodu").FontColor(Colors.Blue).Underline();
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = "Yıllık Faaliyet Raporu",
            Author = "Papira",
            Subject = "Erişilebilir belge örneği",
            Language = "tr-TR",
        })
        .WithSettings(new DocumentSettings { Tagged = true });
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}
