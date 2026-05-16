using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;

namespace BotNexus.Gateway.Services;

/// <summary>
/// Tracks pending <c>ask_user</c> waits so channel responses can complete blocked tool calls
/// without re-entering the session queue.
/// </summary>
public sealed class AskUserResponseRegistry : IAskUserResponseRegistry, IDisposable
{
    private readonly ConcurrentDictionary<string, PendingAskUserResponse> _pendingByRequestId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _requestIdByConversation = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public (string RequestId, Task<AskUserResponse> Task) Register(ConversationId conversationId, TimeSpan? timeout)
    {
        var conversationKey = NormalizeConversationId(conversationId);
        if (_requestIdByConversation.ContainsKey(conversationKey))
            throw new InvalidOperationException("Another ask_user request is already pending for this conversation.");

        while (true)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var pending = new PendingAskUserResponse(
                conversationId,
                requestId,
                new TaskCompletionSource<AskUserResponse>(TaskCreationOptions.RunContinuationsAsynchronously));

            if (!_requestIdByConversation.TryAdd(conversationKey, requestId))
            {
                if (_requestIdByConversation.ContainsKey(conversationKey))
                    throw new InvalidOperationException("Another ask_user request is already pending for this conversation.");
                continue;
            }

            if (!_pendingByRequestId.TryAdd(requestId, pending))
            {
                _requestIdByConversation.TryRemove(conversationKey, out _);
                continue;
            }

            if (timeout is { } timeoutValue && timeoutValue > TimeSpan.Zero)
            {
                pending.TimeoutCts = new CancellationTokenSource(timeoutValue);
                pending.TimeoutRegistration = pending.TimeoutCts.Token.Register(() => HandleTimeout(pending));
            }

            return (requestId, pending.Completion.Task);
        }
    }

    /// <inheritdoc />
    public bool TryComplete(ConversationId conversationId, string requestId, AskUserResponse response)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        if (!_pendingByRequestId.TryGetValue(requestId, out var pending))
            return false;

        if (!pending.ConversationId.Equals(conversationId))
            return false;

        if (!TryRemovePending(requestId, out var removed))
            return false;

        removed.Completion.TrySetResult(response);
        DisposePending(removed);
        return true;
    }

    /// <inheritdoc />
    public void Cancel(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        if (!TryRemovePending(requestId, out var pending))
            return;

        pending.Completion.TrySetCanceled();
        DisposePending(pending);
    }

    /// <inheritdoc />
    public void CancelAllForConversation(ConversationId conversationId)
    {
        if (!_requestIdByConversation.TryRemove(NormalizeConversationId(conversationId), out var requestId))
            return;

        if (_pendingByRequestId.TryRemove(requestId, out var pending))
        {
            pending.Completion.TrySetCanceled();
            DisposePending(pending);
        }
    }

    /// <inheritdoc />
    public bool TryGetPendingRequestId(ConversationId conversationId, out string requestId)
        => _requestIdByConversation.TryGetValue(NormalizeConversationId(conversationId), out requestId!);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var requestId in _pendingByRequestId.Keys)
            Cancel(requestId);
    }

    private void HandleTimeout(PendingAskUserResponse pending)
    {
        if (!TryRemovePending(pending.RequestId, out var removed))
            return;

        removed.Completion.TrySetResult(new AskUserResponse
        {
            RequestId = removed.RequestId,
            WasTimeout = true
        });
        DisposePending(removed);
    }

    private bool TryRemovePending(string requestId, out PendingAskUserResponse pending)
    {
        if (!_pendingByRequestId.TryRemove(requestId, out pending!))
            return false;

        _requestIdByConversation.TryRemove(NormalizeConversationId(pending.ConversationId), out _);
        return true;
    }

    private static void DisposePending(PendingAskUserResponse pending)
    {
        pending.TimeoutRegistration?.Dispose();
        pending.TimeoutCts?.Dispose();
    }

    private static string NormalizeConversationId(ConversationId conversationId)
        => conversationId.Value;

    private sealed class PendingAskUserResponse(
        ConversationId conversationId,
        string requestId,
        TaskCompletionSource<AskUserResponse> completion)
    {
        public ConversationId ConversationId { get; } = conversationId;
        public string RequestId { get; } = requestId;
        public TaskCompletionSource<AskUserResponse> Completion { get; } = completion;
        public CancellationTokenSource? TimeoutCts { get; set; }
        public CancellationTokenRegistration? TimeoutRegistration { get; set; }
    }
}
