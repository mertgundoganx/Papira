# Contributing to Papira

Thank you for considering a contribution! Bug reports, documentation fixes and pull requests are all welcome.

## Reporting bugs and requesting features

- Search [existing issues](https://github.com/mertgundoganx/Papira/issues) first.
- For bugs, use the bug report template. The fastest way to get a bug fixed is a minimal `Document.Create(...)` snippet that reproduces it, plus the generated PDF or a screenshot when the problem is visual.
- Security issues must **not** be reported publicly; see [SECURITY.md](SECURITY.md).

## Development setup

Requirements: [.NET SDK 10](https://dotnet.microsoft.com/download) (the version is pinned in `global.json`). To run the tests on both target frameworks you also need the .NET 8 runtime.

```bash
git clone https://github.com/mertgundoganx/Papira.git
cd Papira
dotnet build
dotnet test
```

Generate the sample documents and run the throughput benchmark:

```bash
dotnet run -c Release --project samples/Papira.Samples -- invoice
dotnet run -c Release --project samples/Papira.Samples -- features
dotnet run -c Release --project samples/Papira.Samples -- bench 5000
```

The PDFs are written to `samples/Papira.Samples/bin/Release/net10.0/output/`.

## Project layout

| Path | Contents |
|---|---|
| `src/Papira/Pdf` | Low-level PDF writing: byte buffer, object/xref writer |
| `src/Papira/Fonts` | TrueType parser, subsetter, font registry |
| `src/Papira/Images` | JPEG and PNG handling |
| `src/Papira/Elements` | Layout elements (measure/draw/pagination) |
| `src/Papira/Fluent` | Public fluent API (extension methods and descriptors) |
| `src/Papira/Rendering` | Page loop, canvas (content stream operators), output pipeline |
| `tests/Papira.Tests` | Unit and end-to-end tests |
| `samples/Papira.Samples` | Example documents and benchmark |

### How layout works

Every element implements three operations:

- `Measure(available)` is side-effect free. It reports whether the element's remaining content is `Empty`, fits entirely (`Full`), fits partially (`Partial`), or doesn't fit at all and must move to the next page (`Wrap`).
- `Draw(available)` renders what fits and advances the element's internal state to the remaining content.
- `Reset()` restores the initial state. It is used for repeated headers and for the second layout pass when `TotalPages()` is used.

## Pull requests

1. Open an issue first for larger changes or new public API, so the design can be discussed before you invest time.
2. Keep changes focused; one topic per pull request.
3. Add or update tests. Layout changes should include a test that exercises pagination.
4. Make sure `dotnet build -c Release` has no warnings (warnings are errors in the library) and `dotnet test` passes.
5. Update `CHANGELOG.md` under **Unreleased** for user-visible changes.

### Guidelines

- **No runtime dependencies.** Papira must remain dependency-free. Test-only packages are fine.
- **Performance matters.** Avoid allocations in hot paths (text shaping, content stream writing). Include benchmark numbers when changing them.
- **Public API is deliberate.** Keep implementation types `internal`. Document new public members with XML comments unless the name says it all (e.g. `Bold()`).
- Follow the existing code style (`.editorconfig`): file-scoped namespaces, `_camelCase` private fields, English identifiers and comments.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
