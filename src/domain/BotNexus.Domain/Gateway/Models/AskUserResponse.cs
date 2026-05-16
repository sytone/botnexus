namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Captures the normalized outcome of an <c>ask_user</c> prompt so the waiting
/// tool call can resume with explicit cancellation/timeout semantics.
/// </summary>
public sealed record AskUserResponse
{
    /// <summary>Correlation identifier for the request this response satisfies.</summary>
    public required string RequestId { get; init; }

    /// <summary>User-provided free-form text, when supplied.</summary>
    public string? FreeFormText { get; init; }

    /// <summary>Selected structured choice values, when supplied.</summary>
    public IReadOnlyList<string>? SelectedValues { get; init; }

    /// <summary>True when the user explicitly cancelled the prompt.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>True when the prompt elapsed without a user response.</summary>
    public bool WasTimeout { get; init; }
}
