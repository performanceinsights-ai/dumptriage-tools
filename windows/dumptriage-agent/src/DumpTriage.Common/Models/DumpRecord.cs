namespace DumpTriage.Common.Models;

/// <summary>
/// A dump file the agent has detected, persisted in the local queue so state
/// survives a service restart.
/// </summary>
public sealed class DumpRecord
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required long SizeBytes { get; set; }

    /// <summary>
    /// The file's last-write time when it was hashed. With FilePath and
    /// SizeBytes it tells a file already seen from a new dump written at the
    /// same path, e.g. the next crash's MEMORY.DMP (website#1486). Null on records
    /// from before it was kept.
    /// </summary>
    public DateTimeOffset? FileLastWriteUtc { get; set; }
    public string? Sha256 { get; set; }
    public DumpType DumpType { get; set; } = DumpType.Unknown;
    public DumpQueueState State { get; set; } = DumpQueueState.Detected;
    public required DateTimeOffset DetectedAtUtc { get; init; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set once the archival copy to the central location succeeds; independent of submission State.</summary>
    public DateTimeOffset? CentralCopiedAtUtc { get; set; }
    public string? CentralCopyError { get; set; }

    /// <summary>Not uploaded before this time: exponential backoff after a transient failure (website#1488).</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    /// <summary>A trace's manifest.json (DumpType.Trace only); null for a dump.</summary>
    public string? Manifest { get; set; }

    /// <summary>First failure in the current run of transient failures; the dump gives up a fixed time after it.</summary>
    public DateTimeOffset? FirstFailedAtUtc { get; set; }
}
