namespace Papira;

public enum PdfCompression
{
    /// <summary>No compression; largest files, lowest CPU cost.</summary>
    None,

    /// <summary>Fast compression; good default for high-throughput services.</summary>
    Fastest,

    /// <summary>Balanced size and speed.</summary>
    Optimal,

    /// <summary>Smallest files, slowest.</summary>
    Smallest,
}

/// <summary>Archive standard the file should conform to.</summary>
public enum PdfStandard
{
    /// <summary>A plain PDF 1.7 file.</summary>
    None,

    /// <summary>PDF/A-2b: long-term archiving. Adds an sRGB output intent and XMP metadata.</summary>
    PdfA2b,

    /// <summary>PDF/A-3b: like PDF/A-2b but allows attachments of any type, e.g. an e-invoice XML.</summary>
    PdfA3b,
}

public sealed record DocumentSettings
{
    /// <summary>
    /// Archive standard to produce. PDF/A files embed an sRGB colour profile and XMP metadata, and cannot be encrypted.
    /// Attachments require <see cref="PdfStandard.PdfA3b"/>.
    /// </summary>
    public PdfStandard Standard { get; init; } = PdfStandard.None;

    /// <summary>Password protection and permissions. Cannot be combined with <see cref="PdfStandard"/>.</summary>
    public PdfEncryptionSettings? Encryption { get; init; }

    /// <summary>Compression of page content and embedded fonts. Default: <see cref="PdfCompression.Optimal"/>.</summary>
    public PdfCompression Compression { get; init; } = PdfCompression.Optimal;

    /// <summary>
    /// Maximum number of threads used for the output stage of a single document (compression, font subsetting, image encoding).
    /// Defaults to the processor count; set to 1 when generating many documents in parallel yourself.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = Math.Max(1, value);
    } = Environment.ProcessorCount;

    /// <summary>
    /// Writes the structure of the document beside its pages: what is a heading, a paragraph, a table,
    /// a figure. Readers for the blind follow that structure, and it is what makes a file accessible.
    /// Set <see cref="DocumentMetadata.Language"/> as well, and give every picture a description.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>Safety limit that stops runaway layouts. Default: 100 000 pages.</summary>
    public int MaxPages
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "MaxPages must be positive.");
    } = 100_000;
}

public sealed record DocumentMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string Producer { get; init; } = "Papira";

    /// <summary>
    /// The language the document is written in, as a tag such as "tr-TR" or "en". A reader for the blind
    /// needs it to pronounce the text, and a tagged document should always carry one.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>Defaults to the time of generation.</summary>
    public DateTimeOffset? CreationDate { get; init; }
}
