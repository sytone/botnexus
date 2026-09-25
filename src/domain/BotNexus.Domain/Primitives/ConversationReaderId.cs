using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies the durable reader whose progress through a conversation is tracked independently
/// from every other user, agent, device, or consumer.
/// </summary>
[ValueObject<string>(
    conversions: Conversions.SystemTextJson,
    isInitializedMethodGeneration: IsInitializedMethodGeneration.Generate,
    primitiveEqualityGeneration: PrimitiveEqualityGeneration.Omit)]
public readonly partial struct ConversationReaderId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("ConversationReaderId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
