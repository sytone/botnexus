using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3417: every LLM request originating from a session - foreground OR background - must carry that
/// session's identity on <see cref="SimpleStreamOptions.SessionId"/>.
///
/// <para>
/// Both background callers previously built their options from scratch and never set the field, so
/// they were the only requests in the system with no session correlation, and the Copilot Responses
/// <c>prompt_cache_key</c> branch (gated on a non-blank <c>SessionId</c>) was dead for them.
/// </para>
///
/// <para>
/// These assertions deliberately pin the ACTUAL id observed on the options reaching the LLM client.
/// Asserting non-null would pass against any id - including a wrong one, or a constant - and would
/// not distinguish "the session's identity was threaded" from "something was threaded", which is
/// precisely the property under test.
/// </para>
/// </summary>
public sealed class BackgroundLlmSessionIdTests
{
    private static readonly LlmModel CloudModel = new(
        Id: "claude-haiku-4.5", Name: "Haiku", Api: "fake-api", Provider: "anthropic",
        BaseUrl: "https://api.anthropic.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 128000, MaxTokens: 4096);

    private static readonly AgentId TestAgentId = Domain.Primitives.AgentId.From("agent-a");
    private static readonly ConversationId TestConvId = Domain.Primitives.ConversationId.From("conv-1");

    // ── AC1: compaction ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_ThreadsCompactedSessionId_OntoOptionsReachingLlmClient()
    {
        const string SessionIdValue = "sess-compaction-3417-abc";

        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(opts => captured = opts);

        var session = CreateLargeSession(SessionIdValue, 300);
        var result = await compactor.CompactAsync(session, CompactionOptionsFor(CloudModel));

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        // Pins the exact id of the session being compacted, not merely "some id".
        captured!.SessionId.ShouldBe(SessionIdValue);
    }

    [Fact]
    public async Task CompactAsync_DistinctSessions_EachCarryTheirOwnId()
    {
        // Guards against a constant/first-wins bug that a single-session assertion cannot see.
        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(opts => captured = opts);

        await compactor.CompactAsync(CreateLargeSession("sess-alpha", 300), CompactionOptionsFor(CloudModel));
        captured!.SessionId.ShouldBe("sess-alpha");

        await compactor.CompactAsync(CreateLargeSession("sess-beta", 300), CompactionOptionsFor(CloudModel));
        captured!.SessionId.ShouldBe("sess-beta");
    }

    [Fact]
    public async Task CompactAsync_WithAuthManager_KeepsBothApiKeyAndSessionId()
    {
        // The seam applies BOTH fields; threading the session must not drop the credential, which
        // is what the #2025 seam exists to guarantee.
        const string SessionIdValue = "sess-both-fields";

        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(
            opts => captured = opts,
            CreateAuthManagerWithToken("anthropic", "resolved-oauth-token"));

        var model = CloudModel;
        var result = await compactor.CompactAsync(
            CreateLargeSession(SessionIdValue, 300), CompactionOptionsFor(model));

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.SessionId.ShouldBe(SessionIdValue);
        captured.ApiKey.ShouldBe("resolved-oauth-token");
    }

    // ── AC2: auto-title ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAndSaveAsync_ThreadsOriginatingSessionId_OntoOptionsReachingLlmClient()
    {
        const string SessionIdValue = "sess-autotitle-3417-xyz";

        SimpleStreamOptions? captured = null;
        var svc = CreateAutoTitleService(opts => captured = opts, out var store);

        var result = await svc.GenerateAndSaveAsync(
            TestConvId, TestAgentId, "What do cats eat?", "Cats eat...", null, 30,
            CancellationToken.None, Domain.Primitives.SessionId.From(SessionIdValue));

        result.ShouldBe("Chat About Cats");
        captured.ShouldNotBeNull();
        captured!.SessionId.ShouldBe(SessionIdValue);
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateProvisionalAndSaveAsync_ThreadsOriginatingSessionId()
    {
        const string SessionIdValue = "sess-provisional-3417";

        SimpleStreamOptions? captured = null;
        var svc = CreateAutoTitleService(opts => captured = opts, out _);

        var result = await svc.GenerateProvisionalAndSaveAsync(
            TestConvId, TestAgentId, "What do cats eat?", null, 30,
            CancellationToken.None, Domain.Primitives.SessionId.From(SessionIdValue));

        result.ShouldBe("Chat About Cats");
        captured.ShouldNotBeNull();
        captured!.SessionId.ShouldBe(SessionIdValue);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_WithAuthManager_KeepsBothApiKeyAndSessionId()
    {
        const string SessionIdValue = "sess-autotitle-both";

        SimpleStreamOptions? captured = null;
        var svc = CreateAutoTitleService(
            opts => captured = opts, out _, CreateAuthManagerWithToken("fake", "titling-token"));

        var result = await svc.GenerateAndSaveAsync(
            TestConvId, TestAgentId, "q", "a", null, 30, CancellationToken.None,
            Domain.Primitives.SessionId.From(SessionIdValue));

        result.ShouldBe("Chat About Cats");
        captured!.SessionId.ShouldBe(SessionIdValue);
        captured.ApiKey.ShouldBe("titling-token");
    }

    // ── AC5: no behaviour change when the session id is unavailable ───────────────

    [Fact]
    public async Task GenerateAndSaveAsync_NoSessionId_NoAuthManager_PreservesNullOptions()
    {
        // Behaviour-preserving: nothing to say about credentials OR session -> null options, exactly
        // as before. A blank id must never manufacture an options object carrying an empty SessionId.
        SimpleStreamOptions? captured = null;
        var observed = false;
        var svc = CreateAutoTitleService(opts => { captured = opts; observed = true; }, out _);

        var result = await svc.GenerateAndSaveAsync(
            TestConvId, TestAgentId, "q", "a", null, 30, CancellationToken.None, sessionId: null);

        result.ShouldBe("Chat About Cats");
        observed.ShouldBeTrue("the provider must still have been called");
        captured.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateAndSaveAsync_NoSessionId_WithAuthManager_LeavesSessionIdNull()
    {
        // AC5: with no session identity there must be no SessionId on the options, so the Copilot
        // builder emits NO prompt_cache_key at all - not an empty one. The credential still flows.
        SimpleStreamOptions? captured = null;
        var svc = CreateAutoTitleService(
            opts => captured = opts, out _, CreateAuthManagerWithToken("fake", "titling-token"));

        await svc.GenerateAndSaveAsync(
            TestConvId, TestAgentId, "q", "a", null, 30, CancellationToken.None, sessionId: null);

        captured.ShouldNotBeNull();
        captured!.SessionId.ShouldBeNull();
        captured.ApiKey.ShouldBe("titling-token");
    }

    // ── The seam itself ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAuthenticatedOptionsAsync_AppliesSessionId_AndPreservesBaseOptions()
    {
        var auth = CreateAuthManagerWithToken("github-copilot", "copilot-token");

        var options = await auth.CreateAuthenticatedOptionsAsync(
            "github-copilot",
            new SimpleStreamOptions { StreamSetupTimeoutMs = 15000 },
            Domain.Primitives.SessionId.From("sess-seam-1"),
            CancellationToken.None);

        options.SessionId.ShouldBe("sess-seam-1");
        options.ApiKey.ShouldBe("copilot-token");
        options.StreamSetupTimeoutMs.ShouldBe(15000);
    }

    [Fact]
    public async Task CreateAuthenticatedOptionsAsync_NullSessionId_DoesNotOverwriteBaseOptionsValue()
    {
        // A null argument must be inert, not destructive: a caller that already set SessionId on
        // its baseOptions keeps it. Typing the parameter as SessionId? (#3099) means a BLANK id is
        // not constructible at this seam at all - the blank/absent distinction is pinned one layer
        // down, on the request builder itself, in CopilotResponsesPromptCacheKeyTests.
        var auth = CreateAuthManagerWithToken("github-copilot", "copilot-token");

        var options = await auth.CreateAuthenticatedOptionsAsync(
            "github-copilot",
            new SimpleStreamOptions { SessionId = "already-set" },
            sessionId: null,
            CancellationToken.None);

        options.SessionId.ShouldBe("already-set");
    }

    [Fact]
    public async Task CreateAuthenticatedOptionsAsync_NoSessionAnywhere_LeavesSessionIdNull()
    {
        var auth = CreateAuthManagerWithToken("github-copilot", "copilot-token");

        var options = await auth.CreateAuthenticatedOptionsAsync(
            "github-copilot", baseOptions: null, sessionId: null, CancellationToken.None);

        options.SessionId.ShouldBeNull();
        options.ApiKey.ShouldBe("copilot-token");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static CompactionOptions CompactionOptionsFor(LlmModel model) => new()
    {
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 2,
        MaxSummaryChars = 5000,
        SummarizationModel = model.Id,
        SummarizationProvider = model.Provider
    };

    private static LlmSessionCompactor CreateCompactor(
        Action<SimpleStreamOptions?> captureOptions,
        GatewayAuthManager? authManager = null)
    {
        var llmClient = CreateCapturingLlmClient(CloudModel, "compacted ok", captureOptions);
        return authManager is null
            ? new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance)
            : new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance, authManager: authManager);
    }

    private static ConversationAutoTitleService CreateAutoTitleService(
        Action<SimpleStreamOptions?> captureOptions,
        out Mock<IConversationStore> store,
        GatewayAuthManager? authManager = null)
    {
        var fakeModel = new LlmModel(
            Id: "fake-model", Name: "fake-model", Api: "fake-api", Provider: "fake",
            BaseUrl: "https://fake.example.com", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 4096, MaxTokens: 512);

        var llmClient = CreateCapturingLlmClient(fakeModel, "Chat About Cats", captureOptions);

        var conv = new Conversation
        {
            ConversationId = TestConvId,
            AgentId = TestAgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(TestConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new ConversationAutoTitleService(
            store.Object, llmClient, NullLogger.Instance, notifier: null, authManager: authManager);
    }

    private static LlmClient CreateCapturingLlmClient(
        LlmModel registeredModel, string responseText, Action<SimpleStreamOptions?> captureOptions)
    {
        var models = new ModelRegistry();
        models.Register(registeredModel.Provider, registeredModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(p => p.Api).Returns(registeredModel.Api);
        provider.Setup(p => p.StreamSimple(
                It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
            .Returns((LlmModel _, Context _, SimpleStreamOptions? o) =>
            {
                captureOptions(o);
                return SuccessStream(responseText);
            });

        var providers = new ApiProviderRegistry();
        providers.Register(provider.Object);
        return new LlmClient(providers, models);
    }

    private static LlmStream SuccessStream(string text)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(text)],
            Api: "any", Provider: "any", ModelId: "any",
            Usage: Usage.Empty(), StopReason: StopReason.Stop,
            ErrorMessage: null, ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    // A real GatewayAuthManager over an in-memory auth.json, so the credential-resolution seam is
    // exercised for real rather than mocked.
    private static GatewayAuthManager CreateAuthManagerWithToken(string provider, string accessToken)
    {
        var fileSystem = new MockFileSystem();
        var configDir = PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem);
        fileSystem.Directory.CreateDirectory(configDir);
        fileSystem.File.WriteAllText(
            Path.Combine(configDir, "auth.json"),
            $$"""
            {
              "{{provider}}": {
                "type": "apikey",
                "refresh": "unused",
                "access": "{{accessToken}}",
                "expires": 4102444800000,
                "endpoint": "https://api.example.com"
              }
            }
            """);

        var monitor = new BackgroundLlmStaticOptionsMonitor<PlatformConfig>(new PlatformConfig());
        return new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
    }

    private static GatewaySession CreateLargeSession(string sessionId, int entryCount)
    {
        var session = new GatewaySession
        {
            SessionId = Domain.Primitives.SessionId.From(sessionId),
            AgentId = TestAgentId
        };
        var entries = new List<SessionEntry>();
        for (var i = 0; i < entryCount; i++)
        {
            entries.Add(new SessionEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i} " + new string('x', 50)
            });
        }
        session.AddEntries(entries);
        return session;
    }
}

// File-scoped: the sibling test files in this assembly each declare their own `file sealed`
// StaticOptionsMonitor, so a distinct name avoids colliding with theirs at assembly scope.
file sealed class BackgroundLlmStaticOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
