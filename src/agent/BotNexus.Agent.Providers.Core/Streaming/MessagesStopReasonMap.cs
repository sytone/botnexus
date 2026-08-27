using System.Collections.Concurrent;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// The single total mapping from an Anthropic Messages-API <c>stop_reason</c> string to the core
/// <see cref="StopReason"/>, shared by every provider that speaks that wire shape (Anthropic direct
/// and Copilot Messages).
/// </summary>
/// <remarks>
/// Both providers previously carried a private copy of this switch whose default arm was
/// <c>throw new InvalidOperationException</c> (#3564). That made the mapping PARTIAL: any stop
/// reason the provider added outside the nine-entry literal list turned a turn that had otherwise
/// succeeded - content streamed, tool calls parsed - into a thrown exception swallowed by the
/// generic <c>catch</c> in <c>StreamAsync</c>. The turn was lost rather than degraded, and the
/// surfaced error said nothing about the only actual problem being an unrecognised enum string.
/// Anthropic added <c>pause_turn</c> and <c>refusal</c> within the lifetime of those files, so this
/// was a latent break scheduled for whenever the provider next shipped a new value - firing across
/// every turn on the platform's two most-used providers at once.
/// <para>
/// The fail-safe shape is deliberately the one <see cref="CompletionsStreamEngine.MapStopReason"/>
/// already used rather than a third invention: an absent reason is an ordinary stop, and an
/// unrecognised one is <see cref="StopReason.Error"/> carrying the diagnostic
/// <c>"Provider stop_reason: {reason}"</c>. Classifying the unknown as <c>Error</c> rather than
/// <c>Stop</c> keeps the gap visible instead of silently pretending the turn ended normally.
/// </para>
/// <para>
/// The unrecognised value is additionally logged once per distinct string at Warning, so a new
/// provider stop reason is diagnosable from the gateway log without a debugger and without
/// re-emitting on every turn for the rest of the process lifetime.
/// </para>
/// </remarks>
public static class MessagesStopReasonMap
{
    private static readonly ConcurrentDictionary<string, byte> ReportedUnknownReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a Messages-API <c>stop_reason</c> to the core <see cref="StopReason"/> plus an optional
    /// human-readable message. Total by construction: never throws, for any input including
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="reason">The raw provider <c>stop_reason</c>, or <see langword="null"/>.</param>
    /// <param name="providerLabel">
    /// Provider name used only in the Warning log line, so an unrecognised value can be attributed
    /// to the provider that emitted it.
    /// </param>
    public static (StopReason StopReason, string? ErrorMessage) Map(string? reason, string providerLabel)
    {
        switch (reason)
        {
            case "end_turn": return (StopReason.Stop, null);
            case "max_tokens": return (StopReason.Length, null);
            case "tool_use": return (StopReason.ToolUse, null);
            case "refusal": return (StopReason.Refusal, null);
            case "pause_turn": return (StopReason.Stop, null);
            case "stop_sequence": return (StopReason.Stop, null);
            case "content_policy": return (StopReason.Sensitive, null);
            case "safety": return (StopReason.Sensitive, null);
            case "sensitive": return (StopReason.Sensitive, null);
            case null: return (StopReason.Stop, null);
            default:
                ReportUnknownOnce(reason, providerLabel);
                return (StopReason.Error, $"Provider stop_reason: {reason}");
        }
    }

    /// <summary>
    /// Convenience overload for the provider seams that consume a <c>Func&lt;string?, StopReason&gt;</c>
    /// and have no channel for the diagnostic message.
    /// </summary>
    public static StopReason MapStopReason(string? reason, string providerLabel) =>
        Map(reason, providerLabel).StopReason;

    private static void ReportUnknownOnce(string reason, string providerLabel)
    {
        if (!ReportedUnknownReasons.TryAdd(reason, 0))
            return;

        ProviderDiagnostics
            .CreateLogger(nameof(MessagesStopReasonMap))
            .LogWarning(
                "Unrecognised {Provider} stop_reason '{StopReason}'. The turn was preserved and " +
                "classified as Error rather than discarded; add an explicit mapping if this value " +
                "is expected.",
                providerLabel,
                reason);
    }
}
