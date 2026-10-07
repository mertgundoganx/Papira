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

                page.Header().PaddingBottom(10).Text("The Papira Society — membership application").FontSize(9).FontColor(Colors.Grey.Darken2);
                page.Footer().AlignCenter().Text("Fill the form in, save it, and send it back to us.").FontSize(8).FontColor(Colors.Grey.Darken1);

                page.Content().Column(column =>
                {
                    column.Spacing(14);

                    column.Item().Text("Membership application").FontSize(20).Bold().Heading(1);
                    column.Item().Text(
                        "Fill these fields in on screen, or print the form and write in them by hand.")
                        .FontColor(Colors.Grey.Darken3);

                    column.Item().Element(c => Field(c, "Ad ve soyad", field =>
                        field.TextField("name").Tooltip("Your full name").Required()));

                    column.Item().Element(c => Field(c, "E-posta adresi", field =>
                        field.TextField("email").Tooltip("An address we can reach you at").Required()));

                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Element(c => Field(c, "City", field =>
                            field.Dropdown("city", "Amsterdam", "Berlin", "Dublin", "Lisbon", "Vienna")
                                .Value("Dublin").Tooltip("Where you live")));

                        row.ConstantItem(16);

                        row.RelativeItem().Element(c => Field(c, "Membership number", field =>
                            field.TextField("member").Value("PAP-1024").ReadOnly()
                                .BackgroundColor(Colors.Grey.Lighten3).Tooltip("The number we gave you")));
                    });

                    column.Item().Element(c => Field(c, "A few words about you", field =>
                        field.TextField("bio").Multiline().MaxLength(400)
                            .Tooltip("Tell us a little about yourself").Height(70)));

                    column.Item().Text("Preferences").FontSize(14).SemiBold().Heading(2);
                    column.Item().Element(c => Option(c, "newsletter", "Send me the monthly newsletter", ticked: true));
                    column.Item().Element(c => Option(c, "events", "Tell me about events"));
                    column.Item().Element(c => Option(c, "terms", "I have read and accept the terms of membership"));
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = "Membership application",
            Author = "The Papira Society",
            Subject = "A fillable form",
            Language = "en-GB",
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
