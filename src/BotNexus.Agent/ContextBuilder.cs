using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent;

/// <summary>
/// Builds the LLM context (messages list) from a session and an inbound message,
/// trimming to stay within the context window token budget.
/// </summary>
public sealed class ContextBuilder
{
    private readonly ILogger<ContextBuilder> _logger;
    private const int ApproxCharsPerToken = 4;

    public ContextBuilder(ILogger<ContextBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds the list of <see cref="ChatMessage"/> objects for the LLM request,
    /// including history (trimmed to context window) and the new user message.
    /// </summary>
    public IReadOnlyList<ChatMessage> Build(
        Core.Models.Session session,
        InboundMessage inboundMessage,
        GenerationSettings settings)
    {
        var maxChars = settings.ContextWindowTokens * ApproxCharsPerToken;
        var messages = new List<ChatMessage>();

        // Add the new user message
        var userMessage = new ChatMessage("user", inboundMessage.Content);
        var budget = maxChars - userMessage.Content.Length;

        // Add history in reverse until we run out of budget
        var historyToInclude = new List<ChatMessage>();
        foreach (var entry in Enumerable.Reverse(session.History))
        {
            var role = entry.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.System => "system",
                MessageRole.Tool => "tool",
                _ => "user"
            };
            var msg = new ChatMessage(role, entry.Content);
            if (budget - msg.Content.Length < 0)
            {
                _logger.LogDebug("Context window budget exhausted at {Count} history entries", historyToInclude.Count);
                break;
            }
            budget -= msg.Content.Length;
            historyToInclude.Insert(0, msg);
        }

        messages.AddRange(historyToInclude);
        messages.Add(userMessage);
        return messages;
    }
}
