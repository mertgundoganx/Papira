# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html):
from 1.0 on, the public API only changes in a way that breaks code in a major version.

## [1.4.0] - 2026-10-07

The style sheets a document links to, and the markup that used to stop a document being written. Five
real templates were laid out beside what Chrome prints from them, page by page.

### Added

- Style sheets the document links to (`<link rel="stylesheet">`): one beside the markup or given as a data URI is read as it stands, and one over the network once `HtmlOptions.AllowRemoteStyleSheets` says it may be.
- `@media`: the rules of a block are read where they apply to a printed page. What `print` and `all` ask for is given to the page and what `screen` asks for is not, as a browser does when it prints, and a block that asks how wide the page is is answered with how wide it is — `HtmlOptions.PageWidth` says how wide that is, and the zoom is taken into account by itself.
- A margin told to take whatever room is left (`margin: auto`) pushes an item of a flexible box across the line, which is how a single item is sent to one end of it.

### Fixed

- A picture that cannot be read is left out instead of stopping the document. A source that is empty, points at nothing, or holds something Papira cannot read is skipped, as a browser skips it — the address of a picture is often data, and data is often wrong.
- A box wider than the space it was given overflows it, as a browser lets it, instead of being moved to a page where it would not fit either. Markup as ordinary as a box with a width of 100% and a padding stopped the document being written.
- A box taller than the page carries on over the next one. A box of markup told how tall it is used to be moved whole to the next page, which left the rest of the page empty, and one taller than a page stopped the document altogether. A box built with the fluent API still moves as a whole, which is what someone who asks for a box of a given height means by it.
- An item of a flexible box is held inside the smallest and the largest size it says it may be, so an item that asks for the whole line and is given a largest width takes only that.
- An element placed against one side of its container is as wide as what it holds, which is the shrink-to-fit of CSS. A label held to a corner used to span the whole box.
- A single line of a flexible box told how thick it is fills that thickness, so its items are placed across the box rather than across the tallest of them.
- The rules inside a block Papira does not read — `@media screen`, `@supports`, `@keyframes` — no longer leak out of it and apply to everything.

### Changed

- Every public type now says what it is, so an editor has something to show for it.
- Trimming and Native AOT are checked on every build: the samples are published as a native binary, the first trimming warning fails the build, and the binary is run to make sure the documents still come out.
- The public API is compared with the last published version on every build, so a release cannot break the code of someone already using Papira without saying so.

## [1.3.0] - 2026-10-07

What was left over from the templates a browser prints: the shadows a box casts, and lines as tall as a
browser makes them.

### Added

- Shadows (`box-shadow`, and `IContainer.Shadow` for the fluent API): moved, softened and cast larger than the box, in as many layers as the markup states. A shadow with nothing to blur is drawn as a shape; a blurred one is laid down through a grey picture of its own softness, which is how a browser writes one into a PDF — the colour stays a colour and only the edge is a picture.
- Colours may say how much of them shows through: `rgba()`, `hsla()`, `#rgba` and `#rrggbbaa` are read wherever a colour is.

### Changed

- Text laid out from markup is measured the way a browser measures it: what the font says about its letters is rounded to whole screen pixels before a line is laid out. Line for line, Papira's lines are now exactly as tall as Chrome's, so a page of fifty table rows breaks where Chrome breaks it. Text laid out through the fluent API is measured as the font states it, as before.
- A picture used more than once in a template is read once and embedded once.

## [1.2.0] - 2026-10-07

The HTML templates a browser prints today. Everything here was compared with what Chrome prints from the
same markup, page by page and word by word.

### Added

- Flexible boxes (`display: flex`): the direction, wrapping, `justify-content` in all its forms, `align-items` and `align-self` — including lining items up on their text — `gap`, and `flex` with the sizes it grows and shrinks from. A column of items that only stacks is laid out as an ordinary column, so it goes on breaking over pages.
- Grids (`display: grid`), as far as a page needs them: `grid-template-columns` written in `fr`, in lengths, in percentages, with `repeat()` and `minmax()`, and the gaps between the tracks. The children fall into the columns a row at a time, and a grid taller than a page breaks between its rows.
- Elements placed against their container (`position: relative` and `absolute`): offsets from any edge, as a length or as a share of the box, negative ones that put the element outside it, and an element held to both edges of an axis spanning the distance between them. Such an element takes no room where it is written, as CSS says.
- Pictures fetched over the network (`HtmlOptions.AllowRemoteImages`). Every picture of a document is fetched at once rather than one after another, with a timeout and a size limit of their own, and a picture that does not arrive is left out instead of the document going unwritten. Addresses on the machine itself and on its own network are refused unless they are allowed, so that markup from elsewhere cannot read what only the machine can reach.
- Sizes that follow the space around them: percentages on any element, `min-width`, `max-width`, `min-height`, `max-height`, `box-sizing`, the sums `calc()` writes, and the viewport units `vh` and `vw`.
- Boxes that stand in a line of text (`display: inline-block`): they keep their own size, padding and background, sit on the line of the text around them, and wrap to the next line when the one they are on is full.
- The page number in the markup itself: an element of the class `pageNumber` or `totalPages` is drawn as the number of the page it ends up on, or as how many pages there are, so a footer written for a browser reads the same here.
- Magnifying a whole document (`HtmlOptions.Zoom`), as the scale of a print dialog does: the markup is laid out in a page that much narrower and drawn that much larger.
- Drawings written in the markup itself (`<svg>`), kept as they were written and drawn as vectors.
- Selectors that ask where an element stands — `:first-child`, `:last-child`, `:nth-child` (by number, `odd` and `even`), `:only-child`, `:not()` — and the text a rule may put before or after an element (`::before`, `::after` with `content`, including `attr()`).
- The features a font offers, by name or by their four-letter tags: `font-variant-numeric` (figures of equal width, oldstyle figures, a slashed zero), `font-variant`, `font-feature-settings`, and `TextStyle.FontFeatures` for the fluent API.
- `text-transform`, in the letters of the language the markup states: the capital of a Turkish `i` is `İ`, and of an English one `I`.
- `border-radius`, `object-fit` (`contain`, `cover`, `fill`), `transform: scale()`, and `display: table-header-group`, which repeats a group of rows at the top of every page.
- `IContainer.Decoration()` marks content as an artifact of the page, which a reader for the blind passes over. An `<img alt="">` is marked that way by itself, as the empty description asks.
- `ImageDescriptor.Stretch()` and `Cover()`, and `TableColumnsDescriptor.AutoColumn()` for a column as wide as what it holds.

### Changed

- A picture told nothing about its size is drawn at its own size, in the pixels it was made of, and shrunk to the page only where it is wider than the room it has. It used to be stretched to the width of whatever held it.
- A picture stands in the line of text around it instead of starting a block of its own, so a line may hold words and pictures together.
- The columns of a table that states no widths are as wide as what they hold, and such a table is as wide as its columns need. Tables that state a width, or widths for their columns, are laid out as before.
- A border takes room of its own in a document laid out from markup, as the box model says.
- The padding and the margins of a box that carries on over a page are drawn where the box begins and where it ends, and not again at the fold.
- The styles of `<html>` and `<body>` are applied to the document, and the body is given the margin a browser gives it. A template that sets `body { margin: 0 }` is unaffected; one that does not now stands where a browser puts it.

### Fixed

- A character no font of the document can draw is left out instead of being drawn as the glyph a font keeps for what it cannot draw. That glyph stands for no character at all, which left the reader a blank and made the file neither accessible nor archivable.
- Whether an element is a block is decided by everything that has a say in it, not only by its `style` attribute: `display` stated in a style sheet is now read.
- `margin: 0 auto` written as a shorthand centres the box, as the longhands already did, and a box is centred in the space it was given rather than inside its own width.

## [1.1.1] - 2026-09-27

### Added

- The scripts of India: Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and Malayalam. Text is split into syllables, the base consonant of each one is found and everything else is placed relative to it — conjuncts and half forms, an initial Ra that becomes a reph, vowel signs that are drawn before the consonant they are written after, and two-part vowel signs that are drawn in two places. A reordered syllable also says what it stands for (`/ActualText`), so the text still reads back as it was written. Compared with HarfBuzz glyph by glyph: every word of a page of real text in all nine scripts is identical, as are 17,890 of 17,903 lines of generated syllables.
- Mark attachment from the font's `GPOS` table: accents on their letters, marks on marks, the harakat of Arabic, and the joins of a cursive script (lookup types 1–9, with the context rules). Across 172 fonts of Latin text with accents and every Arabic and Hebrew font of a machine, Papira's positions agree with HarfBuzz's. A font that positions no marks itself has them centred over the letter by Papira instead.
- Accented text is drawn with the single glyph a font keeps for it where there is one, and with the letter and the mark where there is not — both ways round, as a browser does it.
- The ligatures a font offers of its own accord (fi, fl and the like), left out again when the letters are spaced apart.
- Signing (`DocumentSettings.Signature`): the document is signed with a certificate as it is written, with a detached CMS message Papira builds itself (RFC 5652, SHA-256, RSA or elliptic curve). OpenSSL verifies the result and reports it as broken as soon as a bit of the document changes. A signed document can be PDF/A.
- Radio buttons (`Radio`) and places to sign (`SignatureField`) in forms.
- SVG text (`text`, `tspan`, with the positions given one value or one per character, `text-anchor`, the font properties and letter spacing), masks (`mask`) and patterns (`pattern`, in user space or the box of the shape, with a transform and a view box).
- Style sheets understand descendant and child selectors, in SVG as well as in HTML.

### Fixed

- Text with a letter spacing no longer forms the ligatures that would undo it, which is what browsers do.
- A style sheet rule of one feature could be applied in place of another's: the plan of a script was cached by the script alone, so the same font shaped differently depending on what had been drawn before it.

## 1.1.0 - withdrawn

The package published under this version was built from the 1.0.0 commit by mistake. It is unlisted on NuGet and its tag was removed; 1.1.1 is the release these changes were meant to be.

## [1.0.0] - 2026-09-27

### Added

- Graphics: `CornerRadius` (rounded backgrounds, borders and clipped content), `Opacity`, `Rotate`, `RotateLeft`/`RotateRight`, `Scale`, and linear and radial gradient backgrounds.
- `Canvas` for custom vector drawing: paths, Bézier curves, rectangles, rounded rectangles, circles and lines, filled or stroked.
- QR codes (`QrCode`) with automatic version and mask selection and four error correction levels, and Code 128 barcodes (`Barcode`). The encoders were verified against an independent QR reader and barcode library.
- Attachments (`WithAttachment`), shown in the attachments panel and marked with their relationship, e.g. the machine-readable XML of an invoice.
- PDF/A-2b and PDF/A-3b output (`DocumentSettings.Standard`) with a generated sRGB output intent and XMP metadata. CI validates the output with veraPDF.
- Encryption with AES-256 (`DocumentSettings.Encryption`): user and owner passwords and permissions such as printing or copying.
- Right-to-left text: the Unicode bidirectional algorithm (UAX #9) resolves the direction of every character and reorders each line, mirroring brackets on the way. Both conformance files of the Unicode Character Database pass, 861,948 cases in all. `Direction`/`RightToLeft` override the direction a paragraph would take by itself.
- Arabic and the other cursive scripts: letters take their initial, medial, final and isolated forms and the ligatures the script requires, from the font's own `GSUB` rules (lookup types 1–7, with `GDEF` glyph classes and mark filtering). The output was compared glyph by glyph with HarfBuzz on two fonts and several thousand lines.
- A glyph that stands for several characters, such as a ligature, is mapped back to all of them in the `ToUnicode` map, so the text still extracts.
- Accessible documents (`DocumentSettings.Tagged`): the structure of the document is written beside its pages, with headings (`Heading`), paragraphs, lists, tables whose header cells say which column they head, figures with a description (`Alt`), links, and page furniture marked as decoration. `DocumentMetadata.Language` names the language. The output passes PDF/UA-1, on its own and together with PDF/A-3b; CI validates both with veraPDF.
- WebP images, both lossless (VP8L) and lossy (VP8), with transparency. PDF has no WebP, so the picture is decoded and embedded as pixels. The decoders were checked against libwebp on 229 generated pictures — every one identical, pixel for pixel.
- Colour emoji, from all four tables fonts use for them: layers of outlines (`COLR`/`CPAL`), drawings (`SVG `) and pictures (`sbix`, `CBDT`/`CBLC`). Flags, skin tones, keycaps and sequences joined by a zero width joiner are composed through the font's own rules, and the characters are written invisibly underneath so the text still extracts.
- Gradients can be transparent: the `stop-opacity` of an SVG gradient becomes a PDF soft mask, so a gradient that fades out no longer covers what is under it.
- OpenType fonts with Compact Font Format outlines (`.otf`, and collections that hold them). They are embedded as a CID-keyed subset, with unused glyphs and unreachable subroutines dropped and the remaining calls renumbered. Checked against 127 faces and 6,888 glyphs of a machine's font library: every outline came back identical to the original.
- HTML templates (`Html`, `HtmlFile`): headings, paragraphs, lists, tables with spans and column widths, pictures, preformatted text and links, styled by a `<style>` element, a `style` attribute or a style sheet passed beside the markup. Selectors by tag, class, id, descendant and child are matched with CSS specificity; margins collapse as CSS says. The markup becomes ordinary Papira elements, so it paginates and takes part in a tagged document. Block by block, the output agrees with what Chrome prints from the same markup to within a point.
- Links on part of a paragraph (`text.Span("...").Hyperlink(...)` and `.SectionLink(...)`), where a whole block used to be the smallest thing that could be clicked. In a tagged document each one becomes a `Link` element of its own, as PDF/UA asks.
- Fillable forms: text fields (`TextField`, single line or several, with a length limit), checkboxes (`Checkbox`) and dropdowns (`Dropdown`). Papira draws the appearance of every field itself, so a form looks and prints the same in every viewer — and stays valid as PDF/A, which does not allow leaving that to the reader. Fields carry what they are for (`Tooltip`), whether they have to be filled in (`Required`), and whether they can be (`ReadOnly`); in a tagged document each one also takes its place in the structure, and such a form passes PDF/UA-1.
- SVG drawings (`Svg`, `SvgImage`): paths with arcs, the basic shapes, groups, `use`/`symbol`, nested viewports, transforms, clip paths, linear and radial gradients, opacity and the stroke properties, styled by presentation attributes, a `style` attribute or a `<style>` element. The output was compared pixel by pixel with the SVG renderer of macOS.

### Changed

- The canvas keeps a full transform matrix instead of an offset, so text, images and shapes can be rotated and scaled.

### Fixed

- Line dashes, caps, joins, character spacing and the text render mode could leak out of a group: the canvas assumed a "Q" restored the PDF defaults instead of forgetting what it had cached.
- A tagged document with more than one annotation on a page wrote a number tree whose entries ran into one another, which made the file unreadable to strict parsers.
- A WebP picture was written with an empty name before its filter (`/Filter//FlateDecode`), which strict parsers complain about.
- A table cell of a tagged document that spanned several columns or rows did not say so, which left a reader — and PDF/UA-1 — with a table whose rows differed in width.

## [0.2.0] - 2026-09-22

### Added

- Bulleted and numbered lists (`List`, `NumberedList`) with custom markers, start numbers and nesting.
- Table cells spanning several rows (`RowSpan`). Rows connected by a row span are laid out and paginated as a group.
- Hyperlinks (`Hyperlink`), internal links to named sections (`Section`, `SectionLink`) and a document outline (`Bookmark`) shown in the bookmarks panel of PDF viewers. Links on content split across pages are clickable on every page.
- Per-character font fallback: `FontFamily(family, params fallbacks)`, the global `FontManager.FallbackFontFamilies` list, and automatic use of other registered fonts for characters the primary font lacks.
- Invisible formatting characters (zero-width joiners, variation selectors, byte order mark) are no longer drawn as `.notdef` boxes.
- Pair kerning from the OpenType GPOS `kern` feature (pair adjustment lookups, formats 1 and 2, including extension lookups) with a fallback to the legacy `kern` table. Kerned text is written with `TJ` position adjustments, so it stays selectable and searchable.

### Changed

- `ColumnSpan` returns `ITableCellContainer`, so it can be chained with `RowSpan`, and `FontFamily` accepts optional fallback families. Both changes are source compatible; code built against 0.1 must be recompiled.
- The sample benchmark warms up for two seconds before measuring, so the numbers reflect fully optimized code. The README now reports about 0.6 ms per invoice on one thread.

## [0.1.0] - 2026-09-22

First public release.

### Added

- PDF 1.7 writer with Flate compression and configurable compression level.
- Fluent layout API: `Column`, `Row`, `Table` (column spans, header repeated on every page), `Text`, `Image`, `LineHorizontal`/`LineVertical`, padding, borders, backgrounds, alignment, size constraints, `Extend`, `ShowEntire`, `EnsureSpace` and `PageBreak`.
- Automatic pagination with repeating page headers and footers, `Background`/`Foreground` page layers and multiple page sections.
- Rich text: spans with font family, size, weight, italic, color, underline, strikethrough, letter spacing and line height. Left, center, right and justified alignment. Current page number and total page count.
- TrueType (`.ttf`, `.ttc`) parser with font subsetting and ToUnicode maps, so text in generated files can be selected and searched.
- Bundled Lato font family as the default font, plus system font lookup and custom font registration.
- JPEG (pass-through) and PNG images: all color types and bit depths, transparency (soft masks) and interlacing.
- Multi-core output stage (content compression, font subsetting, image encoding). Documents can be generated concurrently.
- Document metadata (title, author, subject, keywords, creator, producer, creation date).
- Deterministic output: identical input (with a fixed `CreationDate`) produces byte-identical files.
- Hardened parsing of untrusted fonts and images: size limits, bounded decompression and validation of all offsets.

[1.4.0]: https://github.com/mertgundoganx/Papira/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/mertgundoganx/Papira/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/mertgundoganx/Papira/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/mertgundoganx/Papira/compare/v1.0.0...v1.1.1
[1.0.0]: https://github.com/mertgundoganx/Papira/compare/v0.2.0...v1.0.0
[0.2.0]: https://github.com/mertgundoganx/Papira/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/mertgundoganx/Papira/releases/tag/v0.1.0
