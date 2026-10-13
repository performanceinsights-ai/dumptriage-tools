using DumpTriage.Common.Idle;

namespace DumpTriageAgent.Idle;

/// <summary>
/// Headless hosts have no user input to measure, so report "never idle"
/// (same safe fallback as Win32IdleTimeProvider when it can't tell): Idle
/// batching never fires, which is why Program logs a warning if it's chosen.
/// </summary>
public sealed class NullIdleTimeProvider : IIdleTimeProvider
{
    public TimeSpan GetIdleDuration() => TimeSpan.Zero;
}
