using System.Globalization;
using Papira;

namespace Papira.Samples;

public sealed record InvoiceItem(string Name, int Quantity, decimal UnitPrice);

public sealed record InvoiceData(
    string Number,
    DateOnly IssueDate,
    DateOnly DueDate,
    string SellerName,
    string SellerAddress,
    string CustomerName,
    string CustomerAddress,
    IReadOnlyList<InvoiceItem> Items)
{
    public static InvoiceData Sample(int itemCount)
    {
        string[] products =
        [
            "Wireless keyboard", "Ergonomic mouse", "27\" IPS monitor", "USB-C dock",
            "Noise-cancelling headset", "Laptop stand", "1080p webcam", "External SSD, 1 TB",
            "65 W charger", "HDMI cable, 2 m", "Software licence — yearly", "Installation and training",
        ];

        var items = Enumerable.Range(0, itemCount)
            .Select(i => new InvoiceItem(products[i % products.Length], 1 + i % 5, 49.90m + i * 17.35m))
            .ToList();

        return new InvoiceData(
            "PAP-2026-000123",
            new DateOnly(2026, 9, 22),
            new DateOnly(2026, 10, 22),
            "Papira Software Ltd.",
            "185 Riverside Avenue\nFloor 4, Harbour District\nVAT 1234567890",
            "Northwind Trading Co.",
            "42 Market Street, Suite 3\nOld Town\nVAT 9876543210",
            items);
    }
}

public static class InvoiceDocument
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");
    private static readonly Color Accent = Color.FromHex("#1E3A8A");

    public static Document Create(InvoiceData data) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken4));

            page.Header().Element(c => ComposeHeader(c, data));
            page.Content().PaddingVertical(20).Element(c => ComposeContent(c, data));
            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Darken1));
                text.Span("Page ");
                text.CurrentPageNumber().SemiBold();
                text.Span(" / ");
                text.TotalPages();
            });
        }))
        .WithMetadata(new DocumentMetadata { Title = $"Invoice {data.Number}", Author = data.SellerName });

    private static void ComposeHeader(IContainer container, InvoiceData data)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("INVOICE").FontSize(26).Bold().FontColor(Accent);
                column.Item().Text(text =>
                {
                    text.Span("Invoice no. ").SemiBold();
                    text.Span(data.Number);
                });
                column.Item().Text(text =>
                {
                    text.Span("Issued ").SemiBold();
                    text.Span(data.IssueDate.ToString("d MMMM yyyy", Culture));
                });
                column.Item().Text(text =>
                {
                    text.Span("Due ").SemiBold();
                    text.Span(data.DueDate.ToString("d MMMM yyyy", Culture));
                });
            });

            row.ConstantItem(140).Height(60).Background(Accent).AlignCenter().AlignMiddle()
                .Text("PAPIRA").FontSize(22).Bold().FontColor(Colors.White).LetterSpacing(2);
        });
    }

    private static void ComposeContent(IContainer container, InvoiceData data)
    {
        container.Column(column =>
        {
            column.Spacing(16);

            column.Item().Row(row =>
            {
                row.Spacing(20);
                row.RelativeItem().Element(c => ComposeAddress(c, "From", data.SellerName, data.SellerAddress));
                row.RelativeItem().Element(c => ComposeAddress(c, "To", data.CustomerName, data.CustomerAddress));
            });

            column.Item().Element(c => ComposeTable(c, data));

            var subtotal = data.Items.Sum(i => i.Quantity * i.UnitPrice);
            var vat = subtotal * 0.20m;

            column.Item().AlignRight().Width(220).Column(totals =>
            {
                totals.Spacing(4);
                TotalRow(totals.Item(), "Subtotal", subtotal, false);
                TotalRow(totals.Item(), "VAT (20%)", vat, false);
                totals.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                TotalRow(totals.Item(), "Total", subtotal + vat, true);
            });

            column.Item().Background(Colors.Grey.Lighten4).Padding(12).Text(text =>
            {
                text.Span("Notes").Bold().FontSize(11);
                text.EmptyLine();
                text.Span("Please quote the invoice number when you pay, to account GB00 0000 0000 0000 0000 00. " +
                          "This document was produced by Papira, with no browser and nothing else installed. " +
                          "Text of several lines is broken at its words and may be justified, as this paragraph is.")
                    .FontColor(Colors.Grey.Darken2);
                text.Justify();
            });
        });
    }

    private static void ComposeAddress(IContainer container, string title, string name, string address)
    {
        container.Column(column =>
        {
            column.Spacing(3);
            column.Item().BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingBottom(4)
                .Text(title).SemiBold().FontColor(Accent);
            column.Item().Text(name).Bold();
            column.Item().Text(address).LineHeight(1.35f);
        });
    }

    private static void ComposeTable(IContainer container, InvoiceData data)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(28);
                columns.RelativeColumn(4);
                columns.RelativeColumn(1);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
            });

            table.Header(header =>
            {
                static IContainer HeaderCell(IContainer c) =>
                    c.Background(Accent).PaddingVertical(6).PaddingHorizontal(5).DefaultTextStyle(s => s.FontColor(Colors.White).SemiBold());

                HeaderCell(header.Cell()).Text("#");
                HeaderCell(header.Cell()).Text("Product or service");
                HeaderCell(header.Cell()).AlignRight().Text("Qty");
                HeaderCell(header.Cell()).AlignRight().Text("Unit price");
                HeaderCell(header.Cell()).AlignRight().Text("Amount");
            });

            for (var i = 0; i < data.Items.Count; i++)
            {
                var item = data.Items[i];
                var background = i % 2 == 0 ? Colors.White : Colors.Grey.Lighten5;

                IContainer Cell() => table.Cell().Background(background).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                    .PaddingVertical(5).PaddingHorizontal(5);

                Cell().Text(i + 1);
                Cell().Text(item.Name);
                Cell().AlignRight().Text(item.Quantity);
                Cell().AlignRight().Text(item.UnitPrice.ToString("C", Culture));
                Cell().AlignRight().Text((item.Quantity * item.UnitPrice).ToString("C", Culture));
            }
        });
    }

    private static void TotalRow(IContainer container, string label, decimal value, bool emphasize)
    {
        container.Row(row =>
        {
            var labelText = row.RelativeItem().Text(label);
            var valueText = row.AutoItem().Text(value.ToString("C", Culture));
            if (emphasize)
            {
                labelText.Bold().FontSize(12);
                valueText.Bold().FontSize(12).FontColor(Accent);
            }
        });
    }
}
