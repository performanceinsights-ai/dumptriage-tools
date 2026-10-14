namespace DumpTriage.Common.Upload;

/// <summary>When to try an upload again; the same numbers as the Linux agent.</summary>
public static class RetryPolicy
{
    /// <summary>Transient failures before a dump is marked Failed.</summary>
    public const int MaxAttempts = 10;

    /// <summary>Wait after the nth transient failure (n from 1): 30 s doubling, capped at 1 h (#6).</summary>
    public static TimeSpan TransientDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(failures, 1)), 3600));

    /// <summary>
    /// Pause after the nth 401/403 in a row (n from 1): 10 min doubling, capped at 6 h. Every attempt
    /// sends the whole dump, so a revoked key mustn't mean gigabytes every ten minutes forever.
    /// </summary>
    public static TimeSpan AuthPause(int refusalsInARow) =>
        TimeSpan.FromMinutes(Math.Min(10 * Math.Pow(2, Math.Max(refusalsInARow, 1) - 1), 360));
}
