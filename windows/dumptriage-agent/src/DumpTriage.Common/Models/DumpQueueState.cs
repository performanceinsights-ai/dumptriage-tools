namespace DumpTriage.Common.Models;

/// <summary>
/// Lifecycle of a detected dump as it moves through the agent.
/// Detected -&gt; (PendingApproval) -&gt; Queued -&gt; Submitting -&gt; Submitted | Failed | Skipped
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
}
