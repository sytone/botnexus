using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Time.Testing;

namespace BotNexus.Gateway.Tests.Services;

public sealed class AskUserResponseRegistryTests
{
    [Fact]
    public void Register_RequiresSinglePendingPerConversation()
    {
        using var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conversation-1");
        _ = registry.Register(conversationId, timeout: null);

        Should.Throw<InvalidOperationException>(() => registry.Register(conversationId, timeout: null));
    }

    [Fact]
    public async Task Register_CreatesCompletableTask()
    {
        using var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conversation-2");
        var pending = registry.Register(conversationId, timeout: null);
        var response = CreateResponse(pending.RequestId, freeFormText: "approved");

        var completed = registry.TryComplete(conversationId, pending.RequestId, response);
        var result = await pending.Task;

        completed.ShouldBeTrue();
        result.FreeFormText.ShouldBe("approved");
    }

    [Fact]
    public void TryComplete_WithWrongConversationId_ReturnsFalse()
    {
        using var registry = new AskUserResponseRegistry();
        var pending = registry.Register(ConversationId.From("conversation-3"), timeout: null);

        var completed = registry.TryComplete(
            ConversationId.From("conversation-4"),
            pending.RequestId,
            CreateResponse(pending.RequestId));

        completed.ShouldBeFalse();
        pending.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task CancelAllForConversation_CancelsPendingOnlyForTargetConversation()
    {
        using var registry = new AskUserResponseRegistry();
        var firstConversation = ConversationId.From("conversation-5");
        var secondConversation = ConversationId.From("conversation-6");
        var first = registry.Register(firstConversation, timeout: null);
        var second = registry.Register(secondConversation, timeout: null);

        registry.CancelAllForConversation(firstConversation);

        await Should.ThrowAsync<TaskCanceledException>(async () => await first.Task);
        second.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_WithTimeout_CompletesWithTimeoutPayload()
    {
        var timeProvider = new FakeTimeProvider();
        using var registry = new AskUserResponseRegistry(timeProvider);
        var conversationId = ConversationId.From("conversation-7");
        var pending = registry.Register(conversationId, TimeSpan.FromMilliseconds(50));

        pending.Task.IsCompleted.ShouldBeFalse();
        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        pending.Task.IsCompleted.ShouldBeFalse();
        registry.TryGetPendingRequestId(conversationId, out var requestId).ShouldBeTrue();
        requestId.ShouldBe(pending.RequestId);

        // Advance runs due timer callbacks; assert completion before awaiting so a broken
        // clock seam fails immediately instead of hanging or racing a wall-clock deadline.
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        pending.Task.IsCompletedSuccessfully.ShouldBeTrue();
        var result = await pending.Task;
        result.RequestId.ShouldBe(pending.RequestId);
        result.WasTimeout.ShouldBeTrue();
        result.WasCancelled.ShouldBeFalse();
        result.FreeFormText.ShouldBeNull();
        result.SelectedValues.ShouldBeNull();
        registry.TryGetPendingRequestId(conversationId, out _).ShouldBeFalse();
        registry.TryComplete(conversationId, pending.RequestId, CreateResponse(pending.RequestId)).ShouldBeFalse();

        var replacement = registry.Register(conversationId, timeout: null);
        replacement.RequestId.ShouldNotBe(pending.RequestId);
        registry.TryGetPendingRequestId(conversationId, out var replacementId).ShouldBeTrue();
        replacementId.ShouldBe(replacement.RequestId);
        replacement.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task TryComplete_BeforeTimeout_PreservesResponseAndReplacement()
    {
        var timeProvider = new FakeTimeProvider();
        using var registry = new AskUserResponseRegistry(timeProvider);
        var conversationId = ConversationId.From("conversation-response-before-timeout");
        var pending = registry.Register(conversationId, TimeSpan.FromMilliseconds(50));
        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        pending.Task.IsCompleted.ShouldBeFalse();
        var response = CreateResponse(pending.RequestId, freeFormText: "approved", selectedValues: ["yes"]);

        registry.TryComplete(conversationId, pending.RequestId, response).ShouldBeTrue();
        pending.Task.IsCompletedSuccessfully.ShouldBeTrue();
        registry.TryGetPendingRequestId(conversationId, out _).ShouldBeFalse();
        var replacement = registry.Register(conversationId, timeout: null);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var result = await pending.Task;
        result.ShouldBeSameAs(response);
        result.WasTimeout.ShouldBeFalse();
        result.WasCancelled.ShouldBeFalse();
        replacement.Task.IsCompleted.ShouldBeFalse();
        registry.TryGetPendingRequestId(conversationId, out var replacementId).ShouldBeTrue();
        replacementId.ShouldBe(replacement.RequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_BeforeTimeout_PreservesCancellationAndReplacement(bool cancelAll)
    {
        var timeProvider = new FakeTimeProvider();
        using var registry = new AskUserResponseRegistry(timeProvider);
        var conversationId = ConversationId.From("conversation-cancel-before-timeout");
        var pending = registry.Register(conversationId, TimeSpan.FromMilliseconds(50));
        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        pending.Task.IsCompleted.ShouldBeFalse();

        if (cancelAll)
            registry.CancelAllForConversation(conversationId);
        else
            registry.Cancel(pending.RequestId);

        pending.Task.IsCanceled.ShouldBeTrue();
        registry.TryGetPendingRequestId(conversationId, out _).ShouldBeFalse();
        var replacement = registry.Register(conversationId, timeout: null);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        await Should.ThrowAsync<TaskCanceledException>(async () => await pending.Task);
        registry.TryComplete(conversationId, pending.RequestId, CreateResponse(pending.RequestId)).ShouldBeFalse();
        replacement.Task.IsCompleted.ShouldBeFalse();
        registry.TryGetPendingRequestId(conversationId, out var replacementId).ShouldBeTrue();
        replacementId.ShouldBe(replacement.RequestId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Register_WithoutPositiveTimeout_RemainsPendingUntilResponse(int? timeoutMilliseconds)
    {
        var timeProvider = new FakeTimeProvider();
        using var registry = new AskUserResponseRegistry(timeProvider);
        var conversationId = ConversationId.From("conversation-without-timeout");
        TimeSpan? timeout = timeoutMilliseconds is { } milliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
        var pending = registry.Register(conversationId, timeout);

        timeProvider.Advance(TimeSpan.FromDays(1));

        pending.Task.IsCompleted.ShouldBeFalse();
        registry.TryGetPendingRequestId(conversationId, out var requestId).ShouldBeTrue();
        requestId.ShouldBe(pending.RequestId);
        var response = CreateResponse(pending.RequestId);
        registry.TryComplete(conversationId, pending.RequestId, response).ShouldBeTrue();
        (await pending.Task).ShouldBeSameAs(response);
        registry.TryGetPendingRequestId(conversationId, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_MultipleConcurrent_AllIndependent()
    {
        using var registry = new AskUserResponseRegistry();
        var requests = Enumerable.Range(0, 20)
            .Select(index => new
            {
                Index = index,
                ConversationId = ConversationId.From($"conversation-{index}"),
                Pending = registry.Register(ConversationId.From($"conversation-{index}"), timeout: null)
            })
            .ToList();

        await Task.WhenAll(requests.Select(request => Task.Run(() =>
            registry.TryComplete(
                request.ConversationId,
                request.Pending.RequestId,
                CreateResponse(request.Pending.RequestId, freeFormText: $"response-{request.Index}")))));

        var results = await Task.WhenAll(requests.Select(request => request.Pending.Task));
        for (var index = 0; index < results.Length; index++)
            results[index].FreeFormText.ShouldBe($"response-{index}");
    }

    [Fact]
    public void Rehydrate_MakesConversationRequestIdDiscoverable()
    {
        using var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conversation-rehydrate");

        registry.Rehydrate(conversationId, "req-rehydrated").ShouldBeTrue();

        registry.TryGetPendingRequestId(conversationId, out var requestId).ShouldBeTrue();
        requestId.ShouldBe("req-rehydrated");
    }

    [Fact]
    public void Rehydrate_IsNotCompletable_SoDurableCheckpointOwnsResolution()
    {
        using var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conversation-rehydrate-2");
        registry.Rehydrate(conversationId, "req-rehydrated");

        // A rehydrated entry has no in-memory waiter; TryComplete must not resolve it (that would
        // silently drop the answer instead of resuming from the durable checkpoint). #2047
        registry.TryComplete(conversationId, "req-rehydrated", CreateResponse("req-rehydrated")).ShouldBeFalse();
    }

    [Fact]
    public void Rehydrate_IsIdempotent_WhenEntryAlreadyExists()
    {
        using var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conversation-rehydrate-3");
        registry.Register(conversationId, timeout: null);

        registry.Rehydrate(conversationId, "req-other").ShouldBeFalse();
    }

    private static AskUserResponse CreateResponse(
        string requestId,
        string? freeFormText = "answer",
        IReadOnlyList<string>? selectedValues = null,
        bool wasCancelled = false,
        bool wasTimeout = false)
        => new()
        {
            RequestId = requestId,
            FreeFormText = freeFormText,
            SelectedValues = selectedValues,
            WasCancelled = wasCancelled,
            WasTimeout = wasTimeout
        };
}
