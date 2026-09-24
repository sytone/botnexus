using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Carries a store-neutral, opaque position in a conversation. Callers may compare positions only
/// to advance monotonically; they must not infer message indexes or transport semantics from it.
/// </summary>
[ValueObject<long>(conversions: Conversions.SystemTextJson)]
public readonly partial struct ConversationReadPosition
{
    private static Validation Validate(long value) =>
        value < 0
            ? Validation.Invalid("ConversationReadPosition cannot be negative.")
            : Validation.Ok;
}
