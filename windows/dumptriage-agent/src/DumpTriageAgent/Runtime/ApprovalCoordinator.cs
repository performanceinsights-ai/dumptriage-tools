using System.Collections.Concurrent;

namespace DumpTriageAgent.Runtime;

/// <summary>
/// Bridges the watcher (which decides a dump needs human approval) and the
/// pipe server (which relays the tray app's answer back). A dump waiting on
/// approval blocks on a TaskCompletionSource keyed by dump id until either
/// the tray responds or the configured timeout elapses.
/// </summary>
public sealed class ApprovalCoordinator
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deadlines = new();

    public async Task<bool?> WaitForApprovalAsync(string dumpId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(dumpId, tcs))
        {
            // Already awaiting approval for this dump id — shouldn't happen
            // in practice (watcher dedups by file path before requesting).
            return null;
        }

        _deadlines[dumpId] = DateTimeOffset.UtcNow + timeout;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch (TaskCanceledException)
        {
            // Timed out (or the service is shutting down) with no answer.
            return null;
        }
        finally
        {
            _pending.TryRemove(dumpId, out _);
            _deadlines.TryRemove(dumpId, out _);
        }
    }

    public bool Resolve(string dumpId, bool approved)
    {
        return _pending.TryGetValue(dumpId, out var tcs) && tcs.TrySetResult(approved);
    }

    /// <summary>
    /// Dump ids currently mid-timeout awaiting a tray response — used to
    /// re-send AskApproval prompts when a tray (re)connects, since the
    /// original prompt may have been sent before any tray was connected
    /// (SendAsync silently drops messages with nobody listening) and would
    /// otherwise be lost until the timeout's default action fires with the
    /// user never having seen it.
    /// </summary>
    public ICollection<string> GetPendingDumpIds() => _pending.Keys;

    /// <summary>Whole seconds this dump's approval wait has left, or null if none is running.</summary>
    public int? SecondsLeft(string dumpId) =>
        _deadlines.TryGetValue(dumpId, out var deadline)
            ? Math.Max(1, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds))
            : null;
}
