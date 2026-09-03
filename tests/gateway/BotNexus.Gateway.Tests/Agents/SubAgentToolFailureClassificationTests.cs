using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Behavioural guards for #3565: a sub-agent whose tools all failed but which produced confident
/// prose used to be recorded <c>Completed</c> and handed to the parent as a normal result, because
/// the completion contract was text-only and <c>hasFinalResponse</c> was the sole input to the
/// Completed/Failed decision. These tests pin the four dispositions the issue enumerates - clean
/// run, text-plus-failed-tool, empty response, terminal provider error - at the seam that decides.
/// </summary>
public sealed class SubAgentToolFailureClassificationTests
{
    private const string NarratedSummary = "I have completed the migration and everything is green.";

    [Fact]
    public async Task CleanRun_WithText_StaysCompleted_AndSummaryIsUnmodified()
    {
        var manager = CreateManager(out var dispatcher);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await manager.OnCompletedAsync(spawned.SubAgentId, NarratedSummary, SubAgentRunOutcome.Clean);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Completed);
        info.ResultSummary.ShouldBe(NarratedSummary);
    }

    [Fact]
    public async Task TextWithFailedTool_IsRecordedFailed_AndNamesIdAndToolError()
    {
        var manager = CreateManager(out var dispatcher);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var outcome = new SubAgentRunOutcome(2, "write: access to the path is denied", null);
        await manager.OnCompletedAsync(spawned.SubAgentId, NarratedSummary, outcome);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Failed);
        info.ResultSummary.ShouldNotBeNull();

        // AC3: id AND underlying tool error, so the parent can act without opening the transcript.
        info.ResultSummary!.ShouldContain(spawned.SubAgentId);
        info.ResultSummary.ShouldContain("write: access to the path is denied");
        info.ResultSummary.ShouldContain("2 tool invocations failed");

        // The run's own words survive, but explicitly disclaimed - never as a standalone success.
        info.ResultSummary.ShouldContain(NarratedSummary);
        info.ResultSummary.ShouldNotBe(NarratedSummary);
    }

    [Fact]
    public async Task TerminalProviderError_IsRecordedFailed_EvenWithText()
    {
        var manager = CreateManager(out var dispatcher);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var outcome = new SubAgentRunOutcome(0, null, "model 'gpt-4o-retired' was not found");
        await manager.OnCompletedAsync(spawned.SubAgentId, NarratedSummary, outcome);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Failed);
        info.ResultSummary.ShouldNotBeNull();
        info.ResultSummary!.ShouldContain("model 'gpt-4o-retired' was not found");
        info.ResultSummary.ShouldContain(spawned.SubAgentId);
    }

    [Fact]
    public async Task EmptyResponse_WithCleanTools_KeepsTheEmptyResponseDiagnostic()
    {
        var manager = CreateManager(out var dispatcher);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await manager.OnCompletedAsync(spawned.SubAgentId, "   ", SubAgentRunOutcome.Clean);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Failed);
        info.ResultSummary.ShouldBe("Sub-agent failed because it returned an empty final response.");
    }

    [Fact]
    public async Task NoOutcomeSupplied_PreservesHistoricalTextOnlyBehaviour()
    {
        var manager = CreateManager(out var dispatcher);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        // A caller that cannot observe the run's timeline must still complete the sub-agent, and
        // "not measured" must never be mistaken for "measured as failing".
        await manager.OnCompletedAsync(spawned.SubAgentId, NarratedSummary);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Completed);
        info.ResultSummary.ShouldBe(NarratedSummary);
    }

    [Fact]
    public void RunOutcome_From_CountsEveryFailedTool_AndKeepsTheLastError()
    {
        var response = new AgentResponse
        {
            Content = NarratedSummary,
            ToolCalls =
            [
                new AgentToolCallInfo("c1", "read", IsError: true, ResultContent: "first failure"),
                new AgentToolCallInfo("c2", "write", IsError: false, ResultContent: "ok"),
                new AgentToolCallInfo("c3", "exec", IsError: true, ResultContent: "last failure")
            ]
        };

        var outcome = SubAgentRunOutcome.From(response);

        outcome.FailedToolCount.ShouldBe(2);
        outcome.LastToolError.ShouldBe("last failure");
        outcome.HasFailure.ShouldBeTrue();
    }

    [Fact]
    public void RunOutcome_From_DetailFreeFailure_StillCounts_AndNamesTheTool()
    {
        var response = new AgentResponse
        {
            Content = NarratedSummary,
            ToolCalls = [new AgentToolCallInfo("c1", "browser", IsError: true, ResultContent: null)]
        };

        var outcome = SubAgentRunOutcome.From(response);

        outcome.FailedToolCount.ShouldBe(1);
        outcome.HasFailure.ShouldBeTrue();
        outcome.LastToolError.ShouldNotBeNull();
        outcome.LastToolError!.ShouldContain("browser");
    }

    [Fact]
    public void RunOutcome_From_CleanRun_HasNoFailure()
    {
        var response = new AgentResponse
        {
            Content = NarratedSummary,
            ToolCalls = [new AgentToolCallInfo("c1", "read", IsError: false, ResultContent: "ok")]
        };

        SubAgentRunOutcome.From(response).HasFailure.ShouldBeFalse();
    }

    [Fact]
    public void TerminalError_IsProjected_OnlyForAnErroredFinishReason()
    {
        InProcessAgentHandle
            .DescribeTerminalError(new AssistantAgentMessage("ok", FinishReason: StopReason.Stop))
            .ShouldBeNull();

        InProcessAgentHandle
            .DescribeTerminalError(new AssistantAgentMessage(
                "partial",
                FinishReason: StopReason.Error,
                ErrorMessage: "provider rejected the request"))
            .ShouldBe("provider rejected the request");

        // A detail-free provider failure must remain distinguishable from a clean turn.
        InProcessAgentHandle
            .DescribeTerminalError(new AssistantAgentMessage("partial", FinishReason: StopReason.Error))
            .ShouldNotBeNull();
    }

    private static DefaultSubAgentManager CreateManager(out Mock<IChannelDispatcher> dispatcher)
    {
        var childHandle = CreateHangingHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        dispatcher = new Mock<IChannelDispatcher>();

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        return handle;
    }
}
