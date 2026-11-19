using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>Recovery may reserve only an empty, non-processing canonical queue.</summary>
public sealed class InboundRecoveryReservationTests
{
    private static InboundMessage Message(string id) => new()
    {
        ChannelType = ChannelKey.From("test"),
        ChannelAddress = ChannelAddress.From(id),
        SenderId = "test",
        Sender = CitizenId.Of(AgentId.From("worker")),
        Content = "bounded continuation",
        RoutingHints = new InboundMessageRoutingHints(null, null, ConversationId.From(id))
    };

    [Fact]
    public async Task Reservation_FencesNormalAdmission_AndPermitsExactlyOneOwnedPost()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome([], false));
        await using var queue = new DefaultInboundMessageOrchestrator(processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var message = Message("c-recovery");
        using var reservation = queue.TryReserveIdle(message);
        reservation.ShouldNotBeNull();
        queue.Post(message).ShouldBeFalse();
        (await queue.PostAsync(message)).ShouldBe(InboundDispatchStatus.Busy);
        queue.TryReserveIdle(message).ShouldBeNull();
        reservation.TryPost(message).ShouldBeTrue();
        reservation.TryPost(message).ShouldBeFalse();
    }

    [Fact]
    public async Task Quiesce_RevokesHeldReservation_AndPreventsPostOrNewReservation()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        await using var queue = new DefaultInboundMessageOrchestrator(processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        using var reservation = queue.TryReserveIdle(Message("c-quiesce"));
        reservation.ShouldNotBeNull();
        queue.TryBeginQuiesce().ShouldBeTrue();
        reservation.TryPost(Message("c-quiesce")).ShouldBeFalse();
        queue.TryReserveIdle(Message("c-quiesce")).ShouldBeNull();
        queue.Post(Message("c-quiesce")).ShouldBeFalse();
    }

    [Fact]
    public async Task OwnedPost_DoesNotProcessBeforeRelease_AndShutdownDrainsHeldPost()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                entered.TrySetResult();
                return Task.FromResult(new InboundProcessingOutcome([], false));
            });
        var queue = new DefaultInboundMessageOrchestrator(processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        using var reservation = queue.TryReserveIdle(Message("c-held"));
        reservation.ShouldNotBeNull();
        reservation.TryPost(Message("c-held")).ShouldBeTrue();
        entered.Task.IsCompleted.ShouldBeFalse("the reservation holds the actual processor gate");
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        entered.Task.IsCompletedSuccessfully.ShouldBeTrue();
        reservation.TryPost(Message("c-held")).ShouldBeFalse();
    }

    [Fact]
    public async Task ActiveHeadAndQueuedSuccessor_RefuseReservation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new InboundProcessingOutcome([], false);
            });
        await using var queue = new DefaultInboundMessageOrchestrator(processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        try
        {
            queue.Post(Message("c-active")).ShouldBeTrue();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            queue.TryReserveIdle(Message("c-active")).ShouldBeNull();
            queue.Post(Message("c-active")).ShouldBeTrue();
            queue.TryReserveIdle(Message("c-active")).ShouldBeNull();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Reservation_CannotPostIntoAnotherConversation_AndDisposalReopensAdmission()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome([], false));
        await using var queue = new DefaultInboundMessageOrchestrator(processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var reservation = queue.TryReserveIdle(Message("c-one"));
        reservation.ShouldNotBeNull();
        reservation.TryPost(Message("c-other")).ShouldBeFalse();
        reservation.Dispose();
        reservation.TryPost(Message("c-one")).ShouldBeFalse();
        queue.Post(Message("c-one")).ShouldBeTrue();
    }
}
