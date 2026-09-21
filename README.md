# Papira

[![CI](https://github.com/mertgundoganx/Papira/actions/workflows/ci.yml/badge.svg)](https://github.com/mertgundoganx/Papira/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Papira.svg)](https://www.nuget.org/packages/Papira)
[![Downloads](https://img.shields.io/nuget/dt/Papira.svg)](https://www.nuget.org/packages/Papira)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/mertgundoganx/Papira/blob/main/LICENSE)

**Fast, free, dependency-free PDF generation for .NET.**

Papira builds PDF documents from C# code with a fluent layout API. It has no browser, no native libraries and no third-party packages: the PDF writer, TrueType parser, font subsetter, PNG/JPEG handling and the layout engine are all part of the library.

- **Fast.** A 2-page invoice takes about 0.6 ms on one thread. On an 8-core Apple M2, Papira produces about 5,000 invoices per second.
- **Uses every core.** Compression, font subsetting and image encoding run in parallel, and documents can be generated concurrently from many threads.
- **Typographic text.** Pair kerning from the font's GPOS or kern table, as in browsers and word processors. Fonts are embedded as subsets with a ToUnicode map, so text stays selectable and searchable. Characters such as ğ, ş, ı, İ, ₺ and € work out of the box.
- **Same output everywhere.** The bundled Lato font is the default, so documents look the same on Windows, macOS and minimal Linux containers with no fonts installed.
- **Free for any use.** MIT licensed, including commercial use.

Supports .NET 8 and .NET 10. Trimming and Native AOT compatible.

## Installation

```bash
dotnet add package Papira
```

## Quick start

```csharp
using Papira;

Document.Create(document => document.Page(page =>
{
    page.Size(PageSizes.A4);
    page.Margin(40);
    page.DefaultTextStyle(s => s.FontSize(10));

    page.Header().Text("Invoice #123").FontSize(20).Bold();

    page.Content().PaddingVertical(20).Column(column =>
    {
        column.Spacing(10);
        column.Item().Text("Thank you for your order!");

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3);
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Grey.Lighten3).Padding(5).Text("Product").Bold();
                header.Cell().Background(Colors.Grey.Lighten3).Padding(5).AlignRight().Text("Price").Bold();
            });

            foreach (var item in items)
            {
                table.Cell().BorderBottom(0.5f).Padding(5).Text(item.Name);
                table.Cell().BorderBottom(0.5f).Padding(5).AlignRight().Text(item.Price.ToString("C"));
            }
        });
    });

    page.Footer().AlignCenter().Text(text =>
    {
        text.Span("Page ");
        text.CurrentPageNumber();
        text.Span(" of ");
        text.TotalPages();
    });
}))
.GeneratePdf("invoice.pdf");
```

`GeneratePdf()` returns a `byte[]`. `GeneratePdf(Stream)` writes to any stream, such as an ASP.NET Core response body.

Content flows across pages automatically. Table headers repeat on every page, and text, columns and tables split where the page ends.

## Layout building blocks

| Category | API |
|---|---|
| Structure | `Column`, `Row` (`RelativeItem`, `ConstantItem`, `AutoItem`), `Table` (`ColumnsDefinition`, `Header`, `Cell().ColumnSpan(n)`) |
| Text | `Text("...")`, `Text(t => { t.Span(...); t.CurrentPageNumber(); t.TotalPages(); })`, `AlignLeft/Center/Right`, `Justify`, `LineHeight`, `LetterSpacing`, `Underline`, `Strikethrough`, font weights, italic |
| Spacing and size | `Padding*`, `Width`, `Height`, `MinWidth`/`MaxWidth`, `MinHeight`/`MaxHeight`, `Extend*` |
| Decoration | `Background`, `Border*` with `BorderColor`, `LineHorizontal`, `LineVertical` |
| Positioning | `AlignLeft/Center/Right`, `AlignTop/Middle/Bottom` |
| Paging | `PageBreak`, `ShowEntire` (never split), `EnsureSpace(minHeight)` (keep a heading with the content that follows) |
| Page | `Size`, `Margin*`, `PageColor`, `Header`/`Content`/`Footer`, `Background`/`Foreground` full-page layers (for example watermarks), several `Page(...)` sections with different setups |
| Navigation | `Hyperlink(url)`, `Section(name)` with `SectionLink(name)`, `Bookmark(title, level)` for the outline panel |
| Images | `Image(...)` with `FitWidth` (default), `FitHeight`, `FitArea`. JPEG and PNG (all color types and bit depths, transparency, interlacing) |
| Reuse | `Element(c => ...)`, `Component(IComponent)`, `DefaultTextStyle(...)` on the document, a page or any container |

All sizes are in points (1/72 inch). Use `Unit.Millimetre(...)`, `Unit.Centimetre(...)` or `Unit.Inch(...)` to convert.

### Links and bookmarks

```csharp
container.Hyperlink("https://example.com").Text("Visit our website").Underline();

// Jump inside the document, also to later pages.
container.SectionLink("totals").Text("See totals");
container.Section("totals").Text("Totals").Bold();

// Entries in the bookmarks panel of PDF viewers; level 1 nests under the previous level 0 entry.
column.Item().Bookmark("Invoice").Text("Invoice").FontSize(20);
column.Item().Bookmark("Line items", level: 1).Table(...);
```

Links must be absolute URIs with a scheme (`https:`, `mailto:`, `tel:` …); `javascript:` and `data:` links are rejected.

### Reusable components

```csharp
public sealed class AddressBlock(string title, string address) : IComponent
{
    public void Compose(IContainer container) => container.Column(column =>
    {
        column.Item().Text(title).SemiBold();
        column.Item().Text(address);
    });
}

row.RelativeItem().Component(new AddressBlock("Seller", sellerAddress));
```

## Fonts

```csharp
FontManager.RegisterFont("fonts/Inter-Regular.ttf");        // .ttf or .ttc, all faces
FontManager.RegisterFontsFromDirectory("fonts");
FontManager.RegisterFontWithCustomName("Brand", stream);

container.Text("Hello").FontFamily("Inter").SemiBold();
```

Font families are looked up in this order: fonts you registered, then fonts installed on the machine, then the default Lato. System fonts are indexed once, the first time they're needed. If a family has no bold or italic face, Papira simulates it.

### Fallback fonts

Characters that a font doesn't contain (for example Chinese, Arabic or symbols in a customer name) are taken from fallback fonts, character by character:

```csharp
// Per style: tried in order for characters "Inter" lacks.
container.Text(customerName).FontFamily("Inter", "Noto Sans SC", "Noto Sans Arabic");

// For all documents.
FontManager.FallbackFontFamilies = ["Noto Sans SC", "Noto Sans Symbols"];
```

After the style's fallbacks and the global list, every other registered font is tried, so registering a font is often enough. Characters found in no font are drawn as the font's `.notdef` box.

TrueType-outline fonts (`.ttf`, `.ttc`) are supported. CFF-based `.otf` fonts are not supported yet.

## Images

```csharp
var logo = Image.FromFile("logo.png");   // create once, reuse everywhere

container.Width(120).Image(logo);
container.Height(80).Image(logo).FitArea();
```

The PDF encoding of an image is computed once and cached on the `Image` instance, so a logo used in thousands of documents is only processed once. Opaque PNGs and JPEGs are embedded without being decoded.

## Performance tips

- Reuse `Image` instances. Don't load the same file for every document.
- When you generate many documents in parallel yourself, set `new DocumentSettings { MaxDegreeOfParallelism = 1 }` so each document doesn't also parallelize internally.
- `PdfCompression.Fastest` trades slightly larger files for more throughput. `PdfCompression.None` skips compression entirely.
- `TotalPages()` makes Papira lay the document out twice. Documents that don't use it are laid out once.

```csharp
var pdf = Document.Create(Compose)
    .WithSettings(new DocumentSettings { Compression = PdfCompression.Fastest })
    .WithMetadata(new DocumentMetadata { Title = "Invoice #123", Author = "ACME" })
    .GeneratePdf();
```

## Samples

[`samples/Papira.Samples`](https://github.com/mertgundoganx/Papira/tree/main/samples/Papira.Samples) contains a multi-page invoice, a document that shows every feature, and a throughput benchmark:

```bash
dotnet run -c Release --project samples/Papira.Samples -- invoice
dotnet run -c Release --project samples/Papira.Samples -- features
dotnet run -c Release --project samples/Papira.Samples -- bench 5000
```

## Limitations

Papira is young. These features are not implemented yet:

- Ligatures and complex shaping. Scripts that need it (Arabic, Indic scripts) are not supported yet, and right-to-left text is not reordered.
- Color emoji. Emoji fonts that store bitmaps or color layers (Apple Color Emoji, Noto Color Emoji) are not supported; monochrome outline fonts such as Noto Emoji work.
- Forms, encryption and PDF/A.
- CFF-based OpenType fonts (`.otf`).

Contributions are welcome. See the [issues](https://github.com/mertgundoganx/Papira/issues).

## Contributing

See [CONTRIBUTING.md](https://github.com/mertgundoganx/Papira/blob/main/CONTRIBUTING.md) for setup, project layout and guidelines. Please follow the [Code of Conduct](https://github.com/mertgundoganx/Papira/blob/main/CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](https://github.com/mertgundoganx/Papira/blob/main/SECURITY.md).

## License

Papira is licensed under the [MIT License](https://github.com/mertgundoganx/Papira/blob/main/LICENSE).

The bundled Lato font is licensed under the SIL Open Font License 1.1. See [THIRD-PARTY-NOTICES.md](https://github.com/mertgundoganx/Papira/blob/main/THIRD-PARTY-NOTICES.md).
