using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;

namespace DumpTriage.Common.Tests;

public class PolicyEngineTests
{
    private static DumpRecord MakeDump(long sizeBytes = 1024, DumpType type = DumpType.UserMode, string path = "C:\\Dumps\\test.dmp")
    {
        return new DumpRecord
        {
            Id = "1",
            FilePath = path,
            SizeBytes = sizeBytes,
            DumpType = type,
            DetectedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public void AlwaysSubmit_ReturnsAutoSubmit_RegardlessOfDump()
    {
        var config = new AgentPolicyConfig { Mode = DecisionMode.AlwaysSubmit };
        var outcome = PolicyEngine.Evaluate(MakeDump(), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.AutoSubmit, outcome);
    }

    [Fact]
    public void AskFirst_ReturnsRequiresApproval()
    {
        var config = new AgentPolicyConfig { Mode = DecisionMode.AskFirst };
        var outcome = PolicyEngine.Evaluate(MakeDump(), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.RequiresApproval, outcome);
    }

    [Fact]
    public void NotifyOnly_ReturnsManualOnly()
    {
        var config = new AgentPolicyConfig { Mode = DecisionMode.NotifyOnly };
        var outcome = PolicyEngine.Evaluate(MakeDump(), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.ManualOnly, outcome);
    }

    [Fact]
    public void RuleBased_FirstMatchingRuleWins()
    {
        var config = new AgentPolicyConfig
        {
            Mode = DecisionMode.RuleBased,
            Rules =
            {
                new DumpRule { MaxSizeBytes = 100, Action = RuleAction.Submit },
                new DumpRule { DumpType = DumpType.UserMode, Action = RuleAction.Ask },
            },
        };

        // 1024 bytes doesn't match the first rule (max 100), but matches the second.
        var outcome = PolicyEngine.Evaluate(MakeDump(sizeBytes: 1024), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.RequiresApproval, outcome);
    }

    [Fact]
    public void RuleBased_NoMatch_FallsBackToDefaultAction()
    {
        var config = new AgentPolicyConfig
        {
            Mode = DecisionMode.RuleBased,
            Rules = { new DumpRule { DumpType = DumpType.KernelMode, Action = RuleAction.Submit } },
            DefaultRuleAction = RuleAction.Skip,
        };

        var outcome = PolicyEngine.Evaluate(MakeDump(type: DumpType.UserMode), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.Skip, outcome);
    }

    [Fact]
    public void RuleBased_PathGlob_Matches()
    {
        var config = new AgentPolicyConfig
        {
            Mode = DecisionMode.RuleBased,
            Rules = { new DumpRule { PathGlob = "C:\\Dumps\\*", Action = RuleAction.Submit } },
            DefaultRuleAction = RuleAction.Ask,
        };

        var outcome = PolicyEngine.Evaluate(MakeDump(path: "C:\\Dumps\\crash-123.dmp"), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.AutoSubmit, outcome);
    }

    [Theory]
    [InlineData(23, 0, true)]   // 23:00, inside 22:00-06:00
    [InlineData(2, 0, true)]    // 02:00, inside 22:00-06:00 (after midnight)
    [InlineData(12, 0, false)]  // noon, outside 22:00-06:00
    public void RuleBased_OvernightBusinessHoursWindow_WrapsAcrossMidnight(int hour, int minute, bool shouldMatch)
    {
        var config = new AgentPolicyConfig
        {
            Mode = DecisionMode.RuleBased,
            Rules =
            {
                new DumpRule
                {
                    BusinessHoursStart = new TimeOnly(22, 0),
                    BusinessHoursEnd = new TimeOnly(6, 0),
                    Action = RuleAction.Submit,
                },
            },
            DefaultRuleAction = RuleAction.Skip,
        };

        var now = new DateTimeOffset(2026, 9, 22, hour, minute, 0, TimeSpan.Zero);
        var outcome = PolicyEngine.Evaluate(MakeDump(), config, now);

        Assert.Equal(shouldMatch ? ApprovalOutcome.AutoSubmit : ApprovalOutcome.Skip, outcome);
    }

    [Fact]
    public void RuleBased_PathGlob_NonMatchingPathFallsThrough()
    {
        var config = new AgentPolicyConfig
        {
            Mode = DecisionMode.RuleBased,
            Rules = { new DumpRule { PathGlob = "C:\\Dumps\\*", Action = RuleAction.Submit } },
            DefaultRuleAction = RuleAction.Skip,
        };

        var outcome = PolicyEngine.Evaluate(MakeDump(path: "C:\\Windows\\MEMORY.DMP"), config, DateTimeOffset.Now);
        Assert.Equal(ApprovalOutcome.Skip, outcome);
    }

    [Theory]
    [InlineData(RuleAction.Submit, ApprovalOutcome.AutoSubmit)]
    [InlineData(RuleAction.Skip, ApprovalOutcome.Skip)]
    public void ResolveAskTimeout_MapsConfiguredDefaultAction(RuleAction configured, ApprovalOutcome expected)
    {
        var config = new AgentPolicyConfig { AskTimeoutDefaultAction = configured };
        Assert.Equal(expected, PolicyEngine.ResolveAskTimeout(config));
    }

    [Fact]
    public void DefaultConfig_TimesOutToSkip_NotSubmit()
    {
        // Privacy-conscious default: an unanswered approval prompt should fail
        // closed. Dumps can contain sensitive memory contents.
        var config = new AgentPolicyConfig();
        Assert.Equal(ApprovalOutcome.Skip, PolicyEngine.ResolveAskTimeout(config));
    }
}
