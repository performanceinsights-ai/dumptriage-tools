namespace DumpTriage.Common.Policy;

/// <summary>
/// Decides *when* an already-approved dump actually gets submitted. Kept
/// independent of <see cref="PolicyEngine"/>, which only decides *whether*
/// it's approved — see AgentPolicyConfig.Batching. Pure function: takes the
/// signals it needs (now, idle duration) as parameters so it's testable
/// without touching the real clock or Windows idle APIs.
/// </summary>
public static class BatchScheduler
{
    public static bool ShouldSubmitNow(
        BatchingConfig config,
        DateTimeOffset nowLocal,
        TimeSpan systemIdleDuration,
        DateTimeOffset? lastScheduledFlushUtc)
    {
        return config.Mode switch
        {
            BatchMode.Immediate => true,
            BatchMode.Idle => systemIdleDuration >= TimeSpan.FromMinutes(config.IdleMinutes),
            BatchMode.ScheduledTimeOfDay => IsAtOrPastAnyScheduledTimeSinceLastFlush(config, nowLocal, lastScheduledFlushUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Mode, "unknown batch mode"),
        };
    }

    /// <summary>
    /// ScheduledTimeOfDay: only when a scheduled time has passed since the last flush. The caller
    /// records every flush, also one with nothing to send, so a dump queued after an empty slot waits
    /// for the next one (#9), and records a first run as a flush (null means "never ran"), so it waits
    /// for the first slot rather than uploading at once.
    /// </summary>
    private static bool IsAtOrPastAnyScheduledTimeSinceLastFlush(
        BatchingConfig config, DateTimeOffset nowLocal, DateTimeOffset? lastScheduledFlushUtc)
    {
        var today = DateOnly.FromDateTime(nowLocal.LocalDateTime);
        var nowTime = TimeOnly.FromDateTime(nowLocal.LocalDateTime);

        foreach (var scheduled in config.ScheduledTimesLocal)
        {
            if (nowTime < scheduled) continue;

            var scheduledInstant = new DateTimeOffset(today.ToDateTime(scheduled), nowLocal.Offset);
            if (lastScheduledFlushUtc is not null && lastScheduledFlushUtc < scheduledInstant)
            {
                return true;
            }
        }

        return false;
    }
}
