using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

/// <summary>
/// Phase 3b (#534). Behaviour tests for the canonical "history → LLM-visible entries"
/// projection. The two predicates are deliberately distinct — see the
/// <see cref="SessionContextProjector"/> XML docs for the orphaned-tool-result rationale.
/// </summary>
public sealed class SessionContextProjectorTests
{
    private static SessionEntry User(string content = "u") => new()
    {
        Role = MessageRole.User,
        Content = content,
    };

    private static SessionEntry Assistant(string content = "a") => new()
    {
        Role = MessageRole.Assistant,
        Content = content,
    };

    private static SessionEntry Tool(string content = "t") => new()
    {
        Role = MessageRole.Tool,
        Content = content,
        ToolName = "test_tool",
        ToolCallId = "call_1",
    };

    private static SessionEntry RawSystem(string content = "s") => new()
    {
        Role = MessageRole.System,
        Content = content,
    };

    private static SessionEntry Summary(string content = "summary") => new()
    {
        Role = MessageRole.System,
        Content = content,
        IsCompactionSummary = true,
    };

    [Fact]
    public void IsVisibleOnResume_ExcludesIsHistory()
    {
        var entry = User() with { IsHistory = true };

        SessionContextProjector.IsVisibleOnResume(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsVisibleOnResume_ExcludesCrashSentinel()
    {
        var entry = Assistant() with { IsCrashSentinel = true };

        SessionContextProjector.IsVisibleOnResume(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsVisibleOnResume_IncludesUser()
    {
        SessionContextProjector.IsVisibleOnResume(User()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleOnResume_IncludesAssistant()
    {
        SessionContextProjector.IsVisibleOnResume(Assistant()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleOnResume_IncludesCompactionSummary_AsSystem()
    {
        SessionContextProjector.IsVisibleOnResume(Summary()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleOnResume_ExcludesNonSummarySystem()
    {
        SessionContextProjector.IsVisibleOnResume(RawSystem()).ShouldBeFalse();
    }

    [Fact]
    public void IsVisibleOnResume_ExcludesToolRole_DocumentingOrphanCase()
    {
        // Tool entries are dropped on cold-start resume because the Assistant
        // SessionEntry only persists response text and not the tool_use blocks
        // that would pair with this tool_result. Including them would cause an
        // orphaned tool_result rejection by Anthropic.
        SessionContextProjector.IsVisibleOnResume(Tool()).ShouldBeFalse();
    }

    [Fact]
    public void IsVisibleOnResume_HistoryWins_OverEverythingElse()
    {
        var entry = Summary() with { IsHistory = true };

        SessionContextProjector.IsVisibleOnResume(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsVisibleInLiveContext_IncludesUser()
    {
        SessionContextProjector.IsVisibleInLiveContext(User()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleInLiveContext_IncludesAssistant()
    {
        SessionContextProjector.IsVisibleInLiveContext(Assistant()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleInLiveContext_IncludesToolRole()
    {
        // Live context counts Tool entries — they ARE sent in a continuous run
        // because the agent's in-memory message list has them paired with the
        // Assistant tool_use that produced them.
        SessionContextProjector.IsVisibleInLiveContext(Tool()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleInLiveContext_IncludesNonSummarySystem()
    {
        // Non-summary System entries count too — the compactor's budget needs to
        // reflect everything the LLM will see, including any in-conversation
        // system messages the agent injected.
        SessionContextProjector.IsVisibleInLiveContext(RawSystem()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleInLiveContext_IncludesCompactionSummary()
    {
        SessionContextProjector.IsVisibleInLiveContext(Summary()).ShouldBeTrue();
    }

    [Fact]
    public void IsVisibleInLiveContext_ExcludesIsHistory()
    {
        var entry = Assistant() with { IsHistory = true };

        SessionContextProjector.IsVisibleInLiveContext(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsVisibleInLiveContext_ExcludesCrashSentinel()
    {
        var entry = User() with { IsCrashSentinel = true };

        SessionContextProjector.IsVisibleInLiveContext(entry).ShouldBeFalse();
    }

    [Fact]
    public void ProjectForResume_PreservesOrder()
    {
        var history = new[]
        {
            User("u1"),
            Assistant("a1"),
            Tool("t1"),
            User("u2"),
            Assistant("a2"),
        };

        var projected = SessionContextProjector.ProjectForResume(history);

        projected.Select(e => e.Content).ShouldBe(["u1", "a1", "u2", "a2"]);
    }

    [Fact]
    public void ProjectForResume_MultiCycleHistory_KeepsOnlyActiveSummary()
    {
        // Post-Phase-3a invariant: older summaries are already IsHistory=true.
        // The projector trusts the flag — it does NOT discover "latest" itself.
        // That's SessionCompaction.ApplyLegacyHistoryProjection's job.
        var history = new[]
        {
            User("u1") with { IsHistory = true },
            Assistant("a1") with { IsHistory = true },
            Summary("s1") with { IsHistory = true },
            User("u2") with { IsHistory = true },
            Assistant("a2") with { IsHistory = true },
            Summary("s2"),
            User("u3"),
            Assistant("a3"),
        };

        var projected = SessionContextProjector.ProjectForResume(history);

        projected.Select(e => e.Content).ShouldBe(["s2", "u3", "a3"]);
    }

    [Fact]
    public void ProjectForResume_IsMaterialised_NotLazy()
    {
        var history = new List<SessionEntry> { User("u1"), Assistant("a1") };

        var projected = SessionContextProjector.ProjectForResume(history);

        history.Clear();
        projected.Count.ShouldBe(2);
    }

    [Fact]
    public void ProjectForLiveContext_PreservesOrder_AndIncludesTool()
    {
        var history = new[]
        {
            User("u1"),
            Assistant("a1"),
            Tool("t1"),
            Assistant("a2"),
            User("u2"),
        };

        var projected = SessionContextProjector.ProjectForLiveContext(history);

        projected.Select(e => e.Content).ShouldBe(["u1", "a1", "t1", "a2", "u2"]);
    }

    [Fact]
    public void ProjectForLiveContext_IsMaterialised_NotLazy()
    {
        var history = new List<SessionEntry> { User("u1"), Tool("t1") };

        var projected = SessionContextProjector.ProjectForLiveContext(history);

        history.Clear();
        projected.Count.ShouldBe(2);
    }

    [Fact]
    public void ProjectForResume_ThrowsOnNull()
    {
        Should.Throw<ArgumentNullException>(() => SessionContextProjector.ProjectForResume(null!));
    }

    [Fact]
    public void ProjectForLiveContext_ThrowsOnNull()
    {
        Should.Throw<ArgumentNullException>(() => SessionContextProjector.ProjectForLiveContext(null!));
    }
}
