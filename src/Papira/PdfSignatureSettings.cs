using System.Security.Cryptography.X509Certificates;

namespace Papira;

/// <summary>
/// Signs the document as it is written. The signature covers every byte of the file, so a reader can
/// tell that nothing in it has changed since it was signed, and who signed it.
/// </summary>
public sealed class PdfSignatureSettings
{
    /// <summary>
    /// The certificate to sign with, which has to carry its private key — as a PKCS #12 (.pfx) file
    /// loaded with its password does.
    /// </summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>
    /// The certificates between the signing one and the root, which are written into the signature so
    /// that a reader can follow the chain without looking them up.
    /// </summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    /// <summary>
    /// The field to sign, named as <c>SignatureField</c> named it. Without one the document is signed
    /// invisibly: the signature is there, but nothing is drawn for it.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>Why the document was signed, which a reader shows beside the signature.</summary>
    public string? Reason { get; init; }

    /// <summary>Where it was signed.</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>The name of the signer; by default the one the certificate carries.</summary>
    public string? Name { get; init; }

    /// <summary>When it was signed. Default: now.</summary>
    public DateTimeOffset? Date { get; init; }
}
