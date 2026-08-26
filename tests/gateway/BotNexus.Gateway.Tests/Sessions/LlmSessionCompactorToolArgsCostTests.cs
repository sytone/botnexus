using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3536: the compaction token estimate and the #1599 bloat trigger must size a visible entry by
/// everything sent to the provider - <c>Content</c> + <c>ToolArgs</c> + <c>ThinkingContent</c> - not
/// by <c>Content</c> alone.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the motivating session: <c>tool_args</c> was 69% of the visible context (3,544,719 of
/// 5,160,285 characters). The estimator reported ~403,891 tokens where ~1,290,071 were present, a
/// 3.19x undercount. A <c>tool-start</c> row of 27,354 characters of arguments and no content was
/// costed at zero by the bloat trigger.
/// </para>
/// <para>
/// Non-vacuity: every test here uses entries whose <c>Content</c> is empty or trivial and whose mass
/// is entirely in <c>ToolArgs</c>. Under the pre-#3536 implementation each measured value is zero (or
/// far below threshold) and every assertion fails.
/// </para>
/// </remarks>
public sealed class LlmSessionCompactorToolArgsCostTests
{
    private static readonly AgentId TestAgent = AgentId.From("test-agent");

    private static readonly LlmModel TestModel = new(
        Id: "test-model",
        Name: "Test Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    [Fact]
    public void GetLiveContextCharCost_SumsContentToolArgsAndThinking()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = new string('c', 10),
            ToolArgs = new string('a', 100),
            ThinkingContent = new string('t', 1000)
        };

        SessionContextProjector.GetLiveContextCharCost(entry).ShouldBe(1110);
    }

    [Fact]
    public void GetLiveContextCharCost_IsNullSafeOnEveryField()
    {
        var entry = new SessionEntry { Role = MessageRole.Tool, Content = string.Empty };

        SessionContextProjector.GetLiveContextCharCost(entry).ShouldBe(0);
    }

    [Fact]
    public void GetLiveContextCharCost_CountsAnArgsOnlyRow_WhichContentSizingScoredAsZero()
    {
        // The exact shape the old bloat trigger skipped with `continue`.
        var entry = new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = string.Empty,
            ToolArgs = new string('a', 27_354)
        };

        SessionContextProjector.GetLiveContextCharCost(entry).ShouldBe(27_354);
    }

    [Fact]
    public void ShouldCompact_ToolArgsHeavySession_TriggersOnTokenCount()
    {
        // 40 rows x 20,000 chars of arguments = 800,000 chars => 200,000 estimated tokens,
        // against a 120,000 threshold. Content is empty throughout, so the pre-#3536 estimator
        // scores this session at 0 tokens and does not trigger.
        var session = CreateSession(rows: 40, argsChars: 20_000);

        var compactor = CreateCompactor();

        compactor.ShouldCompact(session.Session, TokenOnlyOptions()).ShouldBeTrue();
    }

    [Fact]
    public void ShouldCompact_ToolArgsHeavySession_TriggersOnBloatBytes()
    {
        // A single oversized args-only row, well under the token threshold in total but above the
        // 65,536-byte per-entry bloat threshold. Pre-#3536 this row was skipped outright.
        var session = CreateSession(rows: 1, argsChars: 70_000);

        var compactor = CreateCompactor();

        var options = TokenOnlyOptions() with
        {
            // Token trigger cannot fire here (70,000 chars => 17,500 tokens < 120,000), so a pass
            // proves the BYTE signal fired on ToolArgs specifically.
            LargestEntryBytesThreshold = 65_536
        };

        compactor.ShouldCompact(session.Session, options).ShouldBeTrue();
    }

    [Fact]
    public void ShouldCompact_SmallToolArgsSession_StillDoesNotTrigger()
    {
        // Guards against the fix degenerating into "always compact": counting more fields must not
        // make a genuinely small session eligible.
        var session = CreateSession(rows: 3, argsChars: 100);

        var compactor = CreateCompactor();

        compactor.ShouldCompact(session.Session, TokenOnlyOptions()).ShouldBeFalse();
    }

    [Fact]
    public void ShouldCompact_ThinkingContentIsAlsoCharged()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = TestAgent
        };

        session.AddEntries(Enumerable.Range(0, 40).Select(_ => new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ThinkingContent = new string('t', 20_000)
        }).ToArray());

        CreateCompactor().ShouldCompact(session.Session, TokenOnlyOptions()).ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Window 200000 * ratio 0.6 => 120000 threshold, byte trigger disabled.</summary>
    private static CompactionOptions TokenOnlyOptions() => new()
    {
        PreservedTurns = 3,
        ContextWindowTokens = 200_000,
        TokenThresholdRatio = 0.6,
        LargestEntryBytesThreshold = 0,
        SummarizationModel = TestModel.Id
    };

    private static GatewaySession CreateSession(int rows, int argsChars)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = TestAgent
        };

        session.AddEntries(Enumerable.Range(0, rows).Select(_ => new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = string.Empty,
            ToolArgs = new string('a', argsChars)
        }).ToArray());

        return session;
    }

    private static LlmSessionCompactor CreateCompactor()
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);
        return new LlmSessionCompactor(
            new LlmClient(providers, models),
            new LoggerFactory().CreateLogger<LlmSessionCompactor>());
    }
}
