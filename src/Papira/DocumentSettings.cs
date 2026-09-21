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

public sealed record DocumentSettings
{
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

    /// <summary>Defaults to the time of generation.</summary>
    public DateTimeOffset? CreationDate { get; init; }
}
