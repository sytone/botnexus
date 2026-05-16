using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Services;

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
        using var registry = new AskUserResponseRegistry();
        var pending = registry.Register(ConversationId.From("conversation-7"), TimeSpan.FromMilliseconds(50));

        var result = await pending.Task.WaitAsync(TimeSpan.FromSeconds(2));
        result.RequestId.ShouldBe(pending.RequestId);
        result.WasTimeout.ShouldBeTrue();
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
