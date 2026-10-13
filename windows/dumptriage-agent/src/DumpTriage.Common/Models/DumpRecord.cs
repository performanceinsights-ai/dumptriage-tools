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
}
