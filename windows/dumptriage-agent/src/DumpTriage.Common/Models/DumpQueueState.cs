namespace DumpTriage.Common.Models;

/// <summary>
/// Lifecycle of a detected dump as it moves through the agent.
/// Detected -&gt; (PendingApproval) -&gt; Queued -&gt; Submitting -&gt; Submitted | Failed | Skipped | Superseded
/// Superseded: the file changed before it was uploaded, and its new contents are a new record.
/// </summary>
public enum DumpQueueState
{
    Detected,
    PendingApproval,
    Queued,
    Submitting,
    Submitted,
    Failed,
    Skipped,
    Superseded,
}
