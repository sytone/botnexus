using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Commands;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Commands;

public sealed class ContextCompactionDiagnosticsCommandTests
{
    [Fact]
    public async Task Context_SuccessiveReads_UsesFreshAuthoritativePolicyAndMeasurements()
    {
        var fixture = new Fixture();
        var contributor = fixture.Create();
        var first = await contributor.ExecuteAsync("/context", fixture.Context);
        first.IsError.ShouldBeFalse();
        first.Body.ShouldContain("Scoped working window: 40,000 tokens");
        first.Body.ShouldContain("Compaction ceiling: 12,000 tokens (source: conversation)");
        first.Body.ShouldContain("Ratio safety threshold: 24,000 tokens (configured ratio: 0.6)");
        first.Body.ShouldContain("Effective token trigger threshold: 12,000 tokens");
        first.Body.ShouldContain("Persisted LLM-visible history estimate: 0 tokens");
        fixture.Options = new CompactionOptions { ContextWindowTokens = 80_000, TokenThresholdRatio = 0.5 };
        fixture.Window.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>()).Returns(20_000);
        fixture.Ceiling.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>())
            .Returns(new ScopedCompactionCeiling(15_000, "agent"));
        fixture.Session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "abcdefgh" });
        fixture.Session.Metadata["lastProviderPromptTokens"] = 20;
        fixture.Session.Metadata["arbitrary-secret"] = "SECRET METADATA";
        var second = await contributor.ExecuteAsync("/context", fixture.Context);
        second.Body.ShouldContain("Scoped working window: 20,000 tokens");
        second.Body.ShouldContain("Compaction ceiling: 15,000 tokens (source: agent)");
        second.Body.ShouldContain("Ratio safety threshold: 10,000 tokens (configured ratio: 0.5)");
        second.Body.ShouldContain("Effective token trigger threshold: 10,000 tokens");
        second.Body.ShouldNotContain("900,000");
        second.Body.ShouldContain("Persisted LLM-visible history estimate: 2 tokens");
        second.Body.ShouldContain("Last completed provider prompt: 20 tokens (source: lastProviderPromptTokens)");
        second.Body.ShouldContain("Provider / persisted estimate ratio: 10");
        second.Body.ShouldNotContain("SECRET METADATA");
        await fixture.Window.Received(2).ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Context_UnknownScope_LabelsPolicyFallbackNotModelCapacity()
    {
        var fixture = new Fixture();
        fixture.Window.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>()).Returns((int?)null);
        var result = await fixture.Create().ExecuteAsync("/context", fixture.Context);
        result.Body.ShouldContain("Scoped working window: unavailable");
        result.Body.ShouldContain("Compaction policy fallback window: 80,000 tokens (not known model capacity)");
        result.Body.ShouldNotContain("% of 80,000 token window");
        result.Body.ShouldNotContain("Remaining");
        result.Body.ShouldNotContain("%");
    }

    [Fact]
    public async Task Context_NoProviderMeasurement_ReportsUnavailableNotZero()
    {
        var fixture = new Fixture();
        var result = await fixture.Create().ExecuteAsync("/context", fixture.Context);
        result.Body.ShouldContain("Last completed provider prompt: unavailable (source: unavailable)");
        result.Body.ShouldContain("Provider / persisted estimate ratio: unavailable");
        result.Body.ShouldNotContain("Last completed provider prompt: 0");
    }

    [Fact]
    public async Task Context_ProviderMeasurement_ReportsCountSourceAndRatioWithoutRecalculation()
    {
        var fixture = new Fixture();
        var diagnostics = Substitute.For<ISessionCompactionDiagnostics>();
        diagnostics.ResolveAsync(Fixture.Agent, Fixture.SessionId, Arg.Any<CancellationToken>()).Returns(new CompactionPolicyDiagnostics
        {
            ScopedContextWindowTokens = 40_000, ContextWindowTokens = 40_000,
            TokenThresholdRatio = 0.6, RatioSafetyThresholdTokens = 24_000, EffectiveThresholdTokens = 24_000,
            LocalEstimatedTokens = 1_000, ProviderPromptTokens = 3_000,
            ProviderMeasurementSource = "lastProviderPromptTokens", ProviderToEstimateRatio = 3.25
        });
        var result = await fixture.Create(diagnostics).ExecuteAsync("/context", fixture.Context);
        result.Body.ShouldContain("Persisted LLM-visible history estimate: 1,000 tokens");
        result.Body.ShouldContain("Last completed provider prompt: 3,000 tokens (source: lastProviderPromptTokens)");
        result.Body.ShouldContain("Provider / persisted estimate ratio: 3.25");
    }

    [Fact]
    public async Task Context_InheritedAndAbsentCeiling_ReportsResolvedSourceOrNone()
    {
        var fixture = new Fixture();
        var contributor = fixture.Create();
        foreach (var source in new[] { "conversation", "agent", "world" })
        {
            fixture.Ceiling.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>())
                .Returns(new ScopedCompactionCeiling(12_000, source));
            var result = await contributor.ExecuteAsync("/context", fixture.Context);
            result.Body.ShouldContain($"Compaction ceiling: 12,000 tokens (source: {source})");
        }
        fixture.Ceiling.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>())
            .Returns(new ScopedCompactionCeiling(null, null));
        var noCeiling = await contributor.ExecuteAsync("/context", fixture.Context);
        noCeiling.Body.ShouldContain("Compaction ceiling: none (source: none)");
        noCeiling.Body.ShouldContain("Effective token trigger threshold: 24,000 tokens");
    }

    [Fact]
    public async Task Context_HandleBreakdown_IsSeparateEstimateAndDoesNotExposePrompt()
    {
        var fixture = new Fixture();
        var result = await fixture.Create().ExecuteAsync("/context", fixture.Context);
        result.Body.ShouldContain("Handle context breakdown (estimate)");
        result.Body.ShouldContain("1,234");
        result.Body.ShouldContain("Persisted LLM-visible history estimate: 0 tokens");
        result.Body.ShouldContain("excludes system instructions, tool definitions and injected workspace context");
        result.Body.ShouldContain("not a hard cap or a guarantee that compaction will succeed");
        result.Body.ShouldNotContain("SECRET PROMPT");
        result.Body.ShouldNotContain("SECRET TOOL");
    }

    [Fact]
    public async Task Context_UnavailablePolicy_DoesNotInventPolicyFromGlobalOptionsOrHandle()
    {
        var fixture = new Fixture();
        fixture.Sessions.GetAsync(Fixture.SessionId, Arg.Any<CancellationToken>()).Returns((GatewaySession?)null);
        var result = await fixture.Create().ExecuteAsync("/context", fixture.Context);
        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain("Compaction policy: unavailable");
        result.Body.ShouldNotContain("Effective token trigger threshold:");
        result.Body.ShouldNotContain("80,000");
        result.Body.ShouldNotContain("900,000");
    }

    [Fact]
    public async Task Context_Cancellation_PropagatesToPolicyRead()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var contributor = fixture.Create();
        await Should.ThrowAsync<OperationCanceledException>(() => contributor.ExecuteAsync("/context", fixture.Context, cancellation.Token));
    }

    [Fact]
    public async Task Context_PolicyFailure_PropagatesWithoutFallback()
    {
        var fixture = new Fixture();
        var failure = new InvalidOperationException("policy read failed");
        fixture.Window.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<int?>(failure));
        var contributor = fixture.Create();
        var observed = await Should.ThrowAsync<InvalidOperationException>(() => contributor.ExecuteAsync("/context", fixture.Context));
        observed.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task Context_InvalidPolicy_PropagatesValidationFailure()
    {
        var fixture = new Fixture();
        fixture.Ceiling.ResolveAsync(Fixture.Agent, Fixture.ConversationId, Arg.Any<CancellationToken>())
            .Returns(new ScopedCompactionCeiling(0, "world"));
        var contributor = fixture.Create();
        await Should.ThrowAsync<InvalidOperationException>(() => contributor.ExecuteAsync("/context", fixture.Context));
    }

    [Fact]
    public async Task Context_UnexpectedSourceLabels_DoesNotExposeArbitraryMetadata()
    {
        var fixture = new Fixture();
        var diagnostics = Substitute.For<ISessionCompactionDiagnostics>();
        diagnostics.ResolveAsync(Fixture.Agent, Fixture.SessionId, Arg.Any<CancellationToken>()).Returns(new CompactionPolicyDiagnostics
        {
            CompactionTokenCeiling = 12_000, CompactionTokenCeilingSource = "SECRET CEILING SOURCE",
            ProviderPromptTokens = 20, ProviderMeasurementSource = "SECRET PROVIDER SOURCE"
        });
        var result = await fixture.Create(diagnostics).ExecuteAsync("/context", fixture.Context);
        result.Body.ShouldNotContain("SECRET");
        result.Body.ShouldContain("Compaction ceiling: 12,000 tokens (source: none)");
        result.Body.ShouldContain("Last completed provider prompt: 20 tokens (source: unavailable)");
    }

    [Fact]
    public async Task Context_CancellationToken_IsForwardedUnchanged()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var diagnostics = Substitute.For<ISessionCompactionDiagnostics>();
        await fixture.Create(diagnostics).ExecuteAsync("/context", fixture.Context, cancellation.Token);
        await diagnostics.Received(1).ResolveAsync(Fixture.Agent, Fixture.SessionId, cancellation.Token);
    }

    [Fact]
    public async Task Context_EarlyGuards_PreserveErrorsWithoutReadingPolicy()
    {
        var fixture = new Fixture();
        var diagnostics = Substitute.For<ISessionCompactionDiagnostics>();
        var contributor = fixture.Create(diagnostics);
        var missingSession = await contributor.ExecuteAsync("/context", new CommandExecutionContext { RawInput = "/context", HomeDirectory = "." });
        missingSession.IsError.ShouldBeTrue();
        missingSession.Body.ShouldContain("No active session");
        fixture.SupervisorInspector.GetHandle(Fixture.Agent, Fixture.SessionId).Returns((IAgentHandle?)null);
        var missingHandle = await contributor.ExecuteAsync("/context", fixture.Context);
        missingHandle.IsError.ShouldBeTrue();
        missingHandle.Body.ShouldContain("No active handle");
        fixture.SupervisorInspector.GetHandle(Fixture.Agent, Fixture.SessionId).Returns(fixture.Handle);
        fixture.HandleInspector.GetContextDiagnostics().Returns((ContextDiagnostics?)null);
        var missingDiagnostics = await contributor.ExecuteAsync("/context", fixture.Context);
        missingDiagnostics.IsError.ShouldBeTrue();
        missingDiagnostics.Body.ShouldContain("Context diagnostics are not available");
        var unsupported = await fixture.Create(diagnostics, Substitute.For<IAgentSupervisor>()).ExecuteAsync("/context", fixture.Context);
        unsupported.IsError.ShouldBeTrue();
        unsupported.Body.ShouldContain("does not support context diagnostics");
        diagnostics.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Context_LegacyConstructorWithoutDiagnostics_RemainsUsableAndLabelsFallback()
    {
        var fixture = new Fixture();
        var contributor = new BuiltInCommandContributor(Substitute.For<IAgentRegistry>(), fixture.Supervisor,
            fixture.Sessions, fixture.Monitor, Substitute.For<IServiceProvider>());
        var result = await contributor.ExecuteAsync("/context", fixture.Context);
        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain("Compaction policy: unavailable");
        result.Body.ShouldContain("Configured compaction fallback window: 80,000 tokens (not known model capacity)");
        result.Body.ShouldContain("1,234");
    }

    private sealed class Fixture
    {
        public static readonly AgentId Agent = AgentId.From("command-agent");
        public static readonly SessionId SessionId = SessionId.From("opaque-session");
        public static readonly ConversationId ConversationId = ConversationId.From("actual-conversation");
        public ISessionStore Sessions { get; } = Substitute.For<ISessionStore>();
        public GatewaySession Session { get; } = new() { AgentId = Agent, SessionId = SessionId, ConversationId = ConversationId };
        public ISessionContextWindowResolver Window { get; } = Substitute.For<ISessionContextWindowResolver>();
        public ISessionCompactionCeilingResolver Ceiling { get; } = Substitute.For<ISessionCompactionCeilingResolver>();
        public IOptionsMonitor<CompactionOptions> Monitor { get; } = Substitute.For<IOptionsMonitor<CompactionOptions>>();
        public CompactionOptions Options { get; set; } = new() { ContextWindowTokens = 80_000, TokenThresholdRatio = 0.6 };
        public IAgentSupervisor Supervisor { get; } = Substitute.For<IAgentSupervisor, IAgentHandleInspector>();
        public IAgentHandleInspector SupervisorInspector => (IAgentHandleInspector)Supervisor;
        public IAgentHandle Handle { get; } = Substitute.For<IAgentHandle, IAgentHandleInspector>();
        public IAgentHandleInspector HandleInspector => (IAgentHandleInspector)Handle;
        public SessionCompactionDiagnostics Diagnostics { get; }
        public CommandExecutionContext Context { get; } = new() { RawInput = "/context", AgentId = Agent.Value, SessionId = SessionId.Value, HomeDirectory = "." };

        public Fixture()
        {
            var configuration = new PlatformConfig();
            var conversations = Substitute.For<IConversationStore>();
            Sessions.GetAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Session);
            conversations.GetAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(new Conversation
            {
                AgentId = Agent, ConversationId = ConversationId, WorldId = WorldIdentityResolver.Resolve(configuration).Id
            });
            Window.ResolveAsync(Agent, ConversationId, Arg.Any<CancellationToken>()).Returns(40_000);
            Ceiling.ResolveAsync(Agent, ConversationId, Arg.Any<CancellationToken>()).Returns(new ScopedCompactionCeiling(12_000, "conversation"));
            Monitor.CurrentValue.Returns(_ => Options);
            var config = Substitute.For<IOptionsMonitor<PlatformConfig>>();
            config.CurrentValue.Returns(configuration);
            Diagnostics = new SessionCompactionDiagnostics(Sessions, conversations, Window, Ceiling, Monitor, config);
            SupervisorInspector.GetHandle(Agent, SessionId).Returns(Handle);
            HandleInspector.GetContextWindowTokens().Returns(900_000);
            HandleInspector.GetContextDiagnostics().Returns(new ContextDiagnostics
            {
                TotalEstimatedTokens = 1_234, SystemPrompt = "SECRET PROMPT",
                Tools = [new ToolDiagInfo("SECRET TOOL", "secret description", 100)]
            });
        }

        public ICommandContributor Create(ISessionCompactionDiagnostics? diagnostics = null, IAgentSupervisor? supervisor = null)
        {
            // Parameter-driven construction keeps the RED runnable against the pre-integration constructor.
            var values = new Dictionary<Type, object>
            {
                [typeof(IAgentRegistry)] = Substitute.For<IAgentRegistry>(),
                [typeof(IAgentSupervisor)] = supervisor ?? Supervisor,
                [typeof(ISessionStore)] = Sessions,
                [typeof(IOptionsMonitor<CompactionOptions>)] = Monitor,
                [typeof(IServiceProvider)] = Substitute.For<IServiceProvider>(),
                [typeof(ISessionCompactionDiagnostics)] = diagnostics ?? Diagnostics
            };
            var constructor = typeof(BuiltInCommandContributor).GetConstructors().Single();
            var arguments = constructor.GetParameters().Select(parameter => values.TryGetValue(parameter.ParameterType, out var value)
                ? value : parameter.DefaultValue).ToArray();
            return (ICommandContributor)constructor.Invoke(arguments);
        }
    }
}
