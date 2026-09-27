namespace Papira;

/// <summary>How an attachment relates to the document; readers and archive standards use it to find data files.</summary>
public enum AttachmentRelationship
{
    /// <summary>The attachment is the machine-readable data of the document, e.g. an e-invoice XML.</summary>
    Data,

    /// <summary>The document was generated from the attachment.</summary>
    Source,

    /// <summary>The attachment is an alternative representation of the document.</summary>
    Alternative,

    /// <summary>The attachment adds supplementary material.</summary>
    Supplement,

    /// <summary>No particular relationship.</summary>
    Unspecified,
}

/// <summary>A file embedded in the document, shown in the attachments panel of PDF viewers.</summary>
public sealed record DocumentAttachment
{
    public DocumentAttachment(string fileName, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(data);
        FileName = fileName;
        Data = data.AsSpan().ToArray();
    }

    /// <summary>Name shown to the reader, e.g. <c>invoice.xml</c>.</summary>
    public string FileName { get; }

    /// <summary>File contents; copied when the attachment is created.</summary>
    public byte[] Data { get; }

    public string? Description { get; init; }

    /// <summary>Media type of the file, e.g. <c>text/xml</c>. Required by PDF/A-3.</summary>
    public string MediaType { get; init; } = "application/octet-stream";

    public AttachmentRelationship Relationship { get; init; } = AttachmentRelationship.Unspecified;

    /// <summary>Modification date stored with the file; defaults to the document's creation date.</summary>
    public DateTimeOffset? ModificationDate { get; init; }
}
