using Papira;

namespace Papira.Samples;

public static class FeaturesDocument
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private const string Lorem =
        "Papira, .NET için sıfırdan yazılmış bir PDF motorudur. Yazı tipleri kendi TrueType okuyucumuzla işlenir, " +
        "yalnızca kullanılan glifler belgeye gömülür ve metin kopyalanabilir/aranabilir kalır. Satır kırma, hizalama, " +
        "sayfalar arası bölünme ve tablo başlıklarının tekrarı motorun kendisi tarafından yapılır. ";

    public static Document Create()
    {
        var circle = Image.FromFile(Asset("circle-rgba.png"));
        var gradient = Image.FromFile(Asset("gradient-rgb.png"));
        var checker = Image.FromFile(Asset("checker-palette.png"));
        var photo = Image.FromFile(Asset("photo.jpg"));

        return Document.Create(document =>
        {
            document.DefaultTextStyle(s => s.FontSize(11));

            document.Page(page =>
            {
                page.Margin(Unit.Centimetre(2));
                page.Header().PaddingBottom(10).BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingBottom(6)
                    .Text("Papira — Özellik Turu").FontSize(16).SemiBold();

                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });

                page.Content().Column(column =>
                {
                    column.Spacing(14);

                    column.Item().Text("1. Yazı stilleri").FontSize(14).Bold();
                    column.Item().Text(t =>
                    {
                        t.Span("Normal, ");
                        t.Span("kalın, ").Bold();
                        t.Span("italik, ").Italic();
                        t.Span("kalın italik, ").Bold().Italic();
                        t.Span("altı çizili, ").Underline();
                        t.Span("üstü çizili, ").Strikethrough();
                        t.Span("renkli, ").FontColor(Colors.Red);
                        t.Span("büyük ").FontSize(18);
                        t.Span("ve küçük. ").FontSize(8);
                        t.Span("Türkçe: ĞÜŞİÖÇ ğüşıöç — “tırnak” ‘işaretleri’ … €₺");
                    });
                    column.Item().Text("Sistem fontu (Arial, yüklüyse; değilse Lato'ya düşer): Hızlı kahverengi tilki.").FontFamily("Arial");

                    column.Item().Text("2. Hizalama").FontSize(14).Bold();
                    column.Item().Text(Lorem).AlignLeft();
                    column.Item().Text(Lorem).AlignCenter();
                    column.Item().Text(Lorem).AlignRight();
                    column.Item().Text(Lorem + Lorem).Justify();

                    column.Item().Text("3. Görseller").FontSize(14).Bold();
                    column.Item().Row(row =>
                    {
                        row.Spacing(10);
                        row.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Image(circle);
                        row.RelativeItem().Image(gradient);
                        row.RelativeItem().Image(photo);
                        row.ConstantItem(60).Image(checker);
                    });
                    column.Item().Text("Şeffaf PNG (yumuşak kenarlı daire), opak PNG, JPEG ve 4-bit paletli şeffaf PNG.").FontSize(9).Italic();

                    column.Item().EnsureSpace(100).Text("4. Kutular ve kenarlıklar").FontSize(14).Bold();
                    column.Item().Row(row =>
                    {
                        row.Spacing(10);
                        foreach (var color in new[] { Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple })
                        {
                            row.RelativeItem().Height(60).Background(color).Border(3).BorderColor(Colors.Grey.Darken3)
                                .AlignCenter().AlignMiddle().Text(color.ToString()).FontColor(Colors.White).Bold();
                        }
                    });

                    column.Item().PageBreak();

                    column.Item().Text("5. Sayfalara bölünen uzun metin").FontSize(14).Bold();
                    column.Item().Text(string.Concat(Enumerable.Repeat(Lorem, 40))).Justify().LineHeight(1.5f);

                    column.Item().EnsureSpace(120).Text("6. Sayfalara bölünen tablo (başlık her sayfada tekrarlanır)").FontSize(14).Bold();
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(50);
                            c.RelativeColumn();
                            c.RelativeColumn();
                        });

                        table.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Darken3).Padding(5).Text("No").FontColor(Colors.White).Bold();
                            h.Cell().ColumnSpan(2).Background(Colors.Grey.Darken3).Padding(5).Text("Açıklama (iki sütun kaplar)").FontColor(Colors.White).Bold();
                        });

                        for (var i = 1; i <= 80; i++)
                        {
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(i);
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text($"Satır {i} — sol hücre");
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text($"Satır {i} — sağ hücre");
                        }
                    });
                });
            });

            // A second section with a different page setup.
            document.Page(page =>
            {
                page.Size(PageSizes.A5.Landscape());
                page.Margin(30);
                page.PageColor(Colors.Grey.Lighten5);
                page.Background().AlignCenter().AlignMiddle().Text("TASLAK").FontSize(72).Bold().FontColor(Colors.Grey.Lighten2);
                page.Content().AlignCenter().AlignMiddle().Text(t =>
                {
                    t.AlignCenter();
                    t.Line("Yatay A5 bölümü").FontSize(24).Bold();
                    t.Span("Farklı sayfa boyutu, arka plan rengi ve filigran katmanı.");
                });
            });
        }).WithMetadata(new DocumentMetadata { Title = "Papira Özellik Turu", Author = "Papira" });
    }
}
