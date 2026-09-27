using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Papira.Pdf;

/// <summary>
/// The signature a signed PDF carries: a detached CMS message (PKCS #7) over the bytes of the document,
/// as RFC 5652 describes it and as <c>adbe.pkcs7.detached</c> expects it. Papira builds it itself, from
/// the certificate it is given and the cryptography of the runtime, so that signing needs nothing else.
/// </summary>
internal static class CmsSignature
{
    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string Data = "1.2.840.113549.1.7.1";
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string RsaEncryption = "1.2.840.113549.1.1.1";
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    private const string ContentTypeAttribute = "1.2.840.113549.1.9.3";
    private const string MessageDigestAttribute = "1.2.840.113549.1.9.4";
    private const string SigningTimeAttribute = "1.2.840.113549.1.9.5";

    /// <summary>
    /// Signs the digest of the document. The signed attributes carry the digest rather than the document
    /// itself, which is what makes the message a detached one.
    /// </summary>
    public static byte[] Create(byte[] digest, X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain, DateTimeOffset signedAt)
    {
        var attributes = SignedAttributes(digest, signedAt);
        var (signature, algorithm) = Sign(certificate, attributes);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(SignedData);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);

                using (writer.PushSetOf())
                    WriteAlgorithm(writer, Sha256, null);

                // Detached: the content the signature covers is the document, which is not repeated here.
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(Data);

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    writer.WriteEncodedValue(certificate.RawData);
                    foreach (var extra in chain)
                    {
                        if (!extra.RawData.AsSpan().SequenceEqual(certificate.RawData))
                            writer.WriteEncodedValue(extra.RawData);
                    }
                }

                using (writer.PushSetOf())
                    WriteSigner(writer, certificate, attributes, signature, algorithm);
            }
        }

        return writer.Encode();
    }

    private static void WriteSigner(AsnWriter writer, X509Certificate2 certificate, byte[] attributes, byte[] signature, string algorithm)
    {
        using (writer.PushSequence())
        {
            writer.WriteInteger(1);

            // Which certificate signed: its issuer and the serial number it gave.
            using (writer.PushSequence())
            {
                writer.WriteEncodedValue(certificate.IssuerName.RawData);
                writer.WriteInteger(certificate.SerialNumberBytes.Span);
            }

            WriteAlgorithm(writer, Sha256, null);

            // The attributes are signed as a set, and carried as the field that holds them.
            var carried = attributes.ToArray();
            carried[0] = 0xA0;
            writer.WriteEncodedValue(carried);

            WriteAlgorithm(writer, algorithm, algorithm == RsaEncryption ? AsnNull : null);
            writer.WriteOctetString(signature);
        }
    }

    /// <summary>What the signature is actually computed over: the digest of the document, with its kind and the time.</summary>
    private static byte[] SignedAttributes(byte[] digest, DateTimeOffset signedAt)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSetOf())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(ContentTypeAttribute);
                using (writer.PushSetOf())
                    writer.WriteObjectIdentifier(Data);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(MessageDigestAttribute);
                using (writer.PushSetOf())
                    writer.WriteOctetString(digest);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SigningTimeAttribute);
                using (writer.PushSetOf())
                    writer.WriteUtcTime(signedAt.ToUniversalTime());
            }
        }

        return writer.Encode();
    }

    private static (byte[] Signature, string Algorithm) Sign(X509Certificate2 certificate, byte[] attributes)
    {
        if (certificate.GetRSAPrivateKey() is { } rsa)
            return (rsa.SignData(attributes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), RsaEncryption);

        if (certificate.GetECDsaPrivateKey() is { } ecdsa)
            return (ecdsa.SignData(attributes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), EcdsaWithSha256);

        throw new InvalidOperationException(
            "The certificate has no private key Papira can sign with. Load it with its key — a PKCS #12 (.pfx) file, say — and make sure the key is an RSA or an elliptic curve one.");
    }

    /// <summary>The DER encoding of ASN.1 NULL, which an RSA algorithm identifier has to carry.</summary>
    private static readonly byte[] AsnNull = [0x05, 0x00];

    private static void WriteAlgorithm(AsnWriter writer, string oid, byte[]? parameters)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);

            // RFC 5754: a SHA-2 algorithm is written without parameters, an RSA key with a null.
            if (parameters != null)
                writer.WriteEncodedValue(parameters);
        }
    }
}
