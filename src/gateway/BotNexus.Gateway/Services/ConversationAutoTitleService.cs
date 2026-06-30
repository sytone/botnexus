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

    /// <summary>
    /// Resolves the per-provider API-endpoint override (e.g. enterprise vs individual GitHub
    /// Copilot) used to rewrite the resolved titling model's <see cref="LlmModel.BaseUrl"/> before
    /// the <c>CompleteSimpleAsync</c> call. Mirrors the override the live agent path applies in
    /// <c>InProcessIsolationStrategy</c> and the compactor applies after #1635/PR #1638: without it
    /// a model registered with the static individual Copilot BaseUrl is POSTed to the wrong host on
    /// an enterprise account, returns HTTP 421 Misdirected Request, auto-titling silently fails, and
    /// the conversation keeps the default title. Defaults to
    /// <see cref="GatewayAuthManager.GetApiEndpoint"/>; null when no auth manager is available (the
    /// static BaseUrl is then used unchanged).
    /// </summary>
    private readonly Func<string, string?>? _endpointResolver;

    /// <summary>
    /// Creates the auto-title service. When an <paramref name="authManager"/> is supplied the
    /// resolved titling model inherits the same per-provider API-endpoint override the live agent
    /// path applies (#1636); otherwise the model's static BaseUrl is used unchanged.
    /// </summary>
    public ConversationAutoTitleService(
        IConversationStore store,
        LlmClient llmClient,
        ILogger logger,
        IConversationChangeNotifier? notifier = null,
        GatewayAuthManager? authManager = null)
    {
        _store = store;
        _llmClient = llmClient;
        _logger = logger;
        _notifier = notifier;
        // The endpoint override is the same one the live agent path applies; derive it from the
        // auth manager so the titling call targets the correct provider endpoint (#1636).
        _endpointResolver = authManager is not null ? authManager.GetApiEndpoint : null;
    }

    /// <summary>
    /// Test seam (#1636): constructs the service with an explicit endpoint resolver so the
    /// BaseUrl-override behaviour can be unit-tested without a concrete
    /// <see cref="GatewayAuthManager"/> (which reads auth.json). Production code uses the public
    /// constructor, which derives the resolver from the auth manager.
    /// </summary>
    internal ConversationAutoTitleService(
        IConversationStore store,
        LlmClient llmClient,
        ILogger logger,
        IConversationChangeNotifier? notifier,
        Func<string, string?>? endpointResolver)
    {
        _store = store;
        _llmClient = llmClient;
        _logger = logger;
        _notifier = notifier;
        _endpointResolver = endpointResolver;
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
    /// <param name="timeoutSeconds">Per-call timeout in seconds from auxiliary.titling config; non-positive falls back to 30.</param>
    public void TriggerBestEffort(
        ConversationId conversationId,
        AgentId agentId,
        string userText,
        string assistantText,
        string? preferredModelId,
        int timeoutSeconds = 30)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateAndSaveAsync(
                    conversationId, agentId, userText, assistantText, preferredModelId,
                    timeoutSeconds, CancellationToken.None);
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
        int timeoutSeconds,
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
            // A non-positive configured timeout would otherwise cancel the call instantly; clamp
            // to the 30s default so a mis-set zero degrades to default behaviour, not no titling.
            var effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
            completion = await _llmClient
                .CompleteSimpleAsync(model, context)
                .WaitAsync(TimeSpan.FromSeconds(effectiveTimeout), ct)
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
    /// Evaluates the session history to find a user+assistant exchange to title. Returns the
    /// first user text and the last non-empty assistant text when at least one of each exists, or
    /// (null, null) when no exchange is present yet. #1695: this is no longer one-shot - it fires
    /// whenever an exchange exists, and re-titling stays gated on the default title in
    /// GenerateAndSaveAsync so a custom title is never overwritten. The optional logger records an
    /// INFO skip when the guard does not fire, so stuck-on-default conversations are diagnosable.
    /// </summary>
    public static (string? UserText, string? AssistantText) ShouldTriggerAutoTitle(
        IReadOnlyList<SessionEntry> history,
        ILogger? logger = null)
    {
        var liveEntries = history.Where(IsLiveConversationEntry).ToList();

        var userEntries = liveEntries.Where(e => e.Role == MessageRole.User).ToList();
        var assistantEntries = liveEntries.Where(e => e.Role == MessageRole.Assistant).ToList();

        // #1695: fire on any turn that still has at least one user + one assistant entry, not just
        // the one-shot first exchange. The old equals-1 user guard permanently disqualified a
        // conversation once it reached a second user turn, leaving it stuck on the default title.
        // Re-titling stays gated on the default title in GenerateAndSaveAsync, so firing on a later
        // turn only ever re-titles a still-default-titled conversation.
        if (userEntries.Count < 1 || assistantEntries.Count < 1)
        {
            // Log at INFO so the no-fire path is diagnosable: previously a count mismatch skipped
            // silently and gave no signal for conversations stuck on the default title.
            logger?.LogInformation(
                "Auto-title guard skipped: count mismatch (user={UserCount}, assistant={AssistantCount}); first user+assistant exchange not yet present",
                userEntries.Count, assistantEntries.Count);
            return (null, null);
        }

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
                return ApplyEndpointOverride(preferred, _endpointResolver);

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

        if (fallback is null)
            throw new InvalidOperationException("No models are registered for auto-title generation.");

        // #1636: rewrite the resolved model's BaseUrl to the per-provider endpoint override (e.g.
        // enterprise vs individual GitHub Copilot) so the titling request targets the same host the
        // live agent path uses. Applied at the single ResolveModel funnel so both the preferred and
        // fallback models inherit the override uniformly.
        return ApplyEndpointOverride(fallback, _endpointResolver);
    }

    /// <summary>
    /// Applies the per-provider API-endpoint override to a model's <see cref="LlmModel.BaseUrl"/>
    /// (#1636). When <paramref name="endpointResolver"/> yields a non-empty endpoint for the model's
    /// provider, the model is returned with that BaseUrl; otherwise the model is returned unchanged.
    /// This mirrors the override the live agent path applies (<c>InProcessIsolationStrategy</c>) and
    /// the compactor applies after #1635, ensuring the titling request hits the same host (e.g.
    /// enterprise vs individual GitHub Copilot) rather than the static built-in BaseUrl. Pure and
    /// null-safe so it is trivially unit-testable and never throws for a missing resolver.
    /// </summary>
    internal static LlmModel ApplyEndpointOverride(LlmModel model, Func<string, string?>? endpointResolver)
    {
        if (endpointResolver is null)
            return model;

        var endpoint = endpointResolver(model.Provider);
        if (string.IsNullOrWhiteSpace(endpoint) || string.Equals(endpoint, model.BaseUrl, StringComparison.Ordinal))
            return model;

        return model with { BaseUrl = endpoint };
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
