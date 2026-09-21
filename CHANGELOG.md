# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Until 1.0, minor versions may contain breaking changes.

## [Unreleased]

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

[Unreleased]: https://github.com/mertgundoganx/Papira/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/mertgundoganx/Papira/releases/tag/v0.1.0
