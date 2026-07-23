using System.Text.Json;
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
/// Generates a short descriptive title for a conversation. Two entry points exist:
/// <list type="bullet">
/// <item><see cref="TriggerProvisionalBestEffort"/> fires when the first live user message is
/// persisted, BEFORE the assistant turn completes, so a human-agent conversation stops showing
/// "New conversation" almost immediately (#2126). The title is marked provisional.</item>
/// <item><see cref="TriggerBestEffort"/> fires after the first user+assistant exchange. It either
/// titles a still-default conversation or refines a provisional title exactly once
/// (refine-once policy) using the completed assistant response.</item>
/// </list>
/// </summary>
/// <remarks>
/// This is a best-effort background service. Title generation failures are logged at
/// Warning level and do not surface as turn failures.
/// </remarks>
public sealed class ConversationAutoTitleService
{
    /// <summary>The default title assigned to new conversations before a user-generated title exists.</summary>
    public const string DefaultTitle = "New conversation";

    /// <summary>
    /// Conversation metadata flag marking a title as provisional - generated from the first user
    /// message alone before the assistant turn completed (#2126). A provisional title is eligible
    /// for exactly one refinement by the post-response auto-title path (refine-once policy): the
    /// refine save clears this flag, after which the title is treated as final and the standard
    /// custom-title guard protects it. A manually assigned title never carries this flag, so it is
    /// never overwritten.
    /// </summary>
    public const string ProvisionalTitleMetadataKey = "provisionalTitle";

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
    /// Attempts to auto-generate and persist a PROVISIONAL conversation title from the first user
    /// message alone, best-effort, before the assistant turn completes (#2126). Returns immediately
    /// without waiting for the background work. Callers must gate this on the conversation being a
    /// human-agent, interactive, default-titled conversation with a real human first message; the
    /// service applies the default-title re-guard itself so a custom title is never overwritten.
    /// </summary>
    /// <param name="conversationId">Conversation to title.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="userText">The first user message text.</param>
    /// <param name="preferredModelId">Optional model ID from auxiliary.titling config.</param>
    /// <param name="timeoutSeconds">Per-call timeout in seconds; non-positive falls back to 30.</param>
    public void TriggerProvisionalBestEffort(
        ConversationId conversationId,
        AgentId agentId,
        string userText,
        string? preferredModelId,
        int timeoutSeconds = 30)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateProvisionalAndSaveAsync(
                    conversationId, agentId, userText, preferredModelId,
                    timeoutSeconds, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provisional auto-title generation failed for conversation '{ConversationId}' (best-effort, ignoring)",
                    conversationId);
            }
        });
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
    /// Generates and saves a provisional conversation title from the first user message alone
    /// (#2126). Returns the generated title, or null when the conversation already has a
    /// non-default title, the LLM returned an empty result, or the guard tripped. The persisted
    /// title carries the provisional metadata flag so the post-response path can refine it once.
    /// </summary>
    internal async Task<string?> GenerateProvisionalAndSaveAsync(
        ConversationId conversationId,
        AgentId agentId,
        string userText,
        string? preferredModelId,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // Provisional titling only ever applies to a brand-new, still-default-titled conversation.
        var conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsDefaultTitle(conversation.Title))
        {
            _logger.LogInformation(
                "Provisional auto-title: not persisting for conversation '{ConversationId}' at initial guard " +
                "(conversationLoaded={ConversationLoaded}, title='{Title}'); already non-default or not found",
                conversationId,
                conversation is not null,
                conversation?.Title);
            return null;
        }

        LlmModel? model;
        try
        {
            model = ResolveModel(preferredModelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Provisional auto-title: no model available for conversation '{ConversationId}'",
                conversationId);
            return null;
        }

        var prompt = BuildProvisionalPrompt(userText);
        var title = await CompleteTitleAsync(model, prompt, conversationId, timeoutSeconds, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Re-read and guard again in case another path already set a custom title between reads.
        conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsDefaultTitle(conversation.Title))
        {
            _logger.LogInformation(
                "Provisional auto-title: not persisting for conversation '{ConversationId}' at re-read guard " +
                "(conversationLoaded={ConversationLoaded}, title='{Title}'); title changed concurrently or row removed",
                conversationId,
                conversation is not null,
                conversation?.Title);
            return null;
        }

        conversation.Title = title;
        // Mark provisional so the post-response path may refine exactly once (refine-once policy).
        conversation.Metadata[ProvisionalTitleMetadataKey] = true;
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);

        await NotifyChangedAsync(agentId, conversationId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Provisional auto-title set to '{Title}' for conversation '{ConversationId}' (model {ModelId})",
            title, conversationId, model.Id);

        return title;
    }

    /// <summary>
    /// Generates and saves the conversation title. Returns the generated title, or null when
    /// the conversation already has a final custom title or the LLM returned an empty result.
    /// A provisional title (see <see cref="ProvisionalTitleMetadataKey"/>) is refined exactly
    /// once here and the provisional flag is cleared on save (refine-once policy, #2126).
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
        // Load conversation and check that it is still eligible for titling: either the default
        // title, or a provisional title that has not yet been refined (refine-once, #2126).
        var conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsTitleEligible(conversation))
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
        var title = await CompleteTitleAsync(model, prompt, conversationId, timeoutSeconds, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Re-read and guard again in case another path already set a custom title.
        conversation = await _store.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !IsTitleEligible(conversation))
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
        // Refine-once (#2126): once the post-response path titles/refines, the title is final, so
        // clear the provisional flag. A subsequent turn then hits the standard custom-title guard.
        conversation.Metadata.Remove(ProvisionalTitleMetadataKey);
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);

        await NotifyChangedAsync(agentId, conversationId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Auto-title set to '{Title}' for conversation '{ConversationId}' (model {ModelId})",
            title, conversationId, model.Id);

        return title;
    }

    /// <summary>
    /// Runs the titling LLM call and returns the sanitised title, or null when the call failed or
    /// produced an empty title after sanitisation. Shared by the provisional and post-response paths.
    /// </summary>
    private async Task<string?> CompleteTitleAsync(
        LlmModel model,
        string prompt,
        ConversationId conversationId,
        int timeoutSeconds,
        CancellationToken ct)
    {
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
            // do. A null auth manager yields null-key options -> provider env fallback.
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

        return title;
    }

    private async Task NotifyChangedAsync(AgentId agentId, ConversationId conversationId, CancellationToken ct)
    {
        if (_notifier is null)
            return;

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

    /// <summary>Returns true when the title is still at the default "New conversation" value.</summary>
    public static bool IsDefaultTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ||
           string.Equals(title, DefaultTitle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the conversation carries the provisional-title flag (#2126) - i.e. it was
    /// titled from the first user message alone and has not yet been refined by the post-response
    /// path. The metadata value round-trips through JSON, so a raw bool, a <see cref="JsonElement"/>,
    /// and the string "true" are all accepted.
    /// </summary>
    public static bool IsProvisionalTitle(Conversation conversation)
    {
        if (!conversation.Metadata.TryGetValue(ProvisionalTitleMetadataKey, out var raw) || raw is null)
            return false;

        return raw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement je when je.ValueKind == JsonValueKind.String => bool.TryParse(je.GetString(), out var jb) && jb,
            string s => bool.TryParse(s, out var sb) && sb,
            _ => false,
        };
    }

    /// <summary>
    /// Returns true when the post-response auto-title path may write a title: either the title is
    /// still the default, or it is a provisional title eligible for its one refinement (#2126).
    /// </summary>
    private static bool IsTitleEligible(Conversation conversation)
        => IsDefaultTitle(conversation.Title) || IsProvisionalTitle(conversation);

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
    /// Evaluates the session history to decide whether a provisional title should be generated
    /// from the first user message alone (#2126). Returns the first user text when exactly one
    /// live user entry exists and no assistant entry has been produced yet (the first user message
    /// has just been persisted, before the assistant turn completes); otherwise returns null.
    /// </summary>
    public static string? ShouldTriggerProvisionalTitle(IReadOnlyList<SessionEntry> history)
    {
        var liveEntries = history.Where(IsLiveConversationEntry).ToList();
        var userEntries = liveEntries.Where(e => e.Role == MessageRole.User).ToList();
        var assistantEntries = liveEntries.Where(e => e.Role == MessageRole.Assistant).ToList();

        // Only the pristine "first user message, no assistant yet" state qualifies. A second user
        // turn or any assistant entry means the post-response path owns titling from here.
        if (userEntries.Count != 1 || assistantEntries.Count != 0)
            return null;

        var userText = userEntries[0].Content;
        return string.IsNullOrWhiteSpace(userText) ? null : userText;
    }

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

    /// <summary>
    /// Builds the provisional titling prompt from the first user message alone (#2126). No
    /// assistant response exists yet, so the model titles purely on the user's opening intent.
    /// </summary>
    internal static string BuildProvisionalPrompt(string userText)
        => "In 5 words or fewer, give a descriptive title for a conversation that begins with the " +
           "following user message. Return only the title, no punctuation, no quotes.\n\n" +
           $"User: {userText}";

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

        // No usable text block - fall back to thinking content so a reasoning model still titles.
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
