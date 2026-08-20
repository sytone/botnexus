using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pins the scoped-reload contract from #2728: the warm worker runs whenever the changed
/// configuration paths *might* affect warmed session state, and is skipped only when they
/// provably cannot. The gate fails open by construction.
/// </summary>
public sealed class ScopedConfigReloadTests
{
    // AC4: a change confined to an unrelated section must NOT invoke the warm worker.
    [Fact]
    public async Task Reload_WhenChangeIsConfinedToUnrelatedSection_DoesNotInvokeWarmWorker()
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object);
        var plan = ConfigReloadPlan.ForPaths("promptTemplates:daily", "cron:jobs");

        await service.ReloadAsync(plan, CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Never());
    }

    // AC5: a change to a section that DOES affect warmed state must still invoke it.
    [Theory]
    [InlineData("gateway:sessionWarmup:maxSessionsPerAgent")]
    [InlineData("gateway:sessionStore:type")]
    [InlineData("agents:farnsworth:model")]
    public async Task Reload_WhenChangeAffectsWarmedState_InvokesWarmWorker(string changedPath)
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object);

        await service.ReloadAsync(ConfigReloadPlan.ForPaths(changedPath), CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Once());
    }

    // AC6 / AC3: whole-document replacement takes the fail-open path and invokes the worker.
    [Fact]
    public async Task Reload_WhenWholeDocumentReplaced_InvokesWarmWorker()
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object);

        await service.ReloadAsync(ConfigReloadPlan.WholeDocument, CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Once());
    }

    // AC3: an absent plan is indistinguishable from "we do not know" and must reload fully.
    [Fact]
    public async Task Reload_WhenPlanIsAbsent_InvokesWarmWorker()
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object);

        await service.ReloadAsync(plan: null, CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Once());
    }

    // AC3: an unrecognised path carries no information, so it MUST fail open and reload fully.
    [Fact]
    public async Task Reload_WhenPathIsUnrecognised_InvokesWarmWorker()
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object);

        await service.ReloadAsync(
            ConfigReloadPlan.ForPaths("someFutureSectionNobodyClassifiedYet:field"),
            CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Once());
    }

    // AC3: a mixed plan is only skippable if EVERY path is provably unrelated.
    [Fact]
    public async Task Reload_WhenOneOfSeveralPathsIsAffecting_InvokesWarmWorker()
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object);

        await service.ReloadAsync(
            ConfigReloadPlan.ForPaths("promptTemplates:daily", "agents:farnsworth:model"),
            CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Once());
    }

    // AC1/AC3: an empty or whitespace-only path set degrades to whole-document, never to a skip.
    [Fact]
    public void ForPaths_WithEmptyOrBlankPaths_DegradesToWholeDocument()
    {
        ConfigReloadPlan.ForPaths().IsWholeDocument.ShouldBeTrue();
        ConfigReloadPlan.ForPaths("   ", "").IsWholeDocument.ShouldBeTrue();
        ConfigReloadPlan.ForPaths((IEnumerable<string>?)null).IsWholeDocument.ShouldBeTrue();
        SessionWarmupReloadScope.Affects(ConfigReloadPlan.ForPaths()).ShouldBeTrue();
    }

    // AC1: the plan records the changed paths and is separator-agnostic.
    [Fact]
    public void ForPaths_RecordsChangedPathsAndAcceptsEitherSeparator()
    {
        var plan = ConfigReloadPlan.ForPaths("gateway.sessionWarmup.enabled");

        plan.IsWholeDocument.ShouldBeFalse();
        plan.ChangedPaths.ShouldContain("gateway.sessionWarmup.enabled");
        SessionWarmupReloadScope.Affects(plan).ShouldBeTrue();
        ConfigReloadPlan.SplitPath("gateway:sessionWarmup.enabled")
            .ShouldBe(["gateway", "sessionWarmup", "enabled"]);
    }

    // Disabled warmup has nothing to rebuild regardless of the plan.
    [Fact]
    public async Task Reload_WhenWarmupDisabled_DoesNotInvokeWarmWorker()
    {
        var store = CreateSessionStore();
        var service = CreateService(store.Object, new SessionWarmupOptions { Enabled = false });

        await service.ReloadAsync(ConfigReloadPlan.WholeDocument, CancellationToken.None);

        VerifyWarmWorkerInvocations(store, Times.Never());
    }

    private static void VerifyWarmWorkerInvocations(Mock<ISessionStore> store, Times times)
        => store.Verify(
            value => value.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            times);

    private static SessionWarmupService CreateService(
        ISessionStore sessionStore,
        SessionWarmupOptions? options = null)
        => new(
            sessionStore,
            CreateRegistry("agent-a"),
            Options.Create(options ?? new SessionWarmupOptions()),
            NullLogger<SessionWarmupService>.Instance);

    private static Mock<ISessionStore> CreateSessionStore()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(value => value.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionSummary>());
        return store;
    }

    private static IAgentRegistry CreateRegistry(params string[] agentIds)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll())
            .Returns(agentIds.Select(agentId => new AgentDescriptor
            {
                AgentId = AgentId.From(agentId),
                DisplayName = agentId,
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            }).ToList());
        return registry.Object;
    }
}
