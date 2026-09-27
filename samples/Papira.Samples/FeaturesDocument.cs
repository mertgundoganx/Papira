using Papira;

namespace Papira.Samples;

public static class FeaturesDocument
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static readonly string[] ArabicLines =
    [
        "مرحبا بالعالم",
        "الفاتورة رقم 12345 بتاريخ 2026",
        "كتاب (القراءة) مهم",
    ];

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
        var webp = Image.FromFile(Asset("circle-alpha.webp"));
        var logo = SvgImage.FromFile(Asset("logo.svg"));
        FontManager.RegisterFont(Asset("NotoSansArabic-subset.ttf"));

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

                    column.Item().Bookmark("1. Yazı stilleri").Text("1. Yazı stilleri").FontSize(14).Bold();
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
                    column.Item().Hyperlink("https://github.com/mertgundoganx/Papira").Text("Papira GitHub sayfası").FontColor(Colors.Blue).Underline();

                    column.Item().Bookmark("2. Sağdan sola yazım").Text("2. Sağdan sola yazım ve bitişik harfler").FontSize(14).Bold();
                    column.Item().Text("Yön, metnin ilk güçlü karakterinden anlaşılır; rakamlar ve Latin sözcükler kendi yönünde kalır.")
                        .FontSize(9).Italic();
                    foreach (var line in ArabicLines)
                        column.Item().Text(line).FontFamily("Noto Sans Arabic").FontSize(14);

                    column.Item().Text(text =>
                    {
                        text.Span("Aynı satırda iki yön: ");
                        text.Span("عربي").FontFamily("Noto Sans Arabic");
                        text.Span(" ve Türkçe birlikte.");
                    });

                    column.Item().PaddingTop(8).Bookmark("3. Hizalama").Text("3. Hizalama").FontSize(14).Bold();
                    column.Item().Text(Lorem).AlignLeft();
                    column.Item().Text(Lorem).AlignCenter();
                    column.Item().Text(Lorem).AlignRight();
                    column.Item().Text(Lorem + Lorem).Justify();

                    column.Item().Bookmark("4. Görseller ve vektör çizimler").Text("4. Görseller ve vektör çizimler").FontSize(14).Bold();
                    column.Item().Row(row =>
                    {
                        row.Spacing(10);
                        row.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Image(circle);
                        row.RelativeItem().Image(gradient);
                        row.RelativeItem().Image(photo);
                        row.ConstantItem(60).Image(checker);
                        row.ConstantItem(60).Image(webp);
                    });
                    column.Item().Text("Şeffaf PNG (yumuşak kenarlı daire), opak PNG, JPEG, 4-bit paletli şeffaf PNG ve WebP.").FontSize(9).Italic();

                    column.Item().PaddingTop(8).Row(row =>
                    {
                        row.Spacing(14);
                        row.ConstantItem(150).Svg(logo);
                        row.RelativeItem().AlignMiddle().Text(
                                "Aynı çizim SVG olarak: yollar, degradeler, kırpma, saydamlık ve dönüşümler doğrudan " +
                                "PDF vektör komutlarına çevrilir. Büyütünce bozulmaz, dosyada piksel yer kaplamaz.")
                            .FontSize(9).Italic();
                    });

                    column.Item().EnsureSpace(100).Bookmark("5. Kutular ve kenarlıklar").Text("5. Kutular ve kenarlıklar").FontSize(14).Bold();
                    column.Item().Row(row =>
                    {
                        row.Spacing(10);
                        foreach (var color in new[] { Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple })
                        {
                            row.RelativeItem().Height(60).Background(color).Border(3).BorderColor(Colors.Grey.Darken3)
                                .AlignCenter().AlignMiddle().Text(color.ToString()).FontColor(Colors.White).Bold();
                        }
                    });

                    column.Item().EnsureSpace(200).Bookmark("6. Listeler ve birleşik hücreler").Text("6. Listeler ve birleşik hücreler").FontSize(14).Bold();
                    column.Item().Row(row =>
                    {
                        row.Spacing(20);
                        row.RelativeItem().List(list =>
                        {
                            list.Item().Text("Madde işaretli liste");
                            list.Item().Column(item =>
                            {
                                item.Item().Text("İç içe liste:");
                                item.Item().NumberedList(inner =>
                                {
                                    inner.Item().Text("birinci");
                                    inner.Item().Text("ikinci");
                                });
                            });
                            list.Item().Text("Uzun öğeler, işaretin sağında hizalı olarak alt satıra kayar.");
                        });

                        row.RelativeItem().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                            });

                            IContainer Cell(IContainer c) => c.Border(0.5f).BorderColor(Colors.Grey.Darken1).Padding(4);

                            Cell(table.Cell().RowSpan(2).Background(Colors.Grey.Lighten3)).AlignMiddle().Text("2 satır");
                            Cell(table.Cell()).Text("B");
                            Cell(table.Cell()).Text("C");
                            Cell(table.Cell().ColumnSpan(2).Background(Colors.Grey.Lighten4)).Text("2 sütun");
                            Cell(table.Cell()).Text("D");
                            Cell(table.Cell()).Text("E");
                            Cell(table.Cell()).Text("F");
                        });
                    });

                    column.Item().EnsureSpace(230).Bookmark("7. Grafikler, QR kod ve barkod").Text("7. Grafikler, QR kod ve barkod").FontSize(14).Bold();
                    column.Item().Row(row =>
                    {
                        row.Spacing(12);
                        row.ConstantItem(90).Height(90).QrCode("https://github.com/mertgundoganx/Papira");
                        row.RelativeItem().Column(inner =>
                        {
                            inner.Spacing(8);
                            inner.Item().Height(34).Barcode("PAP-2026-000123");
                            inner.Item().Height(44).CornerRadius(8).BackgroundLinearGradient(90, Color.FromHex("#1E3A8A"), Colors.Purple)
                                .AlignCenter().AlignMiddle().Text("Yuvarlak köşe ve gradyan").FontColor(Colors.White);
                        });
                        row.ConstantItem(90).Height(90).Canvas((canvas, w, h) =>
                        {
                            float[] values = [0.45f, 0.8f, 0.6f, 1f];
                            var bar = w / (values.Length * 1.5f);
                            for (var i = 0; i < values.Length; i++)
                            {
                                var height = (h - 6) * values[i];
                                canvas.RoundedRectangle(i * bar * 1.5f, h - height, bar, height, 2);
                                canvas.Fill(Color.FromHex("#1E3A8A"));
                            }

                            canvas.Line(0, h - 0.5f, w, h - 0.5f, Colors.Grey.Darken1, 1);
                        });
                    });
                    column.Item().Row(row =>
                    {
                        row.Spacing(12);
                        row.ConstantItem(40).Height(60).Background(Colors.Grey.Lighten3).AlignCenter().AlignMiddle().RotateLeft().Text("Dikey").Bold();
                        row.ConstantItem(90).Height(60).AlignCenter().AlignMiddle().Rotate(-20).Text("TASLAK").FontSize(18).Bold().FontColor(Colors.Red);
                        row.RelativeItem().Height(60).Opacity(0.4f).BackgroundRadialGradient(Colors.Yellow, Colors.Orange)
                            .AlignCenter().AlignMiddle().Text("%40 saydam radyal gradyan");
                    });

                    column.Item().PageBreak();

                    column.Item().Bookmark("8. Sayfalara bölünen uzun metin").Text("8. Sayfalara bölünen uzun metin").FontSize(14).Bold();
                    column.Item().SectionLink("table").Text("Bu bölümü atlayıp tabloya git →").FontColor(Colors.Blue).Underline();
                    column.Item().Text(string.Concat(Enumerable.Repeat(Lorem, 40))).Justify().LineHeight(1.5f);

                    column.Item().EnsureSpace(120).Section("table").Bookmark("9. Sayfalara bölünen tablo").Text("9. Sayfalara bölünen tablo (başlık her sayfada tekrarlanır)").FontSize(14).Bold();
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

                    column.Item().EnsureSpace(150).Bookmark("10. Doldurulabilir form").Text("10. Doldurulabilir form").FontSize(14).Bold();
                    column.Item().Text("Aşağıdaki alanlar ekranda doldurulabilir; yazdırıldığında da olduğu gibi çıkar.");
                    column.Item().Text("Ad ve soyad").FontSize(9).FontColor(Colors.Grey.Darken2);
                    column.Item().TextField("ad").Tooltip("Ad ve soyadınız");
                    column.Item().Text("Şehir").FontSize(9).FontColor(Colors.Grey.Darken2);
                    column.Item().Width(220).Dropdown("sehir", "İstanbul", "Ankara", "İzmir").Value("İstanbul").Tooltip("Yaşadığınız şehir");
                    column.Item().Row(row =>
                    {
                        row.AutoItem().PaddingTop(1).Checkbox("kosullar").Tooltip("Koşulları kabul ediyorum");
                        row.ConstantItem(8);
                        row.RelativeItem().Text("Koşulları kabul ediyorum");
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
                page.Content().Bookmark("Yatay A5 bölümü").AlignCenter().AlignMiddle().Text(t =>
                {
                    t.AlignCenter();
                    t.Line("Yatay A5 bölümü").FontSize(24).Bold();
                    t.Span("Farklı sayfa boyutu, arka plan rengi ve filigran katmanı.");
                });
            });
        }).WithMetadata(new DocumentMetadata { Title = "Papira Özellik Turu", Author = "Papira" });
    }
}
