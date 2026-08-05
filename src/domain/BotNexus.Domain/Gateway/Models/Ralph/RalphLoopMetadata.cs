using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Reads and writes the ralph loop's config and durable state on <see cref="Conversation.Metadata"/>
/// (issue #2818).
/// </summary>
/// <remarks>
/// <para>
/// Stored as a <em>single</em> opaque JSON string under one metadata key rather than as a spray of
/// loose scalar keys. Metadata values round-trip through <c>JsonSerializer</c> and hydrate as
/// <see cref="JsonElement"/> boxes, so per-scalar keys would force every reader to re-implement the
/// same unbox-and-coerce dance — the exact duplication this feature's decision function exists to
/// avoid. One key, one parse, one place.
/// </para>
/// <para>
/// Kept on metadata rather than as new typed conversation columns because the persisted shape is
/// owned by this loop alone; the stores treat it as opaque, exactly as they already do for
/// <c>TodoJson</c> and <c>PendingAskUserJson</c>.
/// </para>
/// </remarks>
public static class RalphLoopMetadata
{
    /// <summary>The single conversation-metadata key holding the loop's config and state.</summary>
    public const string Key = "ralph";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Reads the loop's config and state off the conversation. A conversation with no ralph metadata
    /// (or with unreadable metadata) yields <see cref="RalphLoopConfig.Default"/> and
    /// <see cref="RalphLoopState.Initial"/> rather than throwing: an unparseable blob must not make a
    /// running loop un-evaluable, and the defaults are the safe, bounded ones.
    /// </summary>
    public static (RalphLoopConfig Config, RalphLoopState State) Read(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (!conversation.Metadata.TryGetValue(Key, out var raw) || raw is null)
            return (RalphLoopConfig.Default, RalphLoopState.Initial);

        var json = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => raw.ToString()
        };

        if (string.IsNullOrWhiteSpace(json))
            return (RalphLoopConfig.Default, RalphLoopState.Initial);

        try
        {
            var envelope = JsonSerializer.Deserialize<RalphLoopEnvelope>(json, SerializerOptions);
            return envelope is null
                ? (RalphLoopConfig.Default, RalphLoopState.Initial)
                : (envelope.Config ?? RalphLoopConfig.Default, envelope.State ?? RalphLoopState.Initial);
        }
        catch (JsonException)
        {
            return (RalphLoopConfig.Default, RalphLoopState.Initial);
        }
    }

    /// <summary>
    /// Writes the loop's config and state back onto the conversation's metadata, replacing whatever
    /// was there. Mutates <paramref name="conversation"/>'s metadata dictionary in place; the caller
    /// is responsible for persisting the conversation.
    /// </summary>
    public static void Write(Conversation conversation, RalphLoopConfig config, RalphLoopState state)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        conversation.Metadata[Key] = JsonSerializer.Serialize(new RalphLoopEnvelope(config, state), SerializerOptions);
    }

    private sealed record RalphLoopEnvelope(RalphLoopConfig? Config, RalphLoopState? State);
}
