using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class ScopedCompactionCoordinatorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompactAsync_FlushAndCompact_UseSameResolvedSnapshot(bool supplyScopedOptions, bool force)
    {
        var global = new CompactionOptions { ContextWindowTokens = 200_000, PreservedTurns = 3 };
        var scoped = global with { ContextWindowTokens = 128_000, MaxSummaryChars = 777 };
        var session = NewSession();
        using var cancellation = new CancellationTokenSource();
        CompactionOptions? checkedOptions = null;
        CompactionOptions? flushedOptions = null;
        CompactionOptions? compactedOptions = null;
        var monitor = new Mock<IOptionsMonitor<CompactionOptions>>();
        monitor.SetupGet(value => value.CurrentValue).Returns(global);
        var flusher = new Mock<IPreCompactionMemoryFlusher>();
        flusher.Setup(value => value.ShouldFlush(session.Session, It.IsAny<CompactionOptions>()))
            .Callback<Session, CompactionOptions>((_, options) => checkedOptions = options)
            .Returns(true);
        flusher.Setup(value => value.FlushAsync(session.AgentId, session.Session, It.IsAny<CompactionOptions>(), cancellation.Token))
            .Callback<AgentId, Session, CompactionOptions, CancellationToken>((_, _, options, _) =>
            {
                flushedOptions = options;
                monitor.SetupGet(value => value.CurrentValue).Returns(global with { ContextWindowTokens = 999_000 });
            })
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(value => value.CompactAsync(session, It.IsAny<CompactionOptions>(), cancellation.Token))
            .Callback<GatewaySession, CompactionOptions, CancellationToken>((_, options, _) => compactedOptions = options)
            .ReturnsAsync(CompactionResult.Skipped(0, 0, skipReason: CompactionSkipReason.EmptyHistory));
        var coordinator = CreateCoordinator(compactor.Object, monitor.Object, flusher.Object);

        await coordinator.CompactAsync(session.AgentId, session, cancellation.Token, force: force,
            resolvedOptions: supplyScopedOptions ? scoped : null);

        checkedOptions.ShouldNotBeNull();
        flushedOptions.ShouldBeSameAs(checkedOptions);
        compactedOptions.ShouldBeSameAs(checkedOptions);
        checkedOptions.ContextWindowTokens.ShouldBe(supplyScopedOptions ? 128_000 : 200_000);
        checkedOptions.PreservedTurns.ShouldBe(force ? 0 : 3);
        checkedOptions.MaxSummaryChars.ShouldBe(supplyScopedOptions ? 777 : global.MaxSummaryChars);
        if (!force)
            checkedOptions.ShouldBeSameAs(supplyScopedOptions ? scoped : global);
        scoped.PreservedTurns.ShouldBe(3);
        global.PreservedTurns.ShouldBe(3);
        flusher.Verify(value => value.FlushAsync(session.AgentId, session.Session, It.IsAny<CompactionOptions>(), cancellation.Token), Times.Once);
        compactor.Verify(value => value.CompactAsync(session, It.IsAny<CompactionOptions>(), cancellation.Token), Times.Once);
    }

    [Fact]
    public async Task CompactAsync_ToolHeavy85kWithOneUserTurn_ScopedBudgetEnablesRealFallback()
    {
        var model = new LlmModel("summary-model", "Summary", "test-api", "test-provider", "https://example.com",
            false, ["text"], new ModelCost(0, 0, 0, 0), 128_000, 1024);
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(value => value.Api).Returns(model.Api);
        provider.Setup(value => value.StreamSimple(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
            .Returns(() =>
            {
                var stream = new LlmStream();
                var message = new AssistantMessage([new TextContent("compact summary")], model.Api, model.Provider,
                    model.Id, Usage.Empty(), StopReason.Stop, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                stream.Push(new DoneEvent(StopReason.Stop, message));
                stream.End(message);
                return stream;
            });
        var models = new ModelRegistry();
        models.Register(model.Provider, model);
        var providers = new ApiProviderRegistry();
        providers.Register(provider.Object);
        var compactor = new LlmSessionCompactor(new LlmClient(providers, models), NullLogger<LlmSessionCompactor>.Instance);
        var global = new CompactionOptions
        {
            ContextWindowTokens = 200_000, TokenThresholdRatio = 0.6, PreservedTurns = 3,
            LargestEntryBytesThreshold = 0, SummarizationModel = model.Id
        };
        var scoped = ScopedCompactionWindow.Apply(global, 128_000);
        var session = NewSession();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "investigate" });
        for (var i = 0; i < 20; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = new string('x', 17_000) });

        compactor.ShouldCompact(session.Session, global).ShouldBeFalse();
        compactor.ShouldCompact(session.Session, scoped).ShouldBeTrue();
        var baseline = await compactor.CompactAsync(session, global, CancellationToken.None);
        baseline.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);
        baseline.TokensBefore.ShouldBeInRange(84_000, 86_000);
        provider.Verify(value => value.StreamSimple(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()), Times.Never);
        var monitor = new Mock<IOptionsMonitor<CompactionOptions>>();
        monitor.SetupGet(value => value.CurrentValue).Returns(global);
        var coordinator = CreateCoordinator(compactor, monitor.Object);

        var outcome = await coordinator.CompactAsync(session.AgentId, session, CancellationToken.None, resolvedOptions: scoped);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Applied.ShouldBeTrue();
        outcome.SkipReason.ShouldBeNull();
        outcome.EntriesSummarized.ShouldBe(21);
        outcome.EntriesPreserved.ShouldBe(0);
        outcome.TokensAfter.ShouldBeLessThan(outcome.TokensBefore);
        session.GetHistorySnapshot().Count(entry => entry.IsCompactionSummary && !entry.IsHistory).ShouldBe(1);
        provider.Verify(value => value.StreamSimple(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()), Times.Once);
    }

    private static GatewaySession NewSession() => new()
    {
        SessionId = SessionId.From(Guid.NewGuid().ToString("N")), AgentId = AgentId.From("scoped-agent")
    };

    private static SessionCompactionCoordinator CreateCoordinator(ISessionCompactor compactor,
        IOptionsMonitor<CompactionOptions> options, IPreCompactionMemoryFlusher? flusher = null)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(value => value.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionSaveOutcome.Persisted);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(value => value.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new SessionCompactionCoordinator(compactor, store.Object, supervisor.Object, null, null, options,
            NullLogger<SessionCompactionCoordinator>.Instance, flusher);
    }
}
