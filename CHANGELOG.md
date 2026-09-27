# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html):
from 1.0 on, the public API only changes in a way that breaks code in a major version.

## [1.1.0] - 2026-09-27

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

[1.1.0]: https://github.com/mertgundoganx/Papira/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/mertgundoganx/Papira/compare/v0.2.0...v1.0.0
[0.2.0]: https://github.com/mertgundoganx/Papira/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/mertgundoganx/Papira/releases/tag/v0.1.0
