# Papira

[![CI](https://github.com/mertgundoganx/Papira/actions/workflows/ci.yml/badge.svg)](https://github.com/mertgundoganx/Papira/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Papira.svg)](https://www.nuget.org/packages/Papira)
[![Downloads](https://img.shields.io/nuget/dt/Papira.svg)](https://www.nuget.org/packages/Papira)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/mertgundoganx/Papira/blob/main/LICENSE)

**Fast, free, dependency-free PDF generation for .NET.**

Papira builds PDF documents from C# code with a fluent layout API. It has no browser, no native libraries and no third-party packages: the PDF writer, TrueType parser, font subsetter, PNG/JPEG handling and the layout engine are all part of the library.

- **Fast.** A 2-page invoice takes about 0.7 ms on one thread. On an 8-core Apple M2, Papira produces about 5,000 invoices per second.
- **Uses every core.** Compression, font subsetting and image encoding run in parallel, and documents can be generated concurrently from many threads.
- **Typographic text.** Pair kerning from the font's GPOS or kern table, as in browsers and word processors. Fonts are embedded as subsets with a ToUnicode map, so text stays selectable and searchable. Characters such as ğ, ş, ı, İ, ₺ and € work out of the box.
- **Every writing direction.** Arabic and Hebrew are laid out right to left with the Unicode bidirectional algorithm, and cursive letters take their initial, medial and final shapes from the font itself.
- **The scripts of India.** Devanagari, Bengali, Tamil and their relatives are shaped syllable by syllable: conjuncts, rephs and vowel signs that move in front of their consonant.
- **Color emoji.** Emoji are drawn in color from any of the four ways fonts store them, and flags, skin tones and joined sequences come out as one picture.
- **Same output everywhere.** The bundled Lato font is the default, so documents look the same on Windows, macOS and minimal Linux containers with no fonts installed.
- **Accessible.** One setting tags the document with its structure — headings, tables, lists, figures — and the output passes PDF/UA-1.
- **Fillable forms.** Text fields, checkboxes, radio buttons and dropdowns, drawn by Papira so they look the same in every viewer and print as they stand.
- **Signatures.** Sign a document with a certificate as it is written; the signature is built in-process and covers the whole file.
- **HTML templates.** The markup templates are written in — flexible boxes, grids, elements placed against their container, tables, pictures, style sheets — laid out without a browser. Compared with what Chrome prints from the same markup, the two agree to within a point.
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
| Structure | `Column`, `Row` (`RelativeItem`, `ConstantItem`, `AutoItem`), `Table` (`ColumnsDefinition`, `Header`, `Cell().ColumnSpan(n)`, `Cell().RowSpan(n)`), `List`, `NumberedList` |
| Text | `Text("...")`, `Text(t => { t.Span(...); t.CurrentPageNumber(); t.TotalPages(); })`, `AlignLeft/Center/Right`, `Justify`, `LineHeight`, `LetterSpacing`, `Underline`, `Strikethrough`, font weights, italic, `Direction`/`RightToLeft` |
| Spacing and size | `Padding*`, `Width`, `Height`, `MinWidth`/`MaxWidth`, `MinHeight`/`MaxHeight`, `Extend*` |
| Decoration | `Background`, `Border*` with `BorderColor`, `LineHorizontal`, `LineVertical` |
| Positioning | `AlignLeft/Center/Right`, `AlignTop/Middle/Bottom` |
| Paging | `PageBreak`, `ShowEntire` (never split), `EnsureSpace(minHeight)` (keep a heading with the content that follows) |
| Page | `Size`, `Margin*`, `PageColor`, `Header`/`Content`/`Footer`, `Background`/`Foreground` full-page layers (for example watermarks), several `Page(...)` sections with different setups |
| Graphics | `CornerRadius`, `Opacity`, `Rotate`/`RotateLeft`/`RotateRight`, `Scale`, `BackgroundLinearGradient`, `BackgroundRadialGradient`, `Canvas` for custom vector drawing |
| Codes | `QrCode(text)`, `Barcode(text)` (Code 128) |
| Navigation | `Hyperlink(url)`, `Section(name)` with `SectionLink(name)`, `Bookmark(title, level)` for the outline panel |
| Images | `Image(...)` with `FitWidth` (default), `FitHeight`, `FitArea`. JPEG, PNG (all color types and bit depths, transparency, interlacing) and WebP (lossy and lossless, with transparency) |
| Vector drawings | `Svg(...)` for SVG logos, icons and charts, with the same `FitWidth`/`FitHeight`/`FitArea` options |
| Reuse | `Element(c => ...)`, `Component(IComponent)`, `DefaultTextStyle(...)` on the document, a page or any container |

All sizes are in points (1/72 inch). Use `Unit.Millimetre(...)`, `Unit.Centimetre(...)` or `Unit.Inch(...)` to convert.

### Lists and merged table cells

```csharp
container.NumberedList(list =>
{
    list.Item().Text("First step");
    list.Item().Column(item =>
    {
        item.Item().Text("Second step, with details:");
        item.Item().List(inner => inner.Item().Text("A nested bullet"));
    });
});

table.Cell().RowSpan(2).Text("Spans two rows");
table.Cell().ColumnSpan(2).Text("Spans two columns");
```

Rows connected by a row span are kept together: if they don't fit on the page, they move to the next one as a group.

### Graphics

```csharp
container.CornerRadius(8).Background(Colors.Blue);                       // rounded, and the content is clipped
container.Opacity(0.4f).Background(Colors.Green);
container.Shadow(0, 2, 6, Colors.Black, opacity: 0.2f)                   // moved, softened, and behind
    .CornerRadius(8).Background(Colors.White);
container.BackgroundLinearGradient(90, Colors.Blue, Colors.Purple);      // 0° left to right, 90° top to bottom
container.RotateLeft().Text("Vertical heading");                         // swaps width and height
container.Rotate(-20).Text("DRAFT");                                     // keeps the layout size

container.Height(80).Canvas((canvas, width, height) =>
{
    canvas.RoundedRectangle(0, 0, width, height / 2, 4);
    canvas.Fill(Colors.Blue);
    canvas.Circle(width / 2, height / 2, 10);
    canvas.FillAndStroke(Colors.Yellow, Colors.Orange, 2);
    canvas.Line(0, height, width, height, Colors.Grey.Darken1);
});
```

### QR codes and barcodes

```csharp
container.Width(90).QrCode("https://example.com/invoice/123");
container.Width(90).QrCode(payload, QrErrorCorrection.High);   // survives more damage
container.Height(40).Barcode("PAP-2026-000123");               // Code 128
```

QR codes use the smallest version that fits and the best mask, and include the quiet zone needed for scanning. The encoder is verified by decoding its output with an independent reader.

### Fillable forms

```csharp
container.TextField("name").Tooltip("Your full name").Required();
container.TextField("notes").Multiline().MaxLength(400);
container.Dropdown("city", "İstanbul", "Ankara", "İzmir").Value("İstanbul");
container.Checkbox("terms").Tooltip("I accept the terms");

// One of a group: the buttons share a name and differ in what they stand for.
container.Radio("payment", "card").Checked();
container.Radio("payment", "transfer");

// A place to sign, by hand or on screen.
container.SignatureField("approval").Tooltip("Approved by");
```

The reader fills these in on screen and saves the file, or prints it and fills it in by hand. Papira
draws every field itself — the frame, the value, the tick — so a form looks and prints the same in
every viewer. Files that leave this to the viewer (`/NeedAppearances`) come out blank in some of them,
and are not allowed in PDF/A at all.

Every field has a name of its own, which is what identifies it in the filled-in form, and a `Tooltip`
saying what it is for; a tagged document needs that tooltip, so Papira falls back to the name. Fields
also take `ReadOnly()`, `Height(...)`, `BackgroundColor(...)` and `BorderColor(...)`. A field belongs
in the content of a page: one placed in a header or footer would repeat on every page, which a form
cannot express, and Papira says so rather than writing a broken file.

### Accessible documents

```csharp
Document.Create(...)
    .WithMetadata(new DocumentMetadata { Title = "Annual report", Language = "en" })
    .WithSettings(new DocumentSettings { Tagged = true })
    .GeneratePdf("report.pdf");
```

A tagged document carries its structure beside its pages, and that structure is what a screen reader
follows. Papira builds it from the document itself:

| What you write | What the reader is told |
| --- | --- |
| `Text("...")` | a paragraph |
| `Text("...").Heading(1)` … `Heading(6)` | a heading of that level, which readers navigate by |
| `Table(...)` with a `Header` | a table, its rows, and header cells that say which column they head |
| `List(...)` / `NumberedList(...)` | a list, its items, and each marker beside its text |
| `Image(...).Alt("...")` / `Svg(...).Alt("...")` | a figure with the words that replace it |
| `Hyperlink(...)` | a link, with the address it opens |
| `TextField(...)` / `Checkbox(...)` / `Dropdown(...)` | a form field, with what it is for and what it holds |
| `Html(...)` | the structure the markup itself carries: headings, lists, tables, links |
| Header, footer, watermarks, borders, backgrounds | nothing: they are marked as decoration and skipped |

Set `Language` to the language of the text, give every picture an `Alt`, and use `Heading` rather than
just a larger font: those three are what separate a file that passes from one that helps.

CI validates the accessible sample and the form sample against PDF/UA-1 with veraPDF, and PDF/A-3b on
the same file.

## HTML templates

```csharp
container.Html(html);                                   // markup you have in hand
container.HtmlFile("templates/invoice.html");           // a file, with its folder for pictures

container.Html(html, options => options
    .StyleSheet(File.ReadAllText("invoice.css"))        // a style sheet beside the markup
    .Resource("logo", companyLogo)                      // <img src="logo"> draws this image
    .BaseDirectory("templates")                         // where <img src="..."> is looked for
    .AllowRemoteImages(TimeSpan.FromSeconds(10))        // and pictures from http(s), see below
    .Zoom(1.7f));                                       // everything 1.7 times the size
```

A template engine of your choice fills the markup in; Papira lays it out. The markup becomes ordinary
Papira elements, so it paginates, and takes part in a tagged document, like anything else — a report
written as HTML comes out as an accessible PDF, with its headings, lists and tables intact.

This is a document formatter, not a browser: there is no JavaScript, and nothing is fetched but the
pictures you allow. What it does cover is what document templates are written in.

| Supported | Not supported |
| --- | --- |
| Headings, paragraphs, `div`, `section`, `blockquote`, `pre`, `hr`, `br` | `float`, multi-column, `JavaScript` |
| `display: flex` with the direction, `flex-wrap`, `justify-content`, `align-items`, `align-self`, `gap` and `flex`/`flex-grow`/`flex-shrink`/`flex-basis` | `align-content` across several wrapped lines, `order` |
| `display: grid` with `grid-template-columns` (`fr`, lengths, percentages, `repeat()`, `minmax()`) and the gaps | Named areas, `grid-template-rows`, items that span tracks |
| `position: relative` and `absolute`, offsets from any edge as a length or a share of the box, negative ones included | `position: sticky`; `fixed` is placed like `absolute` |
| `display: inline-block`, which keeps its size, padding and background and sits on the line of the text | `vertical-align` other than the baseline |
| `b`, `strong`, `i`, `em`, `u`, `s`, `small`, `code`, `span`, `mark` and the rest of the inline elements | Sub- and superscript raised off the baseline |
| `ul`, `ol` (with `type` and `start`), nested lists, `dl` | Counters other than the page number |
| `table` with `thead`, `colspan`, `rowspan`, column widths, `cellpadding`, `border`, `display: table-header-group`; columns as wide as what they hold | `border-collapse` as a visual rule, `vertical-align`, captions |
| `img` from a file, a data URI, a registered resource or an address, drawn at its own size; `<svg>` written in the markup and SVG files, drawn as vectors | Pictures referred to from CSS (`background-image`) |
| `a` to an address or to an `id` in the document, inside a paragraph or around a block | |
| Colours (including `rgba`/`hsla`), backgrounds, borders, `border-radius`, `box-shadow`, margins (they collapse, as CSS says), padding, `box-sizing` | Gradients, `filter` |
| `width`, `height`, `min-`/`max-` of both, in lengths, percentages, `vh`/`vw` and `calc()` | Percentages of a height that is itself not stated |
| Fonts, sizes in `px`/`pt`/`em`/`rem`/`%`, weight, style, decoration, letter spacing, line height, `text-transform`, `font-variant-numeric`, `font-feature-settings` | `@font-face`: fonts are the ones Papira knows about |
| `text-align`, `white-space: pre`, `display: none`, `visibility`, `object-fit`, `transform: scale()`, `page-break-before`/`-after`, `page-break-inside: avoid` | `@media`, `@page`, transforms other than scaling |
| Selectors by tag, class, id, `*`, descendant and child; `:first-child`, `:last-child`, `:nth-child`, `:only-child`, `:not()`; `::before` and `::after` with `content`; the `style` attribute; specificity | Attribute selectors, sibling combinators |

Sizes without a unit are CSS pixels, three quarters of a point each. `1em` starts from 12 points unless
`options.FontSize(...)` says otherwise; text that states no size of its own keeps the style of the
document around it. A rule whose selector Papira does not understand is skipped rather than guessed at.

### The page number, and pictures from the network

A footer written as markup can say which page it is on. The names are the ones a browser's own print
templates use, so a footer written for one reads the same here:

```csharp
page.Footer().Html("<div>Sayfa <span class='pageNumber'></span> / <span class='totalPages'></span></div>");
```

`AllowRemoteImages` lets the pictures of a document be fetched over http and https. They are all fetched
at once, before the document is laid out, so a page of pictures costs one round of waiting; a picture
that does not arrive is left out and the document is still written. Addresses on the machine itself and
on its own network are refused unless `allowPrivateNetworks` says otherwise, so that markup from
elsewhere cannot read what only the machine can reach. Turn it on for markup you trust.

### How close it is to a browser

Every layout above was compared with what Chrome prints from the same markup, word by word. On a report
of a kind templates are written as — a header laid out with flexible boxes, a seven-column grid, a
picture with seventy-two points marked on it, inline boxes, and a table of fifty rows — the words stand
a tenth of a point apart across the page and a third of a point down it. The widest a word stood from
where Chrome put it was a point and a quarter across, and three points down.

Lines are as tall as a browser makes them, down to the last fraction: text laid out from markup is
measured the way a browser measures it, with what the font says about its letters rounded to whole screen
pixels first. A page of fifty table rows breaks where Chrome breaks it. (Text laid out through the fluent
API is measured as the font states it, which is what a page without a browser in mind should do.)

Two differences are worth knowing about:

- **A character no font of the document can draw is left out.** Browsers reach for any font on the
  machine; Papira draws with the fonts it was given, so that a document comes out the same on a
  developer's machine and in a container with no fonts installed. Name a font that has the character —
  see [Fallback fonts](#fallback-fonts) — if your templates use symbols such as ✓ or →.
- **Shadows come out a little stronger than Chrome prints them.** Papira draws the shadow the style
  sheet asks for; Chrome's own print output washes it out. On screen, the two agree.

## Archiving, attachments and encryption

```csharp
Document.Create(Compose)
    .WithAttachment(new DocumentAttachment("invoice.xml", xmlBytes)
    {
        MediaType = "text/xml",
        Relationship = AttachmentRelationship.Data,
    })
    .WithSettings(new DocumentSettings { Standard = PdfStandard.PdfA3b })
    .GeneratePdf("invoice.pdf");
```

`PdfStandard.PdfA2b` and `PdfA3b` produce archivable files: Papira embeds an sRGB color profile it generates itself and XMP metadata that matches the document information. CI validates the output with veraPDF. Attachments, such as an e-invoice XML, require PDF/A-3b.

```csharp
.WithSettings(new DocumentSettings
{
    Encryption = new PdfEncryptionSettings
    {
        UserPassword = "secret",          // leave empty to let anyone open the file
        OwnerPassword = "owner",          // lifts all restrictions
        Permissions = PdfPermissions.Print | PdfPermissions.PrintHighResolution,
    },
})
```

Encryption uses AES-256 (PDF 2.0, revision 6) and covers every stream and string, including the metadata. Encrypted files cannot be PDF/A.

### Signing

```csharp
using var certificate = X509CertificateLoader.LoadPkcs12FromFile("company.pfx", password);

Document.Create(Compose)
    .WithSettings(new DocumentSettings
    {
        Signature = new PdfSignatureSettings
        {
            Certificate = certificate,
            FieldName = "approval",          // leave it out to sign invisibly
            Reason = "I approve this invoice",
            Location = "İstanbul",
        },
    })
    .GeneratePdf("invoice.pdf");
```

The signature covers every byte of the file apart from itself, so a reader can tell that nothing has
changed since it was signed, and who signed it. Papira builds the signature itself — a detached CMS
message as RFC 5652 describes it, over a SHA-256 digest, with an RSA or an elliptic curve key — so
signing needs nothing beyond the certificate and .NET's own cryptography. OpenSSL verifies the result,
and reports it as broken as soon as a single bit of the document is changed.

A signed document can be PDF/A; it cannot be encrypted. Timestamps from a time-stamping authority are
not fetched: that would mean talking to a server, which Papira never does.

### Links and bookmarks

```csharp
container.Hyperlink("https://example.com").Text("Visit our website").Underline();

// A link on a few words in the middle of a paragraph.
container.Text(text =>
{
    text.Span("Read the ");
    text.Span("documentation").Hyperlink("https://example.com/docs").FontColor(Colors.Blue).Underline();
    text.Span(" before you start.");
});

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

## Right-to-left and cursive text

Arabic, Hebrew and their neighbours need no setting: the direction of a paragraph is taken from its first
strongly directional character, exactly as a browser does it.

```csharp
container.Text("الفاتورة رقم 12345 بتاريخ 2026");     // right to left, with the number left to right
container.Text("Papira ile عربي metin");              // mixed, each part in its own direction
container.Text(t => t.Span("(1) abc").RightToLeft()); // forced, whatever the text starts with
```

What happens behind that:

- **Direction.** The Unicode bidirectional algorithm (UAX #9) resolves an embedding level for every
  character and reorders each line for display, so numbers, punctuation and Latin words inside Arabic text
  end up where they belong. Brackets and quotation marks are mirrored. Papira passes both conformance test
  files of the Unicode Character Database — 861,948 cases — with no failures.
- **Shape.** Cursive letters take their initial, medial, final or isolated form, and the ligatures a script
  requires (lam-alef and the like) are formed, using the font's own `GSUB` rules — the positional features,
  compositions, required and contextual ligatures. The result was compared glyph by glyph and position by
  position with HarfBuzz, the shaper browsers use: 1,198 of 1,200 generated lines across three Arabic fonts
  came out identical.
- **Marks.** Accents, vowel signs and the harakat of Arabic are placed by the font's own `GPOS` rules —
  on the letter, on another mark, at the join of two cursive letters. A font that says nothing about
  them has its marks centred over the letter by Papira instead.
- **Extraction.** A glyph that replaced several characters is mapped back to all of them, so the text can
  still be selected and searched.
- **Alignment.** A right-to-left paragraph is aligned to the right unless you say otherwise.

Any font with the right tables works. The bundled Lato covers Latin, Greek and Cyrillic; for Arabic or
Hebrew, register a font that has those characters:

```csharp
FontManager.RegisterFont("NotoSansArabic-Regular.ttf");
container.Text("مرحبا بالعالم").FontFamily("Noto Sans Arabic");
```

Papira reads OpenType layout tables (`GSUB`, `GPOS`, `GDEF`). Fonts that shape only through Apple's `morx`
table — some macOS system fonts, such as Geeza Pro — are drawn unshaped.

## Scripts of India

Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and Malayalam are written in
syllables, and what is written is not the order the glyphs are drawn in:

```csharp
FontManager.RegisterFont("NotoSansDevanagari-Regular.ttf");
container.Text("नमस्ते दुनिया").FontFamily("Noto Sans Devanagari");
```

Papira splits the text into syllables, finds the consonant each one is built around, and puts everything
else where it belongs before asking the font to draw it:

- a vowel sign written after its consonant is drawn before it (नि is written न ि);
- an initial Ra climbs onto the syllable as a reph, and lands where the script puts it;
- consonants joined by a virama become conjuncts (क + ् + ष becomes क्ष), half forms and subjoined forms;
- a vowel sign written as two characters is taken apart and drawn in two places;
- a zero-width non-joiner keeps letters that would otherwise join apart.

Because the glyphs end up in a different order than the characters, each reordered syllable also says what
it stands for (`/ActualText`), so a reader gets the text back as it was written.

The rules are those of the OpenType Indic script development specification, with the character properties
taken from the Unicode Character Database. The output was compared with HarfBuzz glyph by glyph: every word
of a page of real text in all nine scripts came out identical, as did 17,890 of 17,903 lines of generated
syllables — conjuncts, rephs and reordered vowel signs included.

## Color emoji

Emoji are drawn in color from the font, whichever way it stores them:

| How the font stores them | Fonts that do |
| --- | --- |
| Layers of outlines (`COLR`/`CPAL`) | Segoe UI Emoji, on Windows |
| Drawings (the `SVG ` table) | Noto Color Emoji, the current build |
| Pictures (`sbix`) | Apple Color Emoji, on macOS |
| Pictures (`CBDT`/`CBLC`) | Noto Color Emoji, the Android build |

```csharp
FontManager.RegisterFont("NotoColorEmoji.ttf");
container.Text("Fatura onaylandı 🎉").FontFamily("Noto Color Emoji");
container.Text("Fatura onaylandı 🎉");   // or leave it to the fallback fonts
```

A flag, a skin tone or a family is several characters that the font joins into one picture, and Papira asks
it to do exactly that, through the same rules it uses for cursive scripts. The character itself is written
invisibly underneath, so the text can still be selected and searched. Fonts that compose only through
Apple's `morx` table — Apple Color Emoji among them — draw each emoji but leave such sequences apart.

## Fonts

```csharp
FontManager.RegisterFont("fonts/Inter-Regular.ttf");        // .ttf, .otf or .ttc, all faces
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

Both kinds of outline are supported: TrueType glyphs (`.ttf`, `.ttc`) and Compact Font Format charstrings
(`.otf`, and `.ttc` collections that hold them). Either way the font is embedded as a subset holding only
the glyphs the document uses, with the subroutines the outlines actually call.

## Images

```csharp
var logo = Image.FromFile("logo.png");   // create once, reuse everywhere

container.Width(120).Image(logo);
container.Height(80).Image(logo).FitArea();
```

The PDF encoding of an image is computed once and cached on the `Image` instance, so a logo used in thousands of documents is only processed once. Opaque PNGs and JPEGs are embedded without being decoded.

| Format | How it reaches the PDF |
| --- | --- |
| JPEG | Embedded untouched; PDF reads the same encoding |
| PNG | Opaque images are embedded untouched, transparent ones are decoded and their alpha becomes a soft mask |
| WebP | Decoded — PDF has no WebP — and written as compressed pixels, transparency included |

The WebP decoder is Papira's own, for both the lossless and the lossy variant, and was checked against
libwebp on 229 generated pictures of every size and quality: each one came out pixel for pixel identical.
Animated WebP files are refused, since a page shows one picture.

## SVG drawings

An SVG is translated into PDF vector operators, so it stays sharp at any size and adds no pixels to the file.

```csharp
var logo = SvgImage.FromFile("logo.svg");   // parse once, draw as often as you like

container.Width(150).Svg(logo);
container.Height(60).Svg(logo).FitHeight();
container.Svg(SvgImage.FromString("<svg viewBox='0 0 10 10'>…</svg>"));
```

Papira draws the part of SVG that logos, icons and charts are made of:

| Supported | Skipped |
| --- | --- |
| `path` (including arcs), `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon` | `text` — convert it to outlines before exporting |
| `g`, `a`, `switch`, `use`, `symbol`, `defs`, nested `svg` with its own viewport | `image` — draw bitmaps with `Image(...)` |
| `transform`: `matrix`, `translate`, `scale`, `rotate`, `skewX`, `skewY` | `filter`, `mask`, `pattern` |
| `clip-path`, `mask` (in user space or the box of the element), `viewBox` with `preserveAspectRatio` | `animate`, scripting |
| `text` and `tspan`: `x`, `y`, `dx`, `dy` (one value or one per character), `text-anchor`, the font properties, `letter-spacing` | Text on a path, `textLength` |
| `pattern`, in user space or the box of the shape, with `patternTransform` and a `viewBox` | Patterns that paint a stroke |
| Linear and radial gradients, in both user space and object bounding box units, with `stop-opacity` | `spreadMethod="reflect"`/`"repeat"` (treated as `pad`) |
| Fill and stroke colors in every CSS notation, `currentColor`, `fill-rule`, `stroke-width`, `stroke-dasharray`, `stroke-linecap`, `stroke-linejoin`, `opacity`, `fill-opacity`, `stroke-opacity`, `display`, `visibility` | |
| Presentation attributes, a `style` attribute and a `<style>` element with tag, class, id, descendant and child selectors | Attribute selectors, pseudo-classes, media queries |

`SvgImage` is immutable, so one instance can be shared between documents and threads. A malformed or hostile file
raises `InvalidDataException` rather than consuming memory: element count, nesting, path length and reference
depth are all bounded, and a `DOCTYPE` is skipped instead of resolved.

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

[`samples/Papira.Samples`](https://github.com/mertgundoganx/Papira/tree/main/samples/Papira.Samples) contains a multi-page invoice, a document that shows every feature, an archivable and an accessible document, a fillable form, a report laid out from an HTML template, and a throughput benchmark:

```bash
dotnet run -c Release --project samples/Papira.Samples -- invoice
dotnet run -c Release --project samples/Papira.Samples -- features
dotnet run -c Release --project samples/Papira.Samples -- archive
dotnet run -c Release --project samples/Papira.Samples -- accessible
dotnet run -c Release --project samples/Papira.Samples -- form
dotnet run -c Release --project samples/Papira.Samples -- html
dotnet run -c Release --project samples/Papira.Samples -- bench 5000
```

## Limitations

These features are not implemented yet:

- Khmer, Myanmar, Tibetan and the scripts the Universal Shaping Engine covers (Javanese, Balinese and their
  relatives). The nine scripts of India are shaped; these have rules of their own that Papira does not have yet.
- Apple's own layout tables (`morx`, `kerx`). Fonts that carry OpenType tables as well are shaped from those;
  fonts that carry only Apple's are drawn unshaped.
- SVG filters, which need the drawing to be turned into pixels first. Text, masks and patterns are drawn;
  see the table above.
- A browser: floats, JavaScript and the parts of CSS a page does not need are out of scope. Flexible boxes, grids and elements placed against their container are laid out; see the HTML table above.

Contributions are welcome. See the [issues](https://github.com/mertgundoganx/Papira/issues).

## Contributing

See [CONTRIBUTING.md](https://github.com/mertgundoganx/Papira/blob/main/CONTRIBUTING.md) for setup, project layout and guidelines. Please follow the [Code of Conduct](https://github.com/mertgundoganx/Papira/blob/main/CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](https://github.com/mertgundoganx/Papira/blob/main/SECURITY.md).

## License

Papira is licensed under the [MIT License](https://github.com/mertgundoganx/Papira/blob/main/LICENSE).

The bundled Lato font is licensed under the SIL Open Font License 1.1. See [THIRD-PARTY-NOTICES.md](https://github.com/mertgundoganx/Papira/blob/main/THIRD-PARTY-NOTICES.md).
