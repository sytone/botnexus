using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Default <see cref="IStartConversationService"/>: composes the existing conversation-create,
/// per-conversation-override and send plumbing into the single ordered workflow described by issue #2036.
/// </summary>
public sealed class StartConversationService : IStartConversationService
{
    private readonly IAgentInteractionService _interaction;
    private readonly IGatewayRestClient _restClient;
    private readonly ILogger<StartConversationService> _logger;

    /// <summary>Creates the orchestrator over the existing portal interaction and REST seams.</summary>
    public StartConversationService(
        IAgentInteractionService interaction,
        IGatewayRestClient restClient,
        ILogger<StartConversationService> logger)
    {
        _interaction = interaction;
        _restClient = restClient;
        _logger = logger;
    }

    /// <summary>
    /// Strips CR/LF from client-supplied identifiers before they reach a log template so a crafted
    /// agent id cannot forge additional log lines (CodeQL: cs/log-forging).
    /// </summary>
    private static string? Sanitise(string? value) =>
        value?.Replace("\r", string.Empty, StringComparison.Ordinal)
              .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// Decides whether a UI model selection is a real override. Blank means "agent default", and a
    /// selection equal to the agent's default is not an override either - in both cases nothing is
    /// persisted so the conversation simply inherits the agent default.
    /// </summary>
    private static string? ResolveOverride(string? selectedModel, string? agentDefaultModel)
    {
        if (string.IsNullOrWhiteSpace(selectedModel)) return null;
        var selected = selectedModel.Trim();
        if (!string.IsNullOrWhiteSpace(agentDefaultModel)
            && string.Equals(selected, agentDefaultModel.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return selected;
    }

    /// <inheritdoc />
    public async Task<StartConversationResult> StartAsync(
        StartConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AgentId))
            return StartConversationResult.Failed("No agent selected.");
        if (string.IsNullOrWhiteSpace(request.FirstMessage))
            return StartConversationResult.Failed("Enter a message to start the conversation.");

        // 1. Create the conversation. select: true makes it the agent's active conversation, which is
        //    what the shared send path targets - so step 3 provably lands on this conversation.
        string? conversationId;
        try
        {
            conversationId = await _interaction.CreateConversationAsync(request.AgentId, title: null, select: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartConversation: create failed for {AgentId}", Sanitise(request.AgentId));
            return StartConversationResult.Failed($"Failed to create conversation: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(conversationId))
            return StartConversationResult.Failed("Failed to create conversation.");

        // 2. Persist a non-default model selection as the conversation override BEFORE sending. The
        //    override is per-conversation and durable (not a one-shot for the first message), so every
        //    subsequent turn in this conversation also runs on the selected model.
        var overrideModel = ResolveOverride(request.SelectedModel, request.AgentDefaultModel);
        var overrideThinking = string.IsNullOrWhiteSpace(request.SelectedThinking) ? null : request.SelectedThinking.Trim();
        var overrideContext = request.SelectedContextWindow;
        if (overrideModel is not null || overrideThinking is not null || overrideContext is not null)
        {
            try
            {
                var updated = await _restClient.SetConversationOverrideAsync(
                    conversationId,
                    new SetConversationOverrideRequestDto(
                        Model: overrideModel,
                        Thinking: overrideThinking,
                        ContextWindow: overrideContext),
                    cancellationToken).ConfigureAwait(false);

                if (updated is null)
                {
                    // Fail closed: sending now would silently run the first turn on the agent default,
                    // which is not what the citizen asked for.
                    return StartConversationResult.Failed("Failed to apply the selected model to the new conversation.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StartConversation: override failed for {ConversationId}", Sanitise(conversationId));
                return StartConversationResult.Failed($"Failed to apply the selected model: {ex.Message}");
            }
        }

        // 3. Send the citizen's first message into the conversation created above.
        try
        {
            await _interaction.SendMessageAsync(request.AgentId, conversationId, request.FirstMessage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartConversation: send failed for {ConversationId}", Sanitise(conversationId));
            return StartConversationResult.Failed($"Failed to send the first message: {ex.Message}");
        }

        // 4. Return identity for navigation to /chat/{agentId}/{conversationId}.
        return StartConversationResult.Started(request.AgentId, conversationId, overrideModel);
    }
}
