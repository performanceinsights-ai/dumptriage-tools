namespace DumpTriageAgent.Runtime;

/// <summary>A queue-wide stop on uploads that the server asked for, and why (shown in the tray).</summary>
public sealed record UploadHold(string Reason, DateTimeOffset UntilUtc);

/// <summary>
/// Small piece of shared mutable state the pipe server, watcher, and upload
/// worker all touch: whether the tray has paused the agent, a place to
/// publish "please submit this dump now" requests coming from the tray's
/// manual-submit action (used in NotifyOnly mode), and any upload hold the
/// server's answer put on the whole queue (terms, key, closed account, quota).
/// </summary>
public sealed class AgentRuntimeState
{
    private volatile bool _paused;
    private volatile UploadHold? _uploadHold;

    public bool IsPaused => _paused;

    public event Action<string>? ManualSubmitRequested;

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void RequestManualSubmit(string dumpId) => ManualSubmitRequested?.Invoke(dumpId);

    /// <summary>The tray's "Retry failed uploads": put every Failed dump back in the queue.</summary>
    public event Action? RetryFailedRequested;

    public void RequestRetryFailed() => RetryFailedRequested?.Invoke();

    /// <summary>The upload worker asks the watcher to look at a path again (its file changed after it was hashed).</summary>
    public event Action<string>? RescanRequested;

    public void RequestRescan(string path) => RescanRequested?.Invoke(path);

    /// <summary>The current hold, or null once it has run out.</summary>
    public UploadHold? ActiveUploadHold(DateTimeOffset nowUtc) =>
        _uploadHold is { } hold && hold.UntilUtc > nowUtc ? hold : null;

    public void HoldUploads(string reason, TimeSpan duration, DateTimeOffset nowUtc) =>
        _uploadHold = new UploadHold(reason, nowUtc + duration);

    public void ClearUploadHold() => _uploadHold = null;
}
