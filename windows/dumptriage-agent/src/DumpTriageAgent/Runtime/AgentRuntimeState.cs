namespace DumpTriageAgent.Runtime;

/// <summary>
/// Small piece of shared mutable state the pipe server, watcher, and upload
/// worker all touch: whether the tray has paused the agent, and a place to
/// publish "please submit this dump now" requests coming from the tray's
/// manual-submit action (used in NotifyOnly mode).
/// </summary>
public sealed class AgentRuntimeState
{
    private volatile bool _paused;

    public bool IsPaused => _paused;

    public event Action<string>? ManualSubmitRequested;

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void RequestManualSubmit(string dumpId) => ManualSubmitRequested?.Invoke(dumpId);
}
