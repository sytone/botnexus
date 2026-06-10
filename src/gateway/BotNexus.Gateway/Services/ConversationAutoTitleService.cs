using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Services;

/// <summary>
/// Generates a short descriptive title for a conversation after the first user+assistant
/// exchange, if the title is still at the default value "New conversation".
/// </summary>
/// <remarks>
/// This is a best-effort background service. Title generation failures are logged at
/// Warning level and do not surface as turn failures.
/// </remarks>
public sealed class ConversationAutoTitleService
{
    /// <summary>The default title assigned to new conversations before a user-generated title exists.</summary>
    public const string DefaultTitle = "New conversation";

    private readonly IConversationStore _store;
    private readonly IConversationChangeNotifier? _notifier;
    private readonly LlmClient _llmClient;
    private readonly ILogger _logger;

    public ConversationAutoTitleService(
        IConversationStore store,
        LlmClient llmClient,
        ILogger logger,
        IConversationChangeNotifier? notifier = null)
    {
        _store = store;
        _llmClient = llmClient;
        _logger = logger;
        _notifier = notifier;
    }

    /// <summary>
    /// Attempts to auto-generate and persist a conversation title, best-effort.
    /// The method returns immediately without waiting for the background work to complete.
    /// </summary>
    /// <param name="conversationId">Conversation to title.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="userText">The first user message text.</param>
    /// <param name="assistantText">The first assistant response text.</param>
    /// <param name="preferredModelId">Optional model ID from auxiliary.titling config.</param>
    public void TriggerBestEffort(
        ConversationId conversationId,
        AgentId agentId,
        string userText,
        string assistantText,
        string? preferredModelId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateAndSaveAsync(
                    conversationId, agentId, userText, assistantText, preferredModelId,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Auto-title generation failed for conversation '{ConversationId}' (best-effort, ignoring)",
                    conversationId);
            }
        });
    }

    /// <summary>
    /// Generates and saves the conversation title. Returns the generated title, or null when
    /// the conversation already has a custom title or the LLM returned an empty result.
    /// </summary>
    internal async Task<string?> GenerateAndSaveAsync(
        ConversationId conversationId,
        AgentId agentId,
        string userText,
        string assistantText,
        string? preferredModelId,
        CancellationToken ct)
    {
        // Load conversation and check that it still has the default title.
        var conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsDefaultTitle(conversation.Title))
            return null;

        // Resolve model (preferred titling model, then any registered fallback).
        LlmModel? model;
        try
        {
            model = ResolveModel(preferredModelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Auto-title: no model available for conversation '{ConversationId}'",
                conversationId);
            return null;
        }

        // Call LLM.
        var prompt = BuildPrompt(userText, assistantText);
        var context = new Context(
            SystemPrompt: null,
            Messages: [new UserMessage(new UserMessageContent(prompt), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

        AssistantMessage completion;
        try
        {
            completion = await _llmClient
                .CompleteSimpleAsync(model, context)
                .WaitAsync(TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Auto-title: LLM call failed for conversation '{ConversationId}'",
                conversationId);
            return null;
        }

        var rawTitle = string.Join(
            " ",
            completion.Content
                .OfType<TextContent>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        var title = SanitizeTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Re-read and guard again in case another path already set a custom title.
        conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsDefaultTitle(conversation.Title))
            return null;

        conversation.Title = title;
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);

        if (_notifier is not null)
        {
            try
            {
                await _notifier.NotifyConversationChangedAsync(
                    "updated", agentId.Value, conversationId.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Notification failure must not fail the title save.
                _logger.LogWarning(
                    ex,
                    "Auto-title: SignalR notification failed for conversation '{ConversationId}'",
                    conversationId);
            }
        }

        _logger.LogInformation(
            "Auto-title set to '{Title}' for conversation '{ConversationId}' (model {ModelId})",
            title, conversationId, model.Id);

        return title;
    }

    /// <summary>Returns true when the title is still at the default "New conversation" value.</summary>
    public static bool IsDefaultTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ||
           string.Equals(title, DefaultTitle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the entry is a live conversation entry that should be counted
    /// for auto-title guard logic. Uses <see cref="SessionContextProjector.IsVisibleInLiveContext"/>
    /// as the base filter (excludes historical, crash sentinels, notifications), then further
    /// excludes tool results, compaction summaries, and system entries since only user and
    /// assistant messages matter for first-exchange detection.
    /// </summary>
    public static bool IsLiveConversationEntry(SessionEntry entry)
        => SessionContextProjector.IsVisibleInLiveContext(entry)
           && !entry.IsCompactionSummary
           && entry.Role != MessageRole.Tool
           && entry.Role != MessageRole.System;

    /// <summary>
    /// Evaluates the session history to determine if this is the first user+assistant exchange.
    /// Returns the user text and the last non-empty assistant text if the guard passes, or
    /// (null, null) if auto-title should not fire.
    /// </summary>
    public static (string? UserText, string? AssistantText) ShouldTriggerAutoTitle(
        IReadOnlyList<SessionEntry> history)
    {
        var liveEntries = history.Where(IsLiveConversationEntry).ToList();

        var userEntries = liveEntries.Where(e => e.Role == MessageRole.User).ToList();
        var assistantEntries = liveEntries.Where(e => e.Role == MessageRole.Assistant).ToList();

        // Only fire on the first exchange: exactly 1 user entry + at least 1 assistant entry.
        if (userEntries.Count != 1 || assistantEntries.Count < 1)
            return (null, null);

        var userText = userEntries[0].Content;
        // Pick the last assistant entry with non-empty content (handles tool-call turns
        // where intermediate assistant entries have empty content).
        var assistantText = assistantEntries
            .LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Content))?.Content
            ?? assistantEntries[^1].Content;

        return (userText, assistantText);
    }

    internal static string BuildPrompt(string userText, string assistantText)
        => $"In 5 words or fewer, give a descriptive title for this conversation based on the " +
           $"first user message and assistant response. Return only the title, no punctuation, " +
           $"no quotes.\n\nUser: {userText}\n\nAssistant: {assistantText}";

    internal static string SanitizeTitle(string raw)
    {
        var sanitised = raw
            .Replace("\"", "")
            .Replace("'", "")
            .Trim();
        // Limit to 80 chars defensively.
        return sanitised.Length > 80 ? sanitised[..80].TrimEnd() : sanitised;
    }

    private LlmModel ResolveModel(string? preferredModelId)
    {
        if (!string.IsNullOrWhiteSpace(preferredModelId))
        {
            var preferred = FindModel(preferredModelId);
            if (preferred is not null)
                return preferred;

            _logger.LogWarning(
                "Auto-title: configured titling model '{ModelId}' not found; falling back to first available",
                preferredModelId);
        }

        // Fall back to first available model.
        var fallback = _llmClient.Models
            .GetProviders()
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => _llmClient.Models.GetModels(p))
            .FirstOrDefault();

        return fallback
               ?? throw new InvalidOperationException("No models are registered for auto-title generation.");
    }

    private LlmModel? FindModel(string modelId)
    {
        foreach (var provider in _llmClient.Models.GetProviders())
        {
            var m = _llmClient.Models.GetModels(provider)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is not null)
                return m;
        }
        return null;
    }
}
