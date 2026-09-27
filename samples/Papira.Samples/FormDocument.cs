using Papira;

namespace Papira.Samples;

/// <summary>
/// A form a reader fills in on screen: text fields, a dropdown and checkboxes. Papira draws every
/// field, so the form also prints as it stands. It is tagged as well, which makes it a form a screen
/// reader can be filled in with.
/// </summary>
public static class FormDocument
{
    public static Document Create()
    {
        return Document.Create(document =>
        {
            document.DefaultTextStyle(style => style.FontSize(11));

            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(Unit.Centimetre(2));

                page.Header().PaddingBottom(10).Text("Papira Derneği — Üyelik Başvurusu").FontSize(9).FontColor(Colors.Grey.Darken2);
                page.Footer().AlignCenter().Text("Formu doldurup kaydedin, ardından bize iletin.").FontSize(8).FontColor(Colors.Grey.Darken1);

                page.Content().Column(column =>
                {
                    column.Spacing(14);

                    column.Item().Text("Üyelik Başvuru Formu").FontSize(20).Bold().Heading(1);
                    column.Item().Text(
                        "Aşağıdaki alanları ekranınızda doldurabilir ya da formu yazdırıp elle doldurabilirsiniz.")
                        .FontColor(Colors.Grey.Darken3);

                    column.Item().Element(c => Field(c, "Ad ve soyad", field =>
                        field.TextField("name").Tooltip("Ad ve soyadınız").Required()));

                    column.Item().Element(c => Field(c, "E-posta adresi", field =>
                        field.TextField("email").Tooltip("Size ulaşabileceğimiz e-posta adresi").Required()));

                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Element(c => Field(c, "Şehir", field =>
                            field.Dropdown("city", "İstanbul", "Ankara", "İzmir", "Bursa", "Antalya")
                                .Value("İstanbul").Tooltip("Yaşadığınız şehir")));

                        row.ConstantItem(16);

                        row.RelativeItem().Element(c => Field(c, "Üyelik numarası", field =>
                            field.TextField("member").Value("PAP-1024").ReadOnly()
                                .BackgroundColor(Colors.Grey.Lighten3).Tooltip("Tarafımızdan verilen üyelik numarası")));
                    });

                    column.Item().Element(c => Field(c, "Kısa özgeçmiş", field =>
                        field.TextField("bio").Multiline().MaxLength(400)
                            .Tooltip("Kendinizden kısaca söz edin").Height(70)));

                    column.Item().Text("Tercihler").FontSize(14).SemiBold().Heading(2);
                    column.Item().Element(c => Option(c, "newsletter", "Aylık bültene abone olmak istiyorum", ticked: true));
                    column.Item().Element(c => Option(c, "events", "Etkinlik duyuruları gönderilsin"));
                    column.Item().Element(c => Option(c, "terms", "Üyelik koşullarını okudum ve kabul ediyorum"));
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = "Üyelik Başvuru Formu",
            Author = "Papira Derneği",
            Subject = "Doldurulabilir form örneği",
            Language = "tr-TR",
        })
        .WithSettings(new DocumentSettings { Tagged = true });
    }

    /// <summary>A label above the field it belongs to.</summary>
    private static void Field(IContainer container, string label, Action<IContainer> field) =>
        container.Column(column =>
        {
            column.Spacing(3);
            column.Item().Text(label).FontSize(9).FontColor(Colors.Grey.Darken2);
            column.Item().Element(field);
        });

    /// <summary>A checkbox with its text beside it.</summary>
    private static void Option(IContainer container, string name, string label, bool ticked = false) =>
        container.Row(row =>
        {
            row.AutoItem().PaddingTop(1).Checkbox(name).Checked(ticked).Tooltip(label);
            row.ConstantItem(8);
            row.RelativeItem().Text(label);
        });
}
