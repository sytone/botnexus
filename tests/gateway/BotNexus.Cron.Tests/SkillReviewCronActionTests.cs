using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron.Tests;

public sealed class SkillReviewCronActionTests
{
    private static SkillReviewSignals TriggeringSignals() => new()
    {
        ToolCallCount = 6,
        SkillWasLoaded = true,
        SessionCount = 2
    };

    // --- Config gating: default OFF, non-breaking ---

    [Fact]
    public void ShouldTriggerReview_DisabledConfig_ReturnsFalseEvenWhenSignalsQualify()
    {
        var config = new SkillReviewConfig { Enabled = false };
        SkillReviewCronAction.ShouldTriggerReview(TriggeringSignals(), config).ShouldBeFalse();
    }

    [Fact]
    public void SkillReviewConfig_DefaultsToEnabled()
    {
        // Once a skill-review job exists it is active out of the box; a user opts a
        // specific job out via enabled=false in its metadata.
        new SkillReviewConfig().Enabled.ShouldBeTrue();
    }

    [Fact]
    public void SkillReviewConfig_DefaultsLookbackAndBounds()
    {
        var config = new SkillReviewConfig();
        config.LookbackHours.ShouldBe(24);
        config.MaxSessions.ShouldBe(50);
        config.MinToolCalls.ShouldBe(5);
    }

    [Fact]
    public void FromMetadata_ReadsConfigOnly_NotSignals()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["minToolCalls"] = 3,
            ["lookbackHours"] = 12,
            ["maxSessions"] = 10
        };

        var config = SkillReviewConfig.FromMetadata(metadata);

        config.Enabled.ShouldBeTrue();
        config.MinToolCalls.ShouldBe(3);
        config.LookbackHours.ShouldBe(12);
        config.MaxSessions.ShouldBe(10);
    }

    [Fact]
    public void FromMetadata_DefaultsToEnabledWhenKeyAbsent()
    {
        SkillReviewConfig.FromMetadata(null).Enabled.ShouldBeTrue();
        SkillReviewConfig.FromMetadata(new Dictionary<string, object?>()).Enabled.ShouldBeTrue();
    }

    [Fact]
    public void FromMetadata_HonoursExplicitDisable()
    {
        var metadata = new Dictionary<string, object?> { ["enabled"] = false };
        SkillReviewConfig.FromMetadata(metadata).Enabled.ShouldBeFalse();
    }

    [Fact]
    public void FromMetadata_ClampsSubOneValuesToOne()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["minToolCalls"] = 0,
            ["lookbackHours"] = -5,
            ["maxSessions"] = 0
        };

        var config = SkillReviewConfig.FromMetadata(metadata);

        config.MinToolCalls.ShouldBe(1);
        config.LookbackHours.ShouldBe(1);
        config.MaxSessions.ShouldBe(1);
    }

    // --- Trigger conditions (each qualifying signal fires when enabled) ---

    [Fact]
    public void ShouldTriggerReview_FiveOrMoreToolCalls_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { ToolCallCount = 5 };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_FewerThanFiveToolCallsAndNoOtherSignal_DoesNotTrigger()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { ToolCallCount = 4 };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeFalse();
    }

    [Fact]
    public void ShouldTriggerReview_SkillWasLoaded_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { ToolCallCount = 1, SkillWasLoaded = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_UserCorrectedOrFrustrated_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { UserCorrectedOrFrustrated = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_DiscoveredReusableWorkflow_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { DiscoveredReusableWorkflow = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_SkillManageFailed_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { SkillManageFailed = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_LoadedSkillFoundStale_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { LoadedSkillFoundStale = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_EnabledButNoQualifyingSignal_DoesNotTrigger()
    {
        var config = new SkillReviewConfig { Enabled = true };
        SkillReviewCronAction.ShouldTriggerReview(new SkillReviewSignals(), config).ShouldBeFalse();
    }

    // --- Configurable tool-call threshold ---

    [Fact]
    public void ShouldTriggerReview_RespectsCustomToolCallThreshold()
    {
        var config = new SkillReviewConfig { Enabled = true, MinToolCalls = 10 };
        SkillReviewCronAction.ShouldTriggerReview(new SkillReviewSignals { ToolCallCount = 6 }, config).ShouldBeFalse();
        SkillReviewCronAction.ShouldTriggerReview(new SkillReviewSignals { ToolCallCount = 10 }, config).ShouldBeTrue();
    }

    // --- Signal derivation from live session history (the producer/consumer seam) ---

    private static GatewaySession SessionWith(string id, params SessionEntry[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(id),
            AgentId = AgentId.From("farnsworth")
        };
        session.AddEntries(entries);
        return session;
    }

    private static SessionEntry ToolCall(string tool, DateTimeOffset ts, bool isError = false) => new()
    {
        Role = MessageRole.Tool,
        Content = string.Empty,
        ToolName = tool,
        ToolCallId = Guid.NewGuid().ToString("N"),
        ToolArgs = "{}",
        ToolIsError = isError,
        Timestamp = ts
    };

    [Fact]
    public void FromSessions_CountsToolCallsWithinWindow_IgnoresOlderEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-24);
        var session = SessionWith("s1",
            ToolCall("read", now.AddHours(-1)),
            ToolCall("grep", now.AddHours(-2)),
            ToolCall("read", now.AddHours(-48))); // outside window

        var signals = SkillReviewSignals.FromSessions([session], cutoff);

        signals.ToolCallCount.ShouldBe(2);
        signals.SessionCount.ShouldBe(1);
        signals.SkillWasLoaded.ShouldBeFalse();
        signals.SkillManageFailed.ShouldBeFalse();
    }

    [Fact]
    public void FromSessions_DetectsSkillLoad()
    {
        var now = DateTimeOffset.UtcNow;
        var session = SessionWith("s1", ToolCall("skills", now.AddMinutes(-5)));

        var signals = SkillReviewSignals.FromSessions([session], now.AddHours(-24));

        signals.SkillWasLoaded.ShouldBeTrue();
    }

    [Fact]
    public void FromSessions_DetectsSkillManageFailure()
    {
        var now = DateTimeOffset.UtcNow;
        var session = SessionWith("s1", ToolCall("skill_manage", now.AddMinutes(-5), isError: true));

        var signals = SkillReviewSignals.FromSessions([session], now.AddHours(-24));

        signals.SkillManageFailed.ShouldBeTrue();
        signals.SkillManageFailures.ShouldContain("s1");
    }

    [Fact]
    public void FromSessions_IgnoresNonToolEntriesAndResultRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new GatewaySession
        {
            SessionId = SessionId.From("s1"),
            AgentId = AgentId.From("farnsworth")
        };
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "hi", Timestamp = now.AddMinutes(-9) },
            new SessionEntry { Role = MessageRole.Assistant, Content = "ok", Timestamp = now.AddMinutes(-8) },
            // Tool *result* record: no ToolArgs, no ToolCallId -> not counted as a start
            new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolName = "read", Timestamp = now.AddMinutes(-7) }
        });

        var signals = SkillReviewSignals.FromSessions([session], now.AddHours(-24));

        signals.ToolCallCount.ShouldBe(0);
        signals.SessionCount.ShouldBe(0);
    }

    [Fact]
    public void FromSessions_AggregatesAcrossMultipleSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var a = SessionWith("s1", ToolCall("read", now.AddHours(-1)), ToolCall("grep", now.AddHours(-1)));
        var b = SessionWith("s2", ToolCall("skill_view", now.AddHours(-2)));

        var signals = SkillReviewSignals.FromSessions([a, b], now.AddHours(-24));

        signals.ToolCallCount.ShouldBe(3);
        signals.SessionCount.ShouldBe(2);
        signals.SkillWasLoaded.ShouldBeTrue(); // skill_view counts as a load
    }

    // --- Restricted toolset ---

    [Fact]
    public void AllowedTools_ContainsOnlySkillManageAndInspectionTools()
    {
        var allowed = SkillReviewCronAction.AllowedTools;

        allowed.ShouldContain("skill_manage");
        // Read/inspection tools are allowed.
        allowed.ShouldContain("read");
        allowed.ShouldContain("skills");
        // No arbitrary mutation / execution tools outside the skill surface.
        allowed.ShouldNotContain("exec");
        allowed.ShouldNotContain("shell");
        allowed.ShouldNotContain("write");
        allowed.ShouldNotContain("edit");
    }

    // --- Reviewer prompt: preference order ---

    [Fact]
    public void BuildReviewPrompt_EncodesPreferenceOrder()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("farnsworth"), TriggeringSignals(), lookbackHours: 24);

        prompt.ShouldContain("Patch a currently loaded skill");
        prompt.ShouldContain("Patch an existing umbrella skill");
        prompt.ShouldContain("supporting file");
        prompt.ShouldContain("Create a new");
        prompt.ShouldContain("skill_manage");
        prompt.ShouldContain("farnsworth");
    }

    [Fact]
    public void BuildReviewPrompt_IsPeriodicNotPerTurn()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("farnsworth"), TriggeringSignals(), lookbackHours: 12);

        prompt.ShouldContain("Periodic Skill Review");
        prompt.ShouldContain("12h");
    }

    // --- Reviewer prompt: avoid-list behaviour ---

    [Fact]
    public void BuildReviewPrompt_EncodesAvoidList()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("nova"), TriggeringSignals(), lookbackHours: 24);

        // one-off task narratives
        prompt.ShouldContain("one-off", Case.Insensitive);
        // PR/issue numbers as skill names
        prompt.ShouldContain("PR", Case.Insensitive);
        prompt.ShouldContain("issue number", Case.Insensitive);
        // transient environment/setup failures as durable constraints
        prompt.ShouldContain("transient", Case.Insensitive);
        // negative "tool X is broken" claims
        prompt.ShouldContain("broken", Case.Insensitive);
    }

    [Fact]
    public void BuildReviewPrompt_RestrictsToolsetInInstructions()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("agent"), TriggeringSignals(), lookbackHours: 24);

        prompt.ShouldContain("skill_manage");
        // Explicitly tells the reviewer it may not run arbitrary exec/write.
        prompt.ShouldContain("restricted", Case.Insensitive);
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        new SkillReviewCronAction().ActionType.ShouldBe("skill-review");
    }
}
