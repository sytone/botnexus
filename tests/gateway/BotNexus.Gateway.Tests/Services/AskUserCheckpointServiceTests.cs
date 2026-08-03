using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Services;

/// <summary>
/// Covers the durable, resumable ask_user checkpoint (issue #2047): live completion, restart-safe
/// resume reconstructed from persisted <see cref="Conversation.PendingAskUserJson"/>, explicit
/// cancel, idempotent duplicate/cross-client submission, stale request id, and legacy/orphan
/// reconciliation that must not swallow ordinary messages.
/// </summary>
public sealed class AskUserCheckpointServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task ResolveAsync_WithLiveWaiter_CompletesInProcess()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-live");
        var (requestId, task) = registry.Register(conversationId, timeout: null);
        var service = CreateService(registry, store, out var resumer);

        var outcome = await service.ResolveAsync(
            conversationId, requestId, Answer(requestId, "staging"));

        outcome.ShouldBe(AskUserResolveOutcome.LiveCompleted);
        var response = await task;
        response.FreeFormText.ShouldBe("staging");
        resumer.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_AfterRestart_ResumesFromDurableCheckpoint()
    {
        // Simulate a gateway restart: the durable prompt survives on the conversation row but the
        // original in-memory waiter (and its TaskCompletionSource) no longer exists.
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-restart");
        var requestId = "req-restart-1";
        await PersistCheckpointAsync(store, conversationId, requestId, "Which env?");

        var service = CreateService(registry, store, out var resumer);

        var outcome = await service.ResolveAsync(
            conversationId, requestId, Answer(requestId, "prod"));

        outcome.ShouldBe(AskUserResolveOutcome.ResumedFromCheckpoint);
        // The checkpoint is atomically cleared as part of the claim.
        (await store.GetAsync(conversationId))!.PendingAskUserJson.ShouldBeNull();
        // Exactly one continuation was dispatched from the reconstructed request.
        resumer.Invocations.Count.ShouldBe(1);
        resumer.Invocations[0].Request.RequestId.ShouldBe(requestId);
        resumer.Invocations[0].Response.FreeFormText.ShouldBe("prod");
    }

    [Fact]
    public async Task ResolveAsync_CancelAfterRestart_ResumesWithCancellation()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-cancel");
        var requestId = "req-cancel-1";
        await PersistCheckpointAsync(store, conversationId, requestId, "Continue?");

        var service = CreateService(registry, store, out var resumer);

        var outcome = await service.ResolveAsync(
            conversationId, requestId, new AskUserResponse { RequestId = requestId, WasCancelled = true });

        outcome.ShouldBe(AskUserResolveOutcome.ResumedFromCheckpoint);
        (await store.GetAsync(conversationId))!.PendingAskUserJson.ShouldBeNull();
        resumer.Invocations.Count.ShouldBe(1);
        resumer.Invocations[0].Response.WasCancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_DuplicateSubmission_IsIdempotent()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-dupe");
        var requestId = "req-dupe-1";
        await PersistCheckpointAsync(store, conversationId, requestId, "Pick one");

        var service = CreateService(registry, store, out var resumer);

        var first = await service.ResolveAsync(conversationId, requestId, Answer(requestId, "a"));
        var second = await service.ResolveAsync(conversationId, requestId, Answer(requestId, "a"));

        first.ShouldBe(AskUserResolveOutcome.ResumedFromCheckpoint);
        // The second submission finds no pending checkpoint (already claimed) and does not resume again.
        second.ShouldBe(AskUserResolveOutcome.NoPendingCheckpoint);
        resumer.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentSubmissions_ResumeExactlyOnce()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-race");
        var requestId = "req-race-1";
        await PersistCheckpointAsync(store, conversationId, requestId, "Race");

        var service = CreateService(registry, store, out var resumer);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                service.ResolveAsync(conversationId, requestId, Answer(requestId, "x"))));

        results.Count(r => r == AskUserResolveOutcome.ResumedFromCheckpoint).ShouldBe(1);
        resumer.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_StaleRequestId_LeavesCheckpointPending()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-stale");
        await PersistCheckpointAsync(store, conversationId, "req-current", "Current prompt");

        var service = CreateService(registry, store, out var resumer);

        var outcome = await service.ResolveAsync(
            conversationId, "req-old", Answer("req-old", "late"));

        outcome.ShouldBe(AskUserResolveOutcome.RequestIdMismatch);
        // The live prompt survives so the correct answer can still resolve it.
        (await store.GetAsync(conversationId))!.PendingAskUserJson.ShouldNotBeNull();
        resumer.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_NoPendingCheckpoint_IsNoOp()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-none");
        var service = CreateService(registry, store, out var resumer);

        var outcome = await service.ResolveAsync(
            conversationId, "req-x", Answer("req-x", "hello"));

        outcome.ShouldBe(AskUserResolveOutcome.NoPendingCheckpoint);
        resumer.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryResolveInboundTextAsync_AfterRestart_CapturesAnswerAndDoesNotSwallowOrdinary()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();

        // Conversation A has a durable pending prompt (post-restart, no live waiter).
        var withPrompt = await SeedAsync(store, "conv-with-prompt");
        await PersistCheckpointAsync(store, withPrompt, "req-a", "Answer me");

        // Conversation B has no prompt - ordinary text must pass through untouched.
        var noPrompt = await SeedAsync(store, "conv-no-prompt");

        var service = CreateService(registry, store, out var resumer);

        var captured = await service.TryResolveInboundTextAsync(withPrompt, "the answer");
        var passthrough = await service.TryResolveInboundTextAsync(noPrompt, "just chatting");

        captured.ShouldBeTrue();
        passthrough.ShouldBeFalse();
        resumer.Invocations.Count.ShouldBe(1);
        resumer.Invocations[0].Response.FreeFormText.ShouldBe("the answer");
        (await store.GetAsync(withPrompt))!.PendingAskUserJson.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_LegacyUnparseableCheckpoint_IsClearedNotResumed()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        var conversationId = await SeedAsync(store, "conv-legacy");
        var conversation = (await store.GetAsync(conversationId))!;
        conversation.PendingAskUserJson = "{ this is not valid ask user json ]";
        await store.SaveAsync(conversation);

        var service = CreateService(registry, store, out var resumer);

        var outcome = await service.ResolveAsync(conversationId, "req-any", Answer("req-any", "hi"));

        outcome.ShouldBe(AskUserResolveOutcome.NoPendingCheckpoint);
        // Corrupt row is reconciled (cleared) so it never swallows future ordinary messages.
        (await store.GetAsync(conversationId))!.PendingAskUserJson.ShouldBeNull();
        resumer.Invocations.ShouldBeEmpty();
    }

    private static AskUserCheckpointService CreateService(
        AskUserResponseRegistry registry,
        InMemoryConversationStore store,
        out RecordingResumer resumer)
    {
        resumer = new RecordingResumer();
        // The checkpoint service resolves live waiters through the #2322 resolver seam rather than
        // touching the registry directly, so the real resolver is wired over the real registry here.
        var resolver = new AskUserPromptResolver(registry, NullLogger<AskUserPromptResolver>.Instance);
        return new AskUserCheckpointService(
            resolver,
            store,
            NullLogger<AskUserCheckpointService>.Instance,
            resumer);
    }

    private static async Task<ConversationId> SeedAsync(InMemoryConversationStore store, string id)
    {
        var conversationId = ConversationId.From(id);
        await store.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("agent-a"),
            Title = "checkpoint convo"
        });
        return conversationId;
    }

    private static async Task PersistCheckpointAsync(
        InMemoryConversationStore store,
        ConversationId conversationId,
        string requestId,
        string prompt)
    {
        var request = new AskUserRequest
        {
            RequestId = requestId,
            ConversationId = conversationId,
            SessionId = SessionId.From("session-old"),
            AgentId = AgentId.From("agent-a"),
            Prompt = prompt
        };
        var conversation = (await store.GetAsync(conversationId))!;
        conversation.PendingAskUserJson = JsonSerializer.Serialize(request, JsonOptions);
        await store.SaveAsync(conversation);
    }

    private static AskUserResponse Answer(string requestId, string text)
        => new() { RequestId = requestId, FreeFormText = text };

    private sealed class RecordingResumer : IAskUserCheckpointResumer
    {
        private readonly List<(AskUserRequest Request, AskUserResponse Response)> _invocations = [];
        private readonly object _lock = new();

        public IReadOnlyList<(AskUserRequest Request, AskUserResponse Response)> Invocations
        {
            get { lock (_lock) { return _invocations.ToList(); } }
        }

        public Task ResumeAsync(AskUserRequest request, AskUserResponse response, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _invocations.Add((request, response));
            }
            return Task.CompletedTask;
        }
    }
}
