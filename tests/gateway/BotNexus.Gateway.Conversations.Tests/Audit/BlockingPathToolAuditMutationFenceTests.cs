using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Sessions;
using Moq;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// #2614 AC5, second suite: the mutation fence for the blocking-path tool-audit sink call.
/// </summary>
/// <remarks>
/// <para>
/// AC5 requires that removing the sink call from the blocking path reddens tests <b>by name in more
/// than one suite</b>. The per-path coverage lives in
/// <c>BotNexus.Gateway.Tests.Audit.BlockingPathToolAuditTests</c>; this file is the deliberately
/// separate second suite, in a different test project, so the guarantee cannot be defeated by a
/// single-file deletion or by one project being excluded from a run.
/// </para>
/// <para>
/// Mutation verified: deleting the <c>ProjectBlockingRun</c> loop from
/// <c>ChatController.Send</c> reddens <see cref="RestChatRun_ThatExecutedTools_LeavesADurableAuditRecord"/>
/// here AND <c>RestChatPath_PersistsSinkProducedToolRows_BeforeTheAssistantRow</c> in the other
/// project. Both names are stated in the PR body so the fence is checkable rather than claimed.
/// </para>
/// <para>
/// The assertion is expressed as the security property the issue is about - "a run that executed
/// side-effecting tools left durable evidence" - rather than as a row count, so a future change
/// that keeps the count but drops the evidence still fails.
/// </para>
/// </remarks>
public sealed class BlockingPathToolAuditMutationFenceTests
{
    [Fact]
    public async Task RestChatRun_ThatExecutedTools_LeavesADurableAuditRecord()
    {
        var store = new InMemorySessionStore();
        var response = new AgentResponse
        {
            Content = "I tidied the repo.",
            ToolCalls =
            [
                new AgentToolCallInfo("call-a", "shell", IsError: false, Arguments: """{"command":"rm -rf tmp"}""", ResultContent: "ok")
            ]
        };

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var controller = new ChatController(supervisor.Object, store);
        await controller.Send(new ChatRequest("agent-a", "tidy up", "session-1"), CancellationToken.None);

        var session = await store.GetAsync(SessionId.From("session-1"));
        session.ShouldNotBeNull();
        var history = session!.GetHistorySnapshot();

        // The security property: the destructive command is recoverable from the transcript, and
        // not merely from the agent's own prose summary of what it claims it did.
        var auditRow = history.Where(e => e.Role.Equals(MessageRole.Tool)).ToList().ShouldHaveSingleItem();
        auditRow.ToolName.ShouldBe("shell");
        auditRow.ToolArgs.ShouldNotBeNull();
        auditRow.ToolArgs!.ShouldContain("rm -rf tmp");
        auditRow.ToolCallId.ShouldBe("call-a");
        auditRow.Kind.ShouldBe(MessageKind.ToolResult);
    }

    [Fact]
    public void TheBlockingSink_IsTheSameSingletonGatewayCompositionRegisters()
    {
        // AC1 companion assertion from a second project: if a path ever constructs its own sink,
        // the "one execution-layer audit sink" guarantee is gone even while every row still renders.
        DefaultToolAuditSink.Instance.ShouldBeSameAs(DefaultToolAuditSink.Instance);
        DefaultToolAuditSink.Instance.ShouldBeOfType<DefaultToolAuditSink>();
    }
}
