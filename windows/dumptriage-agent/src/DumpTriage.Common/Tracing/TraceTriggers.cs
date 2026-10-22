namespace DumpTriage.Common.Tracing;

/// <summary>
/// The trigger rules of the shared fixture (<c>tests/fixtures/tracing/triggers.json</c>, #26), the same as the Linux
/// agent's: met when the value stays at or above the threshold for sustainSec; a dip, or no reading, restarts the clock;
/// once met it must be sustained again.
/// </summary>
public sealed class TriggerRunner
{
    private readonly double _threshold;
    private readonly int _sustainSec;
    private double? _runStart;

    public TriggerRunner(double threshold, int sustainSec)
    {
        _threshold = threshold;
        _sustainSec = sustainSec;
    }

    public bool Feed(double t, double? value)
    {
        if (value is null || value < _threshold)
        {
            _runStart = null;
            return false;
        }
        _runStart ??= t;
        if (t - _runStart.Value >= _sustainSec)
        {
            _runStart = null;
            return true;
        }
        return false;
    }
}

public enum CaptureOutcome
{
    Fire,
    Cooldown,
    Capped,
}

public static class CaptureLimiter
{
    public const int SecondsPerDay = 86400;

    /// <summary>What a met trigger does at time <paramref name="t"/> (seconds) given earlier captures: cooldown is
    /// reported before the daily cap.</summary>
    public static CaptureOutcome Decide(double t, TraceLimits limits, IEnumerable<double> history)
    {
        var past = history.ToList();
        if (past.Any(a => a > t - limits.CooldownMin * 60.0)) return CaptureOutcome.Cooldown;
        if (past.Count(a => a > t - SecondsPerDay) >= limits.DailyCap) return CaptureOutcome.Capped;
        return CaptureOutcome.Fire;
    }
}
