namespace Papira;

/// <summary>What readers may do with an encrypted document.</summary>
[Flags]
public enum PdfPermissions
{
    None = 0,

    /// <summary>Printing, at low resolution unless <see cref="PrintHighResolution"/> is also set.</summary>
    Print = 1 << 0,

    /// <summary>Changing the content of the document.</summary>
    ModifyContents = 1 << 1,

    /// <summary>Copying text and graphics out of the document.</summary>
    CopyContent = 1 << 2,

    /// <summary>Adding or changing annotations.</summary>
    Annotate = 1 << 3,

    /// <summary>Filling in form fields.</summary>
    FillForms = 1 << 4,

    /// <summary>Extracting content for accessibility tools.</summary>
    ExtractForAccessibility = 1 << 5,

    /// <summary>Inserting, rotating or deleting pages.</summary>
    AssembleDocument = 1 << 6,

    /// <summary>Printing at full resolution.</summary>
    PrintHighResolution = 1 << 7,

    All = Print | ModifyContents | CopyContent | Annotate | FillForms | ExtractForAccessibility | AssembleDocument | PrintHighResolution,
}

/// <summary>
/// Password protection with AES-256. Readers ask for a password when <see cref="UserPassword"/> is set;
/// with an empty user password the file opens for everyone but the permissions still apply.
/// The owner password lifts all restrictions.
/// </summary>
public sealed record PdfEncryptionSettings
{
    public string UserPassword { get; init; } = "";

    public string OwnerPassword { get; init; } = "";

    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;
}
