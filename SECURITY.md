# Security Policy

## Supported versions

Papira is in early development (0.x). Security fixes are released only for the latest published version.

| Version | Supported |
|---------|-----------|
| 0.2.x   | Yes       |
| < 0.2   | No        |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions or pull requests.**

Report them privately with GitHub's [private vulnerability reporting](https://github.com/mertgundoganx/Papira/security/advisories/new) (the **Security** tab of the repository, then **Report a vulnerability**).

Please include:

- the affected version,
- a description of the issue and its impact,
- steps to reproduce, ideally a minimal program and any input file (font, image) that triggers the problem.

You can expect an acknowledgement within 7 days. Once the issue is confirmed, a fix will be prepared and released, and the advisory will be published with credit to you, unless you prefer to stay anonymous.

## Scope and threat model

Papira runs inside your process and parses the fonts and images you give it. Areas where security matters most:

- **Parsing untrusted input.** The TrueType parser and the PNG/JPEG decoders process binary data that may come from users. Malformed input should fail with an exception (`InvalidDataException`, `NotSupportedException`). Crashes, hangs, unbounded memory use or out-of-bounds access caused by crafted input are all in scope.
- **Resource exhaustion.** Layouts that make the engine loop without progress should hit `DocumentSettings.MaxPages` or throw `DocumentLayoutException`.

Out of scope:

- Content that you put into a document yourself (Papira does not sanitize text; a PDF is not an HTML context).
- Vulnerabilities in PDF viewers that open files produced by Papira.

If you accept fonts or images from untrusted users in a server application, also enforce your own limits on upload size and request time.
