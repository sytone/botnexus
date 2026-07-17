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
    private readonly GatewayAuthManager? _authManager;

    /// <summary>
    /// Creates the auto-title service. Since #1639 the resolved titling model already carries the
    /// correct per-provider endpoint (resolved at registration time), so <paramref name="authManager"/>
    /// is accepted only for API-compatibility and no endpoint override is applied here.
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
        // #1639: the titling model is resolved from the registry, which already carries the correct
        // per-provider endpoint (enterprise vs individual GitHub Copilot resolved at registration),
        // so no endpoint override is threaded through here anymore.
        // #2025: the auth manager IS used now - titling resolves its API key through the same
        // GatewayAuthManager seam every other LLM call uses (foreground agent loop, compaction),
        // rather than calling the provider with no options and relying on environment keys that do
        // not exist for OAuth providers (the "No API key for github-copilot" failure).
        _authManager = authManager;
    }

    /// <summary>
    /// Attempts to auto-generate and persist a conversation title, best-effort.
    /// The method returns immediately without waiting for the background work to complete.
    /// </summary>
    /// <param name="conversationId">Conversation to title.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="userText">The first user message text, or null for an agent-initiated
    /// conversation with no user turn (assistant-only titling seed).</param>
    /// <param name="assistantText">The first assistant response text.</param>
    /// <param name="preferredModelId">Optional model ID from auxiliary.titling config.</param>
    /// <param name="timeoutSeconds">Per-call timeout in seconds from auxiliary.titling config; non-positive falls back to 30.</param>
    public void TriggerBestEffort(
        ConversationId conversationId,
        AgentId agentId,
        string? userText,
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
        string? userText,
        string assistantText,
        string? preferredModelId,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // Load conversation and check that it still has the default title.
        var conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsDefaultTitle(conversation.Title))
        {
            // #1979: previously a silent return. When auto-title fires (the hook logs
            // "titling from assistant content") but no title is ever persisted, this guard
            // was the invisible dead-end. Log which arm tripped and the observed title so a
            // background-task null store-read is distinguishable from a title-comparison miss.
            _logger.LogInformation(
                "Auto-title: not persisting for conversation '{ConversationId}' at initial guard " +
                "(conversationLoaded={ConversationLoaded}, title='{Title}'); already non-default or not found",
                conversationId,
                conversation is not null,
                conversation?.Title);
            return null;
        }

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

            // #2025: resolve credentials through the shared GatewayAuthManager seam and thread the
            // key into the provider options, exactly as the foreground agent loop and the compactor
            // do. Without this the provider fell back to environment keys (absent for OAuth
            // providers like github-copilot) and threw "No API key", surfacing as an empty title.
            // A null auth manager yields null-key options -> provider env fallback (behaviour-
            // preserving for callers/tests that construct the service without an auth manager).
            var streamOptions = _authManager is not null
                ? await _authManager
                    .CreateAuthenticatedOptionsAsync(model.Provider, baseOptions: null, ct)
                    .ConfigureAwait(false)
                : null;

            completion = await _llmClient
                .CompleteSimpleAsync(model, context, streamOptions)
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

        var rawTitle = ExtractTitleText(completion);

        var title = SanitizeTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            // #1979: the model returned an empty/whitespace title after sanitisation. Surface it
            // so a persistent "never titles" symptom is not silently attributed to the guards.
            _logger.LogInformation(
                "Auto-title: not persisting for conversation '{ConversationId}'; model produced an " +
                "empty title after sanitisation (rawLength={RawLength})",
                conversationId,
                rawTitle?.Length ?? 0);
            return null;
        }

        // Re-read and guard again in case another path already set a custom title.
        conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsDefaultTitle(conversation.Title))
        {
            // #1979: a concurrent path set a custom title (or the row vanished) between the first
            // guard and here. Log it so the re-read race is observable rather than a silent no-op.
            _logger.LogInformation(
                "Auto-title: not persisting for conversation '{ConversationId}' at re-read guard " +
                "(conversationLoaded={ConversationLoaded}, title='{Title}'); title changed concurrently or row removed",
                conversationId,
                conversation is not null,
                conversation?.Title);
            return null;
        }

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
        // #1903: only genuinely nothing-to-title (0 user AND 0 assistant) should skip. An
        // assistant-only conversation (agent/cron/sub-agent-initiated: first sender is an agent,
        // so DeriveChannelPostRole stamps it Assistant and the user count stays 0 forever) must
        // still produce a titling candidate via the assistant-only prompt.
        if (assistantEntries.Count < 1)
        {
            // Log at INFO so the no-fire path is diagnosable: previously a count mismatch skipped
            // silently and gave no signal for conversations stuck on the default title.
            logger?.LogInformation(
                "Auto-title guard skipped: nothing to title (user={UserCount}, assistant={AssistantCount}); no assistant response present yet",
                userEntries.Count, assistantEntries.Count);
            return (null, null);
        }

        // Pick the last assistant entry with non-empty content (handles tool-call turns
        // where intermediate assistant entries have empty content).
        var assistantText = assistantEntries
            .LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Content))?.Content
            ?? assistantEntries[^1].Content;

        if (userEntries.Count < 1)
        {
            // Agent-initiated: no user turn. Seed titling from the first assistant entry's
            // content and signal userText=null so BuildPrompt uses the assistant-only prompt.
            var assistantSeed = assistantEntries
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Content))?.Content
                ?? assistantEntries[0].Content;
            logger?.LogInformation(
                "Auto-title: agent-initiated conversation (user=0, assistant={AssistantCount}); titling from assistant content",
                assistantEntries.Count);
            return (null, assistantSeed);
        }

        var userText = userEntries[0].Content;

        return (userText, assistantText);
    }

    internal static string BuildPrompt(string? userText, string assistantText)
    {
        // Agent-initiated conversations (cron/sub-agent/agent-first) have no user turn: the first
        // message sender is an agent, so DeriveChannelPostRole stamps it Assistant and the user
        // count stays 0 forever (#1903). Fall back to an assistant-only titling prompt so those
        // conversations can still title off the assistant's opening content.
        if (string.IsNullOrWhiteSpace(userText))
        {
            return "In 5 words or fewer, give a descriptive title for this conversation based on the " +
                   "following assistant response. Return only the title, no punctuation, " +
                   $"no quotes.\n\nAssistant: {assistantText}";
        }

        return "In 5 words or fewer, give a descriptive title for this conversation based on the " +
               "first user message and assistant response. Return only the title, no punctuation, " +
               $"no quotes.\n\nUser: {userText}\n\nAssistant: {assistantText}";
    }

    /// <summary>
    /// #1994: extracts titling text from a completion. Prefers real answer text (TextContent) and
    /// falls back to reasoning/thinking content (ThinkingContent) when no text block is present.
    /// A reasoning model returns its answer in a thinking block with zero TextContent, which
    /// otherwise sanitised to an empty title and never persisted (the live rawLength=0 bug).
    /// </summary>
    internal static string ExtractTitleText(AssistantMessage completion)
    {
        // Provider streaming can surface each delta as a separate content block. Preserve the
        // provider's exact boundaries: inserting separators here splits token fragments such as
        // "Container" + "ized" and doubles whitespace already carried by chunks.
        var text = string.Concat(
            completion.Content
                .OfType<TextContent>()
                .Select(t => t.Text));

        if (!string.IsNullOrWhiteSpace(text))
            return text;

        // No usable text block — fall back to thinking content so a reasoning model still titles.
        return string.Concat(
            completion.Content
                .OfType<ThinkingContent>()
                .Select(t => t.Thinking));
    }

    internal static string SanitizeTitle(string raw)
    {
        var withoutQuotes = raw
            .Replace("\"", "")
            .Replace("'", "");
        var sanitised = string.Join(
            " ",
            withoutQuotes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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

        if (fallback is null)
            throw new InvalidOperationException("No models are registered for auto-title generation.");

        // #1639: the fallback model is correct by construction (endpoint resolved at registration),
        // so it is returned as-is with no BaseUrl patch.
        return fallback;
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
