using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Covers the <c>/api/chat</c> contract additions that back the headless CLI runner (#2396):
/// per-run model / thinking overrides, and the tool-call summary a shell caller needs to tell an
/// answered-from-context turn apart from one that did work.
/// </summary>
public sealed class ChatControllerRunOverrideTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");

    private static (ChatController Controller, Mock<ISessionStore> Sessions, List<GatewaySession> Saved) Build(
        AgentResponse response)
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var saved = new List<GatewaySession>();
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId id, AgentId agentId, CancellationToken _) =>
                new GatewaySession { SessionId = id, AgentId = agentId });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback((GatewaySession s, CancellationToken _) => saved.Add(s))
            .Returns(Task.CompletedTask);

        return (new ChatController(supervisor.Object, sessions.Object), sessions, saved);
    }

    [Fact]
    public async Task Send_WithModelAndThinking_RecordsThemAsSessionMetadataBeforeTheRun()
    {
        var (controller, _, saved) = Build(new AgentResponse { Content = "ok" });

        await controller.Send(
            new ChatRequest("agent-a", "go", Model: "claude-opus-5", Thinking: "high"),
            CancellationToken.None);

        // The overrides must be persisted BEFORE the turn, on the same seam the cron/soul/heartbeat
        // triggers use - otherwise the supervisor never sees them and the flags are inert.
        saved.ShouldNotBeEmpty();
        var first = saved[0];
        first.Metadata["modelOverride"].ShouldBe("claude-opus-5");
        first.Metadata["thinkingOverride"].ShouldBe("high");
    }

    [Fact]
    public async Task Send_WithoutOverrides_DoesNotStampOverrideMetadata()
    {
        var (controller, _, saved) = Build(new AgentResponse { Content = "ok" });

        await controller.Send(new ChatRequest("agent-a", "go"), CancellationToken.None);

        foreach (var session in saved)
        {
            session.Metadata.ContainsKey("modelOverride").ShouldBeFalse();
            session.Metadata.ContainsKey("thinkingOverride").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Send_WhenAgentIsUnknownAndOverridesWereSupplied_StillReturnsNotFound()
    {
        // An override must not turn an unknown-agent 404 into a 500 or a silent success: the CLI
        // maps 404 to its dedicated unknown-agent exit code, so this boundary is load-bearing.
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Agent 'agent-a' is not registered."));

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId id, AgentId agentId, CancellationToken _) =>
                new GatewaySession { SessionId = id, AgentId = agentId });

        var controller = new ChatController(supervisor.Object, sessions.Object);

        var result = await controller.Send(
            new ChatRequest("agent-a", "go", Model: "claude-opus-5"), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Send_ProjectsToolCallsOntoTheResponse()
    {
        var (controller, _, _) = Build(new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("tc1", "read", IsError: false, Arguments: "{\"path\":\"secret\"}", ResultContent: "body"),
                new AgentToolCallInfo("tc2", "exec", IsError: true)
            ]
        });

        var result = await controller.Send(new ChatRequest("agent-a", "go"), CancellationToken.None);

        var body = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ChatResponse>();
        body.ToolCalls.ShouldNotBeNull();
        body.ToolCalls!.Count.ShouldBe(2);
        body.ToolCalls[0].ToolName.ShouldBe("read");
        body.ToolCalls[0].IsError.ShouldBeFalse();
        body.ToolCalls[1].ToolName.ShouldBe("exec");
        body.ToolCalls[1].IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Send_ToolCallSummary_OmitsArgumentsAndResultBodies()
    {
        // The summary is deliberately narrower than AgentToolCallInfo. Echoing tool arguments and
        // results back over the wire would widen the disclosure surface of every REST chat call.
        var (controller, _, _) = Build(new AgentResponse
        {
            Content = "done",
            ToolCalls = [new AgentToolCallInfo("tc1", "read", false, Arguments: "{\"path\":\"/etc/shadow\"}", ResultContent: "root:x:0:0")]
        });

        var result = await controller.Send(new ChatRequest("agent-a", "go"), CancellationToken.None);

        var body = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ChatResponse>();
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        json.ShouldNotContain("/etc/shadow");
        json.ShouldNotContain("root:x:0:0");
    }

    [Fact]
    public async Task Send_WhenTurnCalledNoTools_ReturnsAnEmptyListRatherThanNull()
    {
        var (controller, _, _) = Build(new AgentResponse { Content = "answered from context" });

        var result = await controller.Send(new ChatRequest("agent-a", "go"), CancellationToken.None);

        var body = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ChatResponse>();
        body.ToolCalls.ShouldNotBeNull();
        body.ToolCalls!.ShouldBeEmpty();
    }
}
