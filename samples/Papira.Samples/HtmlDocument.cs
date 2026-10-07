using Papira;

namespace Papira.Samples;

/// <summary>
/// A report written as HTML and laid out by Papira: headings, paragraphs, a styled table, lists, a
/// vector logo and links, from a template and a style sheet rather than from code.
/// </summary>
public static class HtmlDocument
{
    public static Document Create()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");

        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(Unit.Centimetre(2));
                page.Footer().AlignCenter().Text(text =>
                {
                    text.CurrentPageNumber().FontSize(9).FontColor(Colors.Grey.Darken1);
                    text.Span(" / ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    text.TotalPages().FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                page.Content().HtmlFile(Path.Combine(assets, "report.html"));
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = "Quarterly Report",
            Author = "Papira",
            Subject = "A document laid out from an HTML template",
            Language = "en-GB",
        })
        .WithSettings(new DocumentSettings { Tagged = true });
    }
}
