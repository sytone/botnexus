using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2896: auto-compaction must budget against the context window resolved for the session's agent
/// and conversation, not the process-global <see cref="CompactionOptions.ContextWindowTokens"/>.
/// </summary>
public sealed class ScopedCompactionWindowTests
{
    // ---------------------------------------------------------------------------------------------
    // AC1-AC3: precedence - conversation override > agent descriptor > model window > global option.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_ConversationOverrideSet_WinsOverAgentAndModel()
    {
        ScopedCompactionWindow.Resolve(conversationOverride: 16_000, agentWindow: 32_000, modelWindow: 128_000)
            .ShouldBe(16_000, "AC1: the conversation-level override is the most specific layer.");
    }

    [Fact]
    public void Resolve_NoConversationOverride_UsesAgentDescriptorWindow()
    {
        ScopedCompactionWindow.Resolve(conversationOverride: null, agentWindow: 32_000, modelWindow: 128_000)
            .ShouldBe(32_000, "AC2: absent a conversation override the agent descriptor wins.");
    }

    [Fact]
    public void Resolve_NoConversationOrAgentWindow_UsesModelWindow()
    {
        ScopedCompactionWindow.Resolve(conversationOverride: null, agentWindow: null, modelWindow: 128_000)
            .ShouldBe(128_000, "AC3: absent both, the resolved model's own window is used.");
    }

    [Fact]
    public void Resolve_NoLayerSuppliesWindow_ReturnsNullSoTheGlobalOptionStands()
    {
        ScopedCompactionWindow.Resolve(conversationOverride: null, agentWindow: null, modelWindow: null)
            .ShouldBeNull("AC3: only then does CompactionOptions.ContextWindowTokens apply.");
    }

    // Sad paths: a non-positive value at any layer is not a usable window and must fall through
    // rather than produce a zero/negative threshold that would make ShouldCompact always fire.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_NonPositiveConversationOverride_FallsThroughToAgent(int bogusOverride)
    {
        ScopedCompactionWindow.Resolve(conversationOverride: bogusOverride, agentWindow: 32_000, modelWindow: 128_000)
            .ShouldBe(32_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Resolve_NonPositiveAgentWindow_FallsThroughToModel(int bogusAgentWindow)
    {
        ScopedCompactionWindow.Resolve(conversationOverride: null, agentWindow: bogusAgentWindow, modelWindow: 128_000)
            .ShouldBe(128_000);
    }

    [Fact]
    public void Resolve_AllLayersNonPositive_ReturnsNull()
    {
        ScopedCompactionWindow.Resolve(conversationOverride: 0, agentWindow: -1, modelWindow: 0)
            .ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // AC4: only the base window changes. Ratio semantics and the byte-based bloat trigger untouched.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_ScopedWindow_NarrowsOnlyTheContextWindow()
    {
        var options = new CompactionOptions
        {
            ContextWindowTokens = 200_000,
            TokenThresholdRatio = 0.6,
            LargestEntryBytesThreshold = 65_536,
            PreservedTurns = 3,
            MaxSummaryChars = 16_000
        };

        var scoped = ScopedCompactionWindow.Apply(options, 32_000);

        scoped.ContextWindowTokens.ShouldBe(32_000);
        scoped.TokenThresholdRatio.ShouldBe(0.6, "AC4: TokenThresholdRatio semantics are unchanged.");
        scoped.LargestEntryBytesThreshold.ShouldBe(65_536, "AC4: the bloat trigger is untouched.");
        scoped.PreservedTurns.ShouldBe(3);
        scoped.MaxSummaryChars.ShouldBe(16_000);
    }

    [Fact]
    public void Apply_NoScopedWindow_ReturnsOptionsUnchanged()
    {
        var options = new CompactionOptions { ContextWindowTokens = 200_000, TokenThresholdRatio = 0.6 };

        ScopedCompactionWindow.Apply(options, null).ShouldBeSameAs(options);
        ScopedCompactionWindow.Apply(options, 0).ShouldBeSameAs(options);
        ScopedCompactionWindow.Apply(options, -7).ShouldBeSameAs(options);
    }

    // ---------------------------------------------------------------------------------------------
    // AC5 (headline): global 200k, agent 32k -> compaction triggers against the 32k budget.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ShouldCompact_GlobalWindow200k_AgentWindow32k_TriggersAgainstTheAgentWindow()
    {
        // 25k visible tokens: above 32k * 0.6 = 19.2k, but far below 200k * 0.6 = 120k.
        var session = CreateSession(25_000 * 4);
        var compactor = CreateCompactor();
        var globalOptions = new CompactionOptions
        {
            ContextWindowTokens = 200_000,
            TokenThresholdRatio = 0.6,
            LargestEntryBytesThreshold = 0 // isolate the token trigger from the bloat trigger
        };

        compactor.ShouldCompact(session, globalOptions).ShouldBeFalse(
            "pre-condition: against the 200k global window this session is nowhere near the threshold - " +
            "this is exactly the overflow-before-compaction defect #2896 describes.");

        var scoped = ScopedCompactionWindow.Apply(globalOptions, ScopedCompactionWindow.Resolve(null, 32_000, 200_000));

        compactor.ShouldCompact(session, scoped).ShouldBeTrue(
            "AC5: with the agent's 32k window the same session is over the 19.2k threshold and must compact.");
    }

    [Fact]
    public void ShouldCompact_ConversationOverrideSmallerThanAgent_TriggersAgainstTheOverride()
    {
        // 5k visible tokens: above 8k * 0.6 = 4.8k, below 32k * 0.6 = 19.2k.
        var session = CreateSession(5_000 * 4);
        var compactor = CreateCompactor();
        var options = new CompactionOptions
        {
            ContextWindowTokens = 200_000,
            TokenThresholdRatio = 0.6,
            LargestEntryBytesThreshold = 0
        };

        var agentScoped = ScopedCompactionWindow.Apply(options, ScopedCompactionWindow.Resolve(null, 32_000, 200_000));
        compactor.ShouldCompact(session, agentScoped).ShouldBeFalse(
            "pre-condition: the agent's own 32k window leaves this session under threshold.");

        var conversationScoped = ScopedCompactionWindow.Apply(options, ScopedCompactionWindow.Resolve(8_000, 32_000, 200_000));
        compactor.ShouldCompact(session, conversationScoped).ShouldBeTrue(
            "AC1: the conversation override must be what the threshold is computed from.");
    }

    // ---------------------------------------------------------------------------------------------
    // AC6: byte-identical behaviour for an agent with no scoped window. Explicit, not assumed.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 0.5, 100, false)]   // below threshold
    [InlineData(100, 0.5, 300, true)]    // above threshold
    [InlineData(1_000, 0.6, 2_404, true)]   // 601 tokens > 600 threshold
    [InlineData(1_000, 0.6, 2_400, false)]  // 600 tokens == 600 threshold: strictly-greater, so no trigger
    [InlineData(1_000, 0.6, 2_000, false)]
    public void ShouldCompact_NoScopedWindow_IsIdenticalToThePreChangeDecision(
        int contextWindowTokens,
        double ratio,
        int contentChars,
        bool expected)
    {
        var session = CreateSession(contentChars);
        var compactor = CreateCompactor();
        var options = new CompactionOptions
        {
            ContextWindowTokens = contextWindowTokens,
            TokenThresholdRatio = ratio,
            LargestEntryBytesThreshold = 0
        };

        // The pre-change decision: the raw configured options.
        var baseline = compactor.ShouldCompact(session, options);

        // The post-change decision when no layer supplies a scoped window.
        var resolved = ScopedCompactionWindow.Resolve(conversationOverride: null, agentWindow: null, modelWindow: null);
        var scoped = ScopedCompactionWindow.Apply(options, resolved);
        var actual = compactor.ShouldCompact(session, scoped);

        baseline.ShouldBe(expected);
        actual.ShouldBe(baseline, "AC6: with no scoped window the decision must be byte-identical to today.");
    }

    [Fact]
    public void ShouldCompact_NoScopedWindow_BloatTriggerStillFires()
    {
        // AC4/AC6 interaction: the byte-based trigger is orthogonal and must survive untouched.
        var session = CreateSession(200); // ~50 tokens, far below any token threshold
        var compactor = CreateCompactor();
        var options = new CompactionOptions
        {
            ContextWindowTokens = 200_000,
            TokenThresholdRatio = 0.6,
            LargestEntryBytesThreshold = 100
        };

        var scoped = ScopedCompactionWindow.Apply(options, ScopedCompactionWindow.Resolve(null, null, null));

        scoped.LargestEntryBytesThreshold.ShouldBe(100);
        compactor.ShouldCompact(session, scoped).ShouldBeTrue(
            "the 200-byte entry exceeds the 100-byte bloat threshold regardless of the token window.");
    }

    // ---------------------------------------------------------------------------------------------
    // Resolver: the store-reading implementation honours the same precedence, and fails soft.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolver_ConversationOverride_BeatsAgentDescriptorAndModel()
    {
        var resolver = CreateResolver(conversationOverride: 16_000, agentWindow: 32_000, modelWindow: 128_000);

        (await resolver.ResolveAsync(AgentId.From("a"), ConversationId.From("c"))).ShouldBe(16_000);
    }

    [Fact]
    public async Task Resolver_NoConversationOverride_UsesAgentDescriptor()
    {
        var resolver = CreateResolver(conversationOverride: null, agentWindow: 32_000, modelWindow: 128_000);

        (await resolver.ResolveAsync(AgentId.From("a"), ConversationId.From("c"))).ShouldBe(32_000);
    }

    [Fact]
    public async Task Resolver_NoConversationOrAgentWindow_UsesModelWindow()
    {
        var resolver = CreateResolver(conversationOverride: null, agentWindow: null, modelWindow: 128_000);

        (await resolver.ResolveAsync(AgentId.From("a"), ConversationId.From("c"))).ShouldBe(128_000);
    }

    [Fact]
    public async Task Resolver_UnknownAgent_ReturnsNull()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var resolver = new SessionContextWindowResolver(
            NullLogger<SessionContextWindowResolver>.Instance,
            registry.Object);
        (await resolver.ResolveAsync(AgentId.From("ghost"), ConversationId.From("c"))).ShouldBeNull(
            "AC6: an unresolvable agent must degrade to the configured global window, not to zero.");
    }

    [Fact]
    public async Task Resolver_NoCollaborators_ReturnsNull()
    {
        var resolver = new SessionContextWindowResolver(NullLogger<SessionContextWindowResolver>.Instance);

        (await resolver.ResolveAsync(AgentId.From("a"), ConversationId.From("c"))).ShouldBeNull();
    }

    [Fact]
    public async Task Resolver_ConversationStoreThrows_FallsBackToAgentWindow()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns(NewDescriptor(32_000));

        var conversations = new Mock<IConversationStore>();
        conversations.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store is down"));

        var resolver = new SessionContextWindowResolver(
            NullLogger<SessionContextWindowResolver>.Instance,
            registry.Object,
            conversations.Object);

        (await resolver.ResolveAsync(AgentId.From("a"), ConversationId.From("c"))).ShouldBe(
            32_000,
            "a store failure must degrade the decision, never abort the turn.");
    }

    // ---------------------------------------------------------------------------------------------

    private static SessionContextWindowResolver CreateResolver(int? conversationOverride, int? agentWindow, int? modelWindow)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(NewDescriptor(agentWindow));

        var conversations = new Mock<IConversationStore>();
        conversations.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation
            {
                ConversationId = ConversationId.From("c"),
                AgentId = AgentId.From("a"),
                ContextWindowOverride = conversationOverride
            });

        LlmClient? client = null;
        if (modelWindow.HasValue)
        {
            var models = new ModelRegistry();
            var model = new LlmModel(
                Id: "test-model",
                Name: "Test Model",
                Api: "test-api",
                Provider: "test-provider",
                BaseUrl: "https://example.com",
                Reasoning: false,
                Input: ["text"],
                Cost: new ModelCost(0, 0, 0, 0),
                ContextWindow: modelWindow.Value,
                MaxTokens: 4096);
            models.Register(model.Provider, model);
            client = new LlmClient(new ApiProviderRegistry(), models);
        }

        return new SessionContextWindowResolver(
            NullLogger<SessionContextWindowResolver>.Instance,
            registry.Object,
            conversations.Object,
            client);
    }

    private static AgentDescriptor NewDescriptor(int? contextWindow) => new()
    {
        AgentId = AgentId.From("a"),
        DisplayName = "Test Agent",
        ApiProvider = "test-provider",
        ModelId = "test-model",
        ContextWindow = contextWindow
    };

    private static LlmSessionCompactor CreateCompactor()
        => new(new LlmClient(new ApiProviderRegistry(), new ModelRegistry()), NullLogger<LlmSessionCompactor>.Instance);

    private static Session CreateSession(int contentChars)
        => new()
        {
            SessionId = SessionId.From("s1"),
            ConversationId = ConversationId.From("c1"),
            History =
            [
                new SessionEntry
                {
                    Role = MessageRole.User,
                    Content = new string('a', contentChars),
                    Timestamp = DateTimeOffset.UtcNow
                }
            ]
        };
}
