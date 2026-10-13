namespace DumpTriage.Common.Ipc;

/// <summary>
/// The service's link to a user-facing UI. On Windows this is the named pipe
/// to the tray app; on Linux there is no tray, so a no-op implementation is
/// used and approvals resolve through policy timeouts.
/// </summary>
public interface ITrayChannel
{
    /// <summary>True when a UI can actually receive prompts.</summary>
    bool IsInteractive { get; }

    event Action? TrayConnected;

    /// <summary>Best-effort: dropped if nobody is listening.</summary>
    Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default);
}

public sealed class NullTrayChannel : ITrayChannel
{
    public bool IsInteractive => false;

    public event Action? TrayConnected { add { } remove { } }

    public Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
