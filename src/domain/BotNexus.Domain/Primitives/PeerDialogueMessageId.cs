using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Client-assigned, globally stable message identity. Reuse reconciles admission; it never
/// grants permission to change a message or to repeat an external side effect.
/// </summary>
[ValueObject<Guid>(conversions: Conversions.SystemTextJson,
    isInitializedMethodGeneration: IsInitializedMethodGeneration.Generate)]
public readonly partial struct PeerDialogueMessageId
{
    private static Validation Validate(Guid value) => value == Guid.Empty
        ? Validation.Invalid("PeerDialogueMessageId cannot be empty.")
        : Validation.Ok;

    private static Guid NormalizeInput(Guid input) => input;
}
