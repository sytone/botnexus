using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Services;

namespace BotNexus.Gateway.Tests.Services;

public sealed class AskUserResponseRegistryTests
{
    [Fact]
    public void Register_ReturnsUniqueRequestId()
    {
        var registry = new AskUserResponseRegistry();

        var first = registry.Register("conversation-1", timeout: null);
        var second = registry.Register("conversation-1", timeout: null);

        first.RequestId.ShouldNotBe(second.RequestId);
    }

    [Fact]
    public async Task Register_CreatesCompletableTask()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", timeout: null);
        var response = CreateResponse(pending.RequestId, freeFormText: "approved");

        var completed = registry.TryComplete("conversation-1", pending.RequestId, response);
        var result = await pending.Task;

        completed.ShouldBeTrue();
        result.FreeFormText.ShouldBe("approved");
    }

    [Fact]
    public void TryComplete_WithValidRequestId_CompletesTask()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", timeout: null);
        var response = CreateResponse(pending.RequestId, freeFormText: "deploy");

        var completed = registry.TryComplete("conversation-1", pending.RequestId, response);

        completed.ShouldBeTrue();
    }

    [Fact]
    public void TryComplete_WithInvalidRequestId_ReturnsFalse()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", timeout: null);

        var completed = registry.TryComplete("conversation-1", "missing-request", CreateResponse("missing-request"));

        completed.ShouldBeFalse();
        pending.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void TryComplete_WithWrongConversationId_ReturnsFalse()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", timeout: null);

        var completed = registry.TryComplete("conversation-2", pending.RequestId, CreateResponse(pending.RequestId));

        completed.ShouldBeFalse();
        pending.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_CancelsTask()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", timeout: null);

        registry.Cancel(pending.RequestId);

        await Should.ThrowAsync<TaskCanceledException>(async () => await pending.Task);
    }

    [Fact]
    public async Task CancelAllForConversation_CancelsAllPendingForThatConversation()
    {
        var registry = new AskUserResponseRegistry();
        var first = registry.Register("conversation-1", timeout: null);
        var second = registry.Register("conversation-1", timeout: null);
        var otherConversation = registry.Register("conversation-2", timeout: null);

        registry.CancelAllForConversation("conversation-1");

        await Should.ThrowAsync<TaskCanceledException>(async () => await first.Task);
        await Should.ThrowAsync<TaskCanceledException>(async () => await second.Task);
        otherConversation.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_WithTimeout_AutoCancelsAfterTimeout()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", TimeSpan.FromMilliseconds(75));

        await Should.ThrowAsync<TaskCanceledException>(async () => await pending.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TryComplete_AfterTimeout_ReturnsFalse()
    {
        var registry = new AskUserResponseRegistry();
        var pending = registry.Register("conversation-1", TimeSpan.FromMilliseconds(75));
        await Should.ThrowAsync<TaskCanceledException>(async () => await pending.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        var completed = registry.TryComplete("conversation-1", pending.RequestId, CreateResponse(pending.RequestId));

        completed.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_MultipleConcurrent_AllIndependent()
    {
        var registry = new AskUserResponseRegistry();
        var requests = Enumerable.Range(0, 25)
            .Select(index => new
            {
                Index = index,
                ConversationId = $"conversation-{index % 5}",
                Pending = registry.Register($"conversation-{index % 5}", timeout: null)
            })
            .ToList();

        await Task.WhenAll(requests.Select(request => Task.Run(() =>
            registry.TryComplete(
                request.ConversationId,
                request.Pending.RequestId,
                CreateResponse(request.Pending.RequestId, freeFormText: $"response-{request.Index}")))));

        var results = await Task.WhenAll(requests.Select(r => r.Pending.Task));

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
