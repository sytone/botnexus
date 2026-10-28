using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed class CompactionPolicyDiagnosticsTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");
    private static readonly SessionId Session = SessionId.From("opaque-session");
    private static readonly ConversationId Conversation = ConversationId.From("actual-conversation");

    [Fact]
    public async Task ResolveAsync_ReadsFreshPolicyAndActualConversation_NotHandleBudgetOrSessionId()
    {
        var fixture = new Fixture();
        fixture.Window.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>()).Returns(100_001);
        fixture.Ceiling.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>())
            .Returns(new ScopedCompactionCeiling(20_000, "conversation"));
        fixture.Session.Session.Metadata["lastProviderPromptTokens"] = 22_000;

        var first = await fixture.Service.ResolveAsync(Agent, Session);
        first.ShouldNotBeNull();
        first.ContextWindowTokens.ShouldBe(100_001);
        first.TokenThresholdRatio.ShouldBe(0.6);
        first.RatioSafetyThresholdTokens.ShouldBe(60_000);
        first.EffectiveThresholdTokens.ShouldBe(20_000);
        first.CompactionTokenCeiling.ShouldBe(20_000);
        first.CompactionTokenCeilingSource.ShouldBe("conversation");
        first.LocalEstimatedTokens.ShouldBe(0);
        first.ProviderPromptTokens.ShouldBe(22_000);
        first.ProviderMeasurementSource.ShouldBe("lastProviderPromptTokens");
        first.ProviderToEstimateRatio.ShouldBeNull();

        fixture.Options = fixture.Options with { TokenThresholdRatio = 0.4 };
        fixture.Window.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>()).Returns(40_001);
        fixture.Ceiling.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>())
            .Returns(new ScopedCompactionCeiling(30_000, "agent"));
        var second = await fixture.Service.ResolveAsync(Agent, Session);
        second.ShouldNotBeNull();
        second.TokenThresholdRatio.ShouldBe(0.4);
        second.RatioSafetyThresholdTokens.ShouldBe(16_000);
        second.EffectiveThresholdTokens.ShouldBe(16_000);
        second.CompactionTokenCeilingSource.ShouldBe("agent");
        await fixture.Window.Received(2).ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>());
        await fixture.Ceiling.Received(2).ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_MeasurementUsesExistingVisibleHistoryEstimatorAndRealProducer()
    {
        var fixture = new Fixture();
        fixture.Session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "abcdefgh" });
        fixture.Session.History.Add(new SessionEntry { Role = MessageRole.User, Content = new string('x', 1000), IsHistory = true });
        fixture.Session.History.Add(new SessionEntry { Role = MessageRole.Notification, Content = new string('x', 1000) });
        BotNexus.Gateway.Streaming.ProviderTokenUsageRecorder.Record(fixture.Session,
            new AgentResponseUsage { InputTokens = 10, CacheRead = 5, CacheWrite = 5 }).ShouldBeTrue();
        var measured = await fixture.Service.ResolveAsync(Agent, Session);
        measured.ShouldNotBeNull();
        measured.LocalEstimatedTokens.ShouldBe(2);
        measured.ProviderPromptTokens.ShouldBe(20);
        measured.ProviderToEstimateRatio.ShouldBe(10);
        measured.ProviderMeasurementSource.ShouldBe("lastProviderPromptTokens");
        fixture.Session.Metadata.Remove("lastProviderPromptTokens");
        var cleared = await fixture.Service.ResolveAsync(Agent, Session);
        cleared.ShouldNotBeNull();
        cleared.LocalEstimatedTokens.ShouldBe(2);
        cleared.ProviderPromptTokens.ShouldBeNull();
        cleared.ProviderMeasurementSource.ShouldBeNull();
        cleared.ProviderToEstimateRatio.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-count")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task ResolveAsync_UnavailableProviderMeasurement_RemainsNull(string? raw)
    {
        var fixture = new Fixture();
        if (raw is not null) fixture.Session.Session.Metadata["lastProviderPromptTokens"] = raw;
        var snapshot = await fixture.Service.ResolveAsync(Agent, Session);
        snapshot.ShouldNotBeNull();
        snapshot.ProviderPromptTokens.ShouldBeNull();
        snapshot.ProviderMeasurementSource.ShouldBeNull();
        snapshot.ProviderToEstimateRatio.ShouldBeNull();
        snapshot.CompactionTokenCeiling.ShouldBeNull();
        snapshot.CompactionTokenCeilingSource.ShouldBeNull();
        snapshot.ContextWindowTokens.ShouldBe(80_000); // Configured runtime fallback, not a new default.
        snapshot.ScopedContextWindowTokens.ShouldBeNull();
        snapshot.EffectiveThresholdTokens.ShouldBe(48_000);
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("wrong-session-agent")]
    [InlineData("missing-conversation")]
    [InlineData("wrong-conversation-agent")]
    [InlineData("foreign-world")]
    public async Task ResolveAsync_UnknownOrForeignOwnership_DoesNotResolvePolicy(string scenario)
    {
        var fixture = new Fixture();
        switch (scenario)
        {
            case "missing-session":
                fixture.Sessions.GetAsync(Session, Arg.Any<CancellationToken>()).Returns((GatewaySession?)null);
                break;
            case "wrong-session-agent":
                fixture.Sessions.GetAsync(Session, Arg.Any<CancellationToken>()).Returns(new GatewaySession
                {
                    SessionId = Session, AgentId = AgentId.From("other"), ConversationId = Conversation
                });
                break;
            case "missing-conversation":
                fixture.Conversations.GetAsync(Conversation, Arg.Any<CancellationToken>()).Returns((Conversation?)null);
                break;
            case "wrong-conversation-agent":
                fixture.Conversations.GetAsync(Conversation, Arg.Any<CancellationToken>()).Returns(fixture.Conversation with { AgentId = AgentId.From("other") });
                break;
            case "foreign-world":
                fixture.Conversation.WorldId = "foreign";
                break;
        }
        (await fixture.Service.ResolveAsync(Agent, Session)).ShouldBeNull();
        fixture.Window.ReceivedCalls().ShouldBeEmpty();
        fixture.Ceiling.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_PolicyReadFailure_DoesNotServeRelaxedOrCachedPolicy()
    {
        var fixture = new Fixture();
        fixture.Ceiling.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ScopedCompactionCeiling>(new InvalidOperationException("policy read failed")));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Service.ResolveAsync(Agent, Session));
    }

    [Fact]
    public async Task ResolveAsync_InvalidCeiling_DoesNotPublishAThreshold()
    {
        var fixture = new Fixture();
        fixture.Ceiling.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>()).Returns(new ScopedCompactionCeiling(0, "world"));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Service.ResolveAsync(Agent, Session));
    }

    [Fact]
    public async Task ResolveAsync_Cancellation_Propagates()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Service.ResolveAsync(Agent, Session, cancellation.Token));
    }

    [Fact]
    public async Task BuildContextResponse_AddsSafeSnapshot_PreservesLegacyFieldsAndUnknowns()
    {
        var fixture = new Fixture();
        var snapshot = await fixture.Service.ResolveAsync(Agent, Session);
        var response = AgentsController.BuildContextResponse(Agent.Value, Session.Value,
            new ContextDiagnostics { TotalEstimatedTokens = 123, SystemPromptTokens = 100, SystemPrompt = "SECRET PROMPT" },
            compactionPolicy: snapshot);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.ShouldNotContain("SECRET PROMPT");
        json.ShouldNotContain("memoryFlush");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("totalEstimatedTokens").GetInt32().ShouldBe(123);
        root.GetProperty("contextWindowTokens").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("usagePercent").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("sections").GetProperty("systemPrompt").GetProperty("tokens").GetInt32().ShouldBe(100);
        var policy = root.GetProperty("compactionPolicy");
        policy.GetProperty("effectiveThresholdTokens").GetInt32().ShouldBe(48_000);
        policy.GetProperty("providerPromptTokens").ValueKind.ShouldBe(JsonValueKind.Null);
        policy.GetProperty("compactionTokenCeiling").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetContextAsync_NoResidentHandle_PreservesTransient404AndDoesNotReadPolicy()
    {
        var diagnostics = Substitute.For<ISessionCompactionDiagnostics>();
        var controller = new AgentsController(Substitute.For<IAgentRegistry>(), Substitute.For<IAgentSupervisor>(),
            Substitute.For<IAgentConfigurationWriter>(), compactionDiagnostics: diagnostics);
        var response = await controller.GetContextAsync(Agent.Value, Session.Value);
        response.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe(AgentsController.NoActiveHandleMessage);
        diagnostics.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetContextAsync_ResidentHandle_UsesFreshServiceInsteadOfBoundWindow()
    {
        var fixture = new Fixture();
        fixture.Window.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>()).Returns(40_000);
        var registry = Substitute.For<IAgentRegistry>();
        var descriptor = new AgentDescriptor { AgentId = Agent, DisplayName = "Agent A", ModelId = "test-model", ApiProvider = "test-provider" };
        registry.Get(Agent).Returns(descriptor);
        var handle = Substitute.For<IAgentHandle, IAgentHandleInspector>();
        var inspector = (IAgentHandleInspector)handle;
        inspector.GetContextDiagnostics().Returns(new ContextDiagnostics { TotalEstimatedTokens = 100, SystemPrompt = "SECRET PROMPT" });
        inspector.GetContextWindowTokens().Returns(900_000);
        var strategy = Substitute.For<IIsolationStrategy>();
        strategy.Name.Returns(descriptor.IsolationStrategy);
        strategy.CreateAsync(descriptor, Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(handle);
        var supervisor = new DefaultAgentSupervisor(registry, [strategy], fixture.Sessions,
            NullLogger<DefaultAgentSupervisor>.Instance);
        await supervisor.GetOrCreateAsync(Agent, Session);
        try
        {
            var controller = new AgentsController(registry, supervisor, Substitute.For<IAgentConfigurationWriter>(),
                compactionDiagnostics: fixture.Service);
            var first = await controller.GetContextAsync(Agent.Value, Session.Value);
            using var firstJson = JsonDocument.Parse(JsonSerializer.Serialize(first.ShouldBeOfType<OkObjectResult>().Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            firstJson.RootElement.GetProperty("contextWindowTokens").GetInt32().ShouldBe(900_000);
            firstJson.RootElement.GetProperty("compactionPolicy").GetProperty("contextWindowTokens").GetInt32().ShouldBe(40_000);
            fixture.Window.ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>()).Returns(20_000);
            var second = await controller.GetContextAsync(Agent.Value, Session.Value);
            var wire = JsonSerializer.Serialize(second.ShouldBeOfType<OkObjectResult>().Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            wire.ShouldNotContain("SECRET PROMPT");
            using var secondJson = JsonDocument.Parse(wire);
            secondJson.RootElement.GetProperty("compactionPolicy").GetProperty("effectiveThresholdTokens").GetInt32().ShouldBe(12_000);
            await fixture.Window.Received(2).ResolveAsync(Agent, Conversation, Arg.Any<CancellationToken>());
        }
        finally
        {
            await supervisor.StopAsync(Agent, Session);
        }
    }

    private sealed class Fixture
    {
        public ISessionStore Sessions { get; } = Substitute.For<ISessionStore>();
        public IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();
        public ISessionContextWindowResolver Window { get; } = Substitute.For<ISessionContextWindowResolver>();
        public ISessionCompactionCeilingResolver Ceiling { get; } = Substitute.For<ISessionCompactionCeilingResolver>();
        public CompactionOptions Options { get; set; } = new() { ContextWindowTokens = 80_000, TokenThresholdRatio = 0.6 };
        public GatewaySession Session { get; } = new() { SessionId = CompactionPolicyDiagnosticsTests.Session, AgentId = Agent, ConversationId = CompactionPolicyDiagnosticsTests.Conversation };
        public Conversation Conversation { get; }
        public SessionCompactionDiagnostics Service { get; }

        public Fixture()
        {
            var configuration = new PlatformConfig();
            Conversation = new Conversation
            {
                ConversationId = CompactionPolicyDiagnosticsTests.Conversation, AgentId = Agent,
                WorldId = WorldIdentityResolver.Resolve(configuration).Id
            };
            Sessions.GetAsync(CompactionPolicyDiagnosticsTests.Session, Arg.Any<CancellationToken>()).Returns(Session);
            Conversations.GetAsync(CompactionPolicyDiagnosticsTests.Conversation, Arg.Any<CancellationToken>()).Returns(Conversation);
            Ceiling.ResolveAsync(Agent, CompactionPolicyDiagnosticsTests.Conversation, Arg.Any<CancellationToken>()).Returns(new ScopedCompactionCeiling(null, null));
            var options = Substitute.For<IOptionsMonitor<CompactionOptions>>();
            options.CurrentValue.Returns(_ => Options);
            var config = Substitute.For<IOptionsMonitor<PlatformConfig>>();
            config.CurrentValue.Returns(configuration);
            Service = new SessionCompactionDiagnostics(Sessions, Conversations, Window, Ceiling, options, config);
        }
    }
}
