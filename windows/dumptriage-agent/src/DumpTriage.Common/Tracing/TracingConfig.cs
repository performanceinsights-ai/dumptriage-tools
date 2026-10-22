using System.Text.RegularExpressions;
using DumpTriage.Common.Policy;

namespace DumpTriage.Common.Tracing;

/// <summary>
/// The <c>tracing</c> section of the agent config (website#1999, dumptriage-tools#29): the same keys as the Linux agent and
/// the macOS script, defined once in <c>schemas/tracing-config.schema.json</c> (#26). Off until the host opts in; traces
/// are held for approval by default.
/// </summary>
public sealed class TracingConfig
{
    public bool Enabled { get; init; } = false;

    /// <summary>"wpr" is the only backend of this agent.</summary>
    public string Backend { get; init; } = "wpr";

    /// <summary>The submission policy for a trace; the dump policy's other settings (ask timeout, batching) apply.</summary>
    public DecisionMode Submission { get; init; } = DecisionMode.AskFirst;

    public List<TraceTrigger> Triggers { get; init; } = new();

    public TraceLimits Limits { get; init; } = new();

    public static readonly string[] TriggerTypes =
        { "cpuPercent", "loadPerCpu", "psiSomeAvg10", "diskLatencyMs", "process", "hangEvent", "schedule" };

    private static readonly string[] ThresholdTypes = { "cpuPercent", "loadPerCpu", "psiSomeAvg10", "diskLatencyMs", "process" };
    private static readonly Regex NamePattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    /// <summary>What is wrong with this section, as the schema and this agent see it; empty when usable. The agent does
    /// not trace on a section it cannot use (fail closed).</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (Backend != "wpr") problems.Add($"backend '{Backend}' is not available in the Windows agent (wpr is)");
        if (Triggers.Count == 0) problems.Add("no triggers are configured");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Triggers.Count; i++)
        {
            var t = Triggers[i];
            var where = $"triggers[{i}]";
            if (t.Name is null || !NamePattern.IsMatch(t.Name)) problems.Add($"{where}: name must be 1 to 64 letters, digits, '.', '_' or '-'");
            else if (!names.Add(t.Name)) problems.Add($"{where}: name '{t.Name}' is used twice");
            if (!TriggerTypes.Contains(t.Type)) { problems.Add($"{where}: type '{t.Type}' is not one of {string.Join(", ", TriggerTypes)}"); continue; }
            if (ThresholdTypes.Contains(t.Type) && t.Threshold is null) problems.Add($"{where}: {t.Type} needs a threshold");
            if (t.Type == "process" && string.IsNullOrWhiteSpace(t.Process)) problems.Add($"{where}: a process trigger needs a process name");
            if (t.Type == "schedule" && (t.IntervalMin is null || t.IntervalMin < 1)) problems.Add($"{where}: a schedule trigger needs intervalMin of 1 or more");
            if (t.Type == "psiSomeAvg10") problems.Add($"{where}: psiSomeAvg10 is Linux pressure stall information; Windows has none");
            if (t.SustainSec < 0) problems.Add($"{where}: sustainSec must be 0 or more");
        }
        if (Limits.MaxDurationSec < 1) problems.Add("limits.maxDurationSec must be 1 or more");
        if (Limits.MaxDiskMB < 1) problems.Add("limits.maxDiskMB must be 1 or more");
        if (Limits.MaxOverheadPercent is <= 0 or > 100) problems.Add("limits.maxOverheadPercent must be above 0 and at most 100");
        if (Limits.CooldownMin < 0) problems.Add("limits.cooldownMin must be 0 or more");
        if (Limits.DailyCap < 0) problems.Add("limits.dailyCap must be 0 or more");
        return problems;
    }
}

public sealed class TraceTrigger
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public double? Threshold { get; init; }
    /// <summary>Scopes cpuPercent to one process, and names the process of a <c>process</c> trigger (without .exe).</summary>
    public string? Process { get; init; }
    public int SustainSec { get; init; }
    public int? IntervalMin { get; init; }
}

public sealed class TraceLimits
{
    public int MaxDurationSec { get; init; } = 60;
    public int MaxDiskMB { get; init; } = 512;
    public double MaxOverheadPercent { get; init; } = 2;
    public int CooldownMin { get; init; } = 60;
    public int DailyCap { get; init; } = 4;
}
