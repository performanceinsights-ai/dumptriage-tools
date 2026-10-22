namespace DumpTriage.Common.Models;

public enum DumpType
{
    Unknown,
    UserMode,
    KernelMode,
    /// <summary>A WPR performance trace the agent captured (website#1999): a zip with one .etl, uploaded to
    /// /api/v1/traces with its manifest. Written to the queue as "Trace"; an agent older than this skips such rows.</summary>
    Trace,
}
