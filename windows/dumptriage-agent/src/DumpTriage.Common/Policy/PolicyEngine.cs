using DumpTriage.Common.Models;

namespace DumpTriage.Common.Policy;

/// <summary>
/// Pure decision function: given a detected dump and the configured policy,
/// what should happen to it? Deliberately has no side effects (no I/O, no
/// clock dependency beyond the passed-in "now") so it's trivial to unit test
/// against the full mode x rule matrix.
/// </summary>
public static class PolicyEngine
{
    public static ApprovalOutcome Evaluate(DumpRecord dump, AgentPolicyConfig config, DateTimeOffset nowLocal)
    {
        return config.Mode switch
        {
            DecisionMode.AlwaysSubmit => ApprovalOutcome.AutoSubmit,
            DecisionMode.AskFirst => ApprovalOutcome.RequiresApproval,
            DecisionMode.NotifyOnly => ApprovalOutcome.ManualOnly,
            DecisionMode.RuleBased => EvaluateRules(dump, config, nowLocal),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Mode, "unknown decision mode"),
        };
    }

    private static ApprovalOutcome EvaluateRules(DumpRecord dump, AgentPolicyConfig config, DateTimeOffset nowLocal)
    {
        foreach (var rule in config.Rules)
        {
            if (rule.Matches(dump, nowLocal))
            {
                return ToOutcome(rule.Action);
            }
        }

        return ToOutcome(config.DefaultRuleAction);
    }

    private static ApprovalOutcome ToOutcome(RuleAction action) => action switch
    {
        RuleAction.Submit => ApprovalOutcome.AutoSubmit,
        RuleAction.Ask => ApprovalOutcome.RequiresApproval,
        RuleAction.Skip => ApprovalOutcome.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "unknown rule action"),
    };

    /// <summary>
    /// Resolves what an unanswered approval prompt becomes once its timeout
    /// elapses, per <see cref="AgentPolicyConfig.AskTimeoutDefaultAction"/>.
    /// </summary>
    public static ApprovalOutcome ResolveAskTimeout(AgentPolicyConfig config) =>
        ToOutcome(config.AskTimeoutDefaultAction);
}
