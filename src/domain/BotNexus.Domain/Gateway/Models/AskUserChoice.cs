namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents a selectable option for an <c>ask_user</c> prompt so channels can
/// render stable values and user-friendly labels independently.
/// </summary>
public sealed record AskUserChoice
{
    /// <summary>Machine-stable value returned to the tool result when selected.</summary>
    public required string Value { get; init; }

    /// <summary>Optional display label shown to users; defaults to <see cref="Value"/> when omitted.</summary>
    public string? Label { get; init; }

    /// <summary>Optional helper text that gives users more context about this option.</summary>
    public string? Description { get; init; }
}
