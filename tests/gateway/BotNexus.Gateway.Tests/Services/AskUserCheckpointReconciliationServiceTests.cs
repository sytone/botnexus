using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Services;

/// <summary>
/// Verifies startup reconciliation (issue #2047): durable ask_user checkpoints are rehydrated into
/// the response registry so an inbound answer after a restart is recognised rather than
/// mis-dispatched as a fresh turn.
/// </summary>
public sealed class AskUserCheckpointReconciliationServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task StartAsync_RehydratesPendingCheckpointsIntoRegistry()
    {
        var store = new InMemoryConversationStore();
        var registry = new AskUserResponseRegistry();

        var withPrompt = ConversationId.From("conv-pending");
        await store.CreateAsync(new Conversation
        {
            ConversationId = withPrompt,
            AgentId = AgentId.From("agent-a"),
            Title = "pending",
            PendingAskUserJson = SerializeRequest(withPrompt, "req-1")
        });

        var withoutPrompt = ConversationId.From("conv-clean");
        await store.CreateAsync(new Conversation
        {
            ConversationId = withoutPrompt,
            AgentId = AgentId.From("agent-a"),
            Title = "clean"
        });

        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        registry.TryGetPendingRequestId(withPrompt, out var requestId).ShouldBeTrue();
        requestId.ShouldBe("req-1");
        registry.TryGetPendingRequestId(withoutPrompt, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_SkipsUnparseableCheckpointWithoutThrowing()
    {
        var store = new InMemoryConversationStore();
        var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conv-corrupt");
        await store.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("agent-a"),
            Title = "corrupt",
            PendingAskUserJson = "not json ]["
        });

        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
        registry.TryGetPendingRequestId(conversationId, out _).ShouldBeFalse();
    }

    private static string SerializeRequest(ConversationId conversationId, string requestId)
        => JsonSerializer.Serialize(new AskUserRequest
        {
            RequestId = requestId,
            ConversationId = conversationId,
            SessionId = SessionId.From("session-old"),
            AgentId = AgentId.From("agent-a"),
            Prompt = "restored prompt"
        }, JsonOptions);
}
