using DumpTriage.Common.Models;

namespace DumpTriage.Common.Policy;

public enum DecisionMode
{
    AlwaysSubmit,
    AskFirst,
    NotifyOnly,
    RuleBased,
}

public enum RuleAction
{
    Submit,
    Ask,
    Skip,
}

/// <summary>
/// What the policy engine decided should happen to a detected dump.
/// </summary>
public enum ApprovalOutcome
{
    /// <summary>Enqueue for submission with no human involvement.</summary>
    AutoSubmit,

    /// <summary>Prompt the logged-in user (toast); apply AskTimeoutDefaultAction on timeout.</summary>
    RequiresApproval,

    /// <summary>Notify only — submission requires an explicit manual action later.</summary>
    ManualOnly,

    /// <summary>Do not submit; not queued.</summary>
    Skip,
}

/// <summary>
/// One rule in a RuleBased policy. Rules are evaluated in list order; the
/// first rule whose conditions all match wins. Null conditions are ignored
/// (treated as "matches anything").
/// </summary>
public sealed class DumpRule
{
    public string? Name { get; init; }
    public long? MaxSizeBytes { get; init; }
    public long? MinSizeBytes { get; init; }
    public DumpType? DumpType { get; init; }
    public string? PathGlob { get; init; }
    public TimeOnly? BusinessHoursStart { get; init; }
    public TimeOnly? BusinessHoursEnd { get; init; }
    public required RuleAction Action { get; init; }

    public bool Matches(Models.DumpRecord dump, DateTimeOffset nowLocal)
    {
        if (MaxSizeBytes is { } max && dump.SizeBytes > max) return false;
        if (MinSizeBytes is { } min && dump.SizeBytes < min) return false;
        if (DumpType is { } type && dump.DumpType != type) return false;
        if (PathGlob is { } glob && !MatchesGlob(dump.FilePath, glob)) return false;
        if (BusinessHoursStart is { } start && BusinessHoursEnd is { } end)
        {
            var timeOfDay = TimeOnly.FromDateTime(nowLocal.LocalDateTime);
            var inWindow = start <= end
                ? timeOfDay >= start && timeOfDay <= end
                : timeOfDay >= start || timeOfDay <= end; // overnight window, e.g. 22:00-06:00
            if (!inWindow) return false;
        }
        return true;
    }

    private static bool MatchesGlob(string path, string glob)
    {
        // Simple glob: '*' matches any run of characters, everything else is literal.
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            path, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

public enum BatchMode
{
    Immediate,
    ScheduledTimeOfDay,
    Idle,
}

public sealed class BatchingConfig
{
    public BatchMode Mode { get; init; } = BatchMode.Immediate;

    /// <summary>Times of day (local) to flush the queue, used when Mode == ScheduledTimeOfDay.</summary>
    public List<TimeOnly> ScheduledTimesLocal { get; init; } = new();

    /// <summary>Minutes of no user input required before flushing, used when Mode == Idle.</summary>
    public int IdleMinutes { get; init; } = 15;
}

public sealed class AgentPolicyConfig
{
    public DecisionMode Mode { get; init; } = DecisionMode.AskFirst;
    public List<DumpRule> Rules { get; init; } = new();

    /// <summary>Fallback action for RuleBased mode when no rule matches.</summary>
    public RuleAction DefaultRuleAction { get; init; } = RuleAction.Ask;

    public int AskTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// What happens if the user doesn't respond to an approval prompt in time.
    /// Defaults to Skip — dumps can contain sensitive memory contents, so an
    /// unanswered prompt should fail closed, not open.
    /// </summary>
    public RuleAction AskTimeoutDefaultAction { get; init; } = RuleAction.Skip;

    public BatchingConfig Batching { get; init; } = new();
}
