using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tests.Dispatching;
using Moq;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression coverage for #2484: the portal's Steer, Redirect (InterruptAndSteer) and Follow Up
/// paths silently discarded draft attachments.
/// </summary>
/// <remarks>
/// <para>
/// Root cause is the same defect #2296/#2294 fixed for the Send path, resurfacing because that fix
/// landed at ONE call site (<c>GatewayHost.BuildUserMessage</c>) rather than at a shared seam.
/// Steer/redirect/follow-up never reached <c>AppendNonImageAttachments</c> at all: their hub methods
/// took only a <c>string</c>, so there was no seam to carry parts through. The fix routes every
/// dispatch path through the single <see cref="AgentUserMessageComposer"/> seam.
/// </para>
/// <para>
/// Each test asserts the OBSERVABLE - the attachment content reaching the agent-facing message -
/// not merely that a call did not throw. A broken implementation is one that reaches the handle
/// with zero parts, and every assertion below fails in exactly that case.
/// </para>
/// <para>
/// Vacuity: no test below contains an early <c>return</c>, a conditional skip, or a
/// catch-and-continue. Every test ends in an unconditional assertion.
/// </para>
/// </remarks>
public sealed class DraftAttachmentDispatchTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");
    private static readonly SessionId Session = SessionId.From("sess-attach");

    private const string TextAttachmentBody = "line one\nline two";
    private const string ImageBase64Marker = "data:image/png;base64,";

    private static IReadOnlyList<MediaContentPartDto> TextPart() =>
        [new MediaContentPartDto { MimeType = "text/plain", FileName = "notes.log", Text = TextAttachmentBody }];

    private static IReadOnlyList<MediaContentPartDto> ImagePart() =>
        [new MediaContentPartDto { MimeType = "image/png", FileName = "shot.png", Base64Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }];

    private static Mock<IAgentSupervisor> SupervisorFor(IAgentHandle handle)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(handle);
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        return supervisor;
    }

    private static Mock<IAgentHandle> RunningHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        return handle;
    }

    // ── Path 1: Steer ────────────────────────────────────────────────────

    [Fact]
    public async Task Steer_WithTextAttachment_DeliversAttachmentContentToTheAgent()
    {
        AgentUserMessage? injected = null;
        var handle = RunningHandle();
        handle.Setup(h => h.SteerAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => injected = m)
            .Returns(Task.CompletedTask);

        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        await hub.SteerWithMedia(Agent, Session, "look at this", TextPart(), conversationId: null);

        injected.ShouldNotBeNull();
        injected!.Content.ShouldContain("look at this");
        injected.Content.ShouldContain(TextAttachmentBody);
    }

    [Fact]
    public async Task Steer_WithImageAttachment_DeliversImageOnTheVisionPath()
    {
        AgentUserMessage? injected = null;
        var handle = RunningHandle();
        handle.Setup(h => h.SteerAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => injected = m)
            .Returns(Task.CompletedTask);

        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        await hub.SteerWithMedia(Agent, Session, "what is this", ImagePart(), conversationId: null);

        injected.ShouldNotBeNull();
        injected!.Images.ShouldNotBeNull();
        injected.Images!.Count.ShouldBe(1);
        injected.Images[0].Value.ShouldStartWith(ImageBase64Marker);
    }

    // ── Path 2: Redirect (InterruptAndSteer) ─────────────────────────────

    [Fact]
    public async Task Redirect_WithTextAttachment_DeliversAttachmentContentToTheAgent()
    {
        AgentUserMessage? injected = null;
        var handle = RunningHandle();
        handle.Setup(h => h.InterruptAndSteerAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => injected = m)
            .Returns(Task.CompletedTask);

        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        var delivered = await hub.InterruptAndSteerWithMedia(Agent, Session, "do this instead", TextPart());

        delivered.ShouldBeTrue();
        injected.ShouldNotBeNull();
        injected!.Content.ShouldContain("do this instead");
        injected.Content.ShouldContain(TextAttachmentBody);
    }

    [Fact]
    public async Task Redirect_WithImageAttachment_DeliversImageOnTheVisionPath()
    {
        AgentUserMessage? injected = null;
        var handle = RunningHandle();
        handle.Setup(h => h.InterruptAndSteerAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => injected = m)
            .Returns(Task.CompletedTask);

        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        var delivered = await hub.InterruptAndSteerWithMedia(Agent, Session, "look here", ImagePart());

        delivered.ShouldBeTrue();
        injected.ShouldNotBeNull();
        injected!.Images.ShouldNotBeNull();
        injected.Images!.Count.ShouldBe(1);
        injected.Images[0].Value.ShouldStartWith(ImageBase64Marker);
    }

    // ── Path 3: Follow Up (running -> pending-message queue, #2458) ───────

    [Fact]
    public async Task FollowUp_WhileRunning_QueuesTheAttachmentThroughThePendingMessageQueue()
    {
        // #2458 added the follow-up queue. A message that queues and dequeues without its parts is
        // the same data loss wearing a different hat, so assert on what is HANDED TO THE QUEUE.
        AgentUserMessage? queued = null;
        var handle = RunningHandle();
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => queued = m)
            .ReturnsAsync(true);

        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        await hub.FollowUpWithMedia(Agent, Session, "and also this", TextPart());
        var dispatch = hub.LastFollowUpDispatch;
        Assert.NotNull(dispatch);
        await dispatch!;

        queued.ShouldNotBeNull();
        queued!.Content.ShouldContain("and also this");
        queued.Content.ShouldContain(TextAttachmentBody);
    }

    [Fact]
    public async Task FollowUp_WhileRunning_QueuesImagesThroughThePendingMessageQueue()
    {
        AgentUserMessage? queued = null;
        var handle = RunningHandle();
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => queued = m)
            .ReturnsAsync(true);

        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        await hub.FollowUpWithMedia(Agent, Session, "see attached", ImagePart());
        var dispatch = hub.LastFollowUpDispatch;
        Assert.NotNull(dispatch);
        await dispatch!;

        queued.ShouldNotBeNull();
        queued!.Images.ShouldNotBeNull();
        queued.Images!.Count.ShouldBe(1);
        queued.Images[0].Value.ShouldStartWith(ImageBase64Marker);
    }

    [Fact]
    public async Task FollowUp_WhenAgentIdle_DispatchesTheAttachmentAsInboundContentParts()
    {
        // The idle branch bypasses the queue and becomes an ordinary inbound message. Previously
        // it called DispatchMessageAsync, which has no contentParts argument at all - so the parts
        // were dropped on this branch even after the queue branch was fixed.
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            orchestrator: orchestrator);

        await hub.FollowUpWithMedia(Agent, Session, "idle follow-up", TextPart());
        var dispatch = hub.LastFollowUpDispatch;
        Assert.NotNull(dispatch);
        await dispatch!;

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.ContentParts.ShouldNotBeNull();
        var text = dispatched.ContentParts!.OfType<TextContentPart>().ShouldHaveSingleItem();
        text.Text.ShouldBe(TextAttachmentBody);
    }

    // ── Guard: no send-family path may discard a non-empty draft list ─────

    [Fact]
    public void SendFamilyHubMethods_AllExposeAContentPartsSeam()
    {
        // AC5: enumerate the send-family entry points on the hub. A future fifth send path added
        // without a content-parts overload would fail here rather than silently dropping files.
        var hubType = typeof(GatewayHub);
        string[] mediaMethods =
        [
            nameof(GatewayHub.SendMessageWithMedia),
            nameof(GatewayHub.SteerWithMedia),
            nameof(GatewayHub.InterruptAndSteerWithMedia),
            nameof(GatewayHub.FollowUpWithMedia),
        ];

        var missing = mediaMethods
            .Where(name => hubType.GetMethod(name) is not { } m
                || !m.GetParameters().Any(p => p.ParameterType == typeof(IReadOnlyList<MediaContentPartDto>)))
            .ToList();

        missing.ShouldBeEmpty();
    }

    /// <summary>
    /// The whole point of #2484's fix: ONE seam, not four private copies. Asserting that the
    /// gateway's own helper still delegates to the shared composer keeps a future edit from
    /// re-forking the folding logic (the #2442 N-private-copies failure mode).
    /// </summary>
    [Fact]
    public void GatewayHostAndComposer_ProduceIdenticalAttachmentFolding()
    {
        MessageContentPart[] parts =
        [
            new TextContentPart { MimeType = "text/plain", Text = TextAttachmentBody },
            new BinaryContentPart { MimeType = "application/pdf", Data = [1, 2, 3, 4, 5], FileName = "report.pdf" },
        ];

        var viaHost = GatewayHost.AppendNonImageAttachments("msg", parts);
        var viaComposer = AgentUserMessageComposer.AppendNonImageAttachments("msg", parts);

        viaHost.ShouldBe(viaComposer);
        viaHost.ShouldContain(TextAttachmentBody);
        viaHost.ShouldContain("report.pdf");
    }
}
