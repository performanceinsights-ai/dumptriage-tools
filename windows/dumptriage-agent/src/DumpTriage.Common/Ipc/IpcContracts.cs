using DumpTriage.Common.Models;

namespace DumpTriage.Common.Ipc;

/// <summary>
/// Named pipe used between the service (DumpTriageAgent) and the per-user
/// tray helper (DumpTriageTray). NamedPipeServerStream/ClientStream work
/// cross-platform in .NET (Unix domain sockets on Linux/macOS), but this
/// pipe only ever runs on Windows in practice.
/// </summary>
public static class IpcPipe
{
    public const string PipeName = "DumpTriageAgent";
}

public enum IpcMessageType
{
    AskApproval,
    ApprovalResponse,
    StatusUpdate,
    ManualSubmit,
    PauseAgent,
    ResumeAgent,
}

/// <summary>
/// Envelope for every message on the pipe. Serialized as a single line of
/// JSON (newline-delimited framing) in each direction. Only the field(s)
/// relevant to <see cref="Type"/> are populated.
/// </summary>
public sealed class IpcMessage
{
    public required IpcMessageType Type { get; init; }

    /// <summary>Service -&gt; tray, for AskApproval.</summary>
    public string? DumpId { get; init; }
    public string? DumpFileName { get; init; }
    public long? DumpSizeBytes { get; init; }
    public DumpType? DumpType { get; init; }
    public int? AskTimeoutSeconds { get; init; }

    /// <summary>Tray -&gt; service, for ApprovalResponse/ManualSubmit.</summary>
    public bool? Approved { get; init; }

    /// <summary>Service -&gt; tray, for StatusUpdate.</summary>
    public int? QueuedCount { get; init; }
    public int? PendingApprovalCount { get; init; }
    public bool? Paused { get; init; }
    public string? StatusMessage { get; init; }
}
