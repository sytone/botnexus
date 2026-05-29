using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a conversation -- the durable container the citizen (user or agent) thinks
/// they are in. Construct via <see cref="From(string)"/> for existing values, or
/// <see cref="Create"/> for a new conversation. The value must be non-null, non-empty,
/// non-whitespace and is stored trimmed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsInitialized"/> is generated so callers can distinguish a Vogen
/// default-constructed instance (used as the "unset" sentinel on
/// <c>Session.ConversationId</c> before the legacy backfill resolver fires) from a
/// valid, From-constructed value. Production code outside the session-store layer
/// should never observe an uninitialized <c>ConversationId</c> — stores backfill on
/// both save and load (Phase 9 / P9-B; issues #615, #627).
/// </para>
/// <para>
/// <see cref="PrimitiveEqualityGeneration.Omit"/> is set to remove the auto-generated
/// <c>operator ==(ConversationId, string?)</c> overload that would otherwise make
/// <c>conversationId == default</c> ambiguous against the typed equality overload.
/// Callers compare ConversationIds against each other directly; the only "unset"
/// check should use <see cref="IsInitialized"/>.
/// </para>
/// </remarks>
[ValueObject<string>(
    conversions: Conversions.SystemTextJson,
    isInitializedMethodGeneration: IsInitializedMethodGeneration.Generate,
    primitiveEqualityGeneration: PrimitiveEqualityGeneration.Omit)]
public readonly partial struct ConversationId
{
    /// <summary>
    /// Creates a new unique <see cref="ConversationId"/> with the <c>c_</c> prefix.
    /// </summary>
    public static ConversationId Create() => From($"c_{Guid.NewGuid():N}");

    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("ConversationId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
