using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Maps between the gateway-side <see cref="AskUserRequest"/> (typed, Vogen-backed) and the
/// channel-agnostic <see cref="AskUserPrompt"/> wire/render model (#2322).
/// </summary>
/// <remarks>
/// <para>
/// This projection deliberately stays in <c>BotNexus.Domain</c> while <see cref="AskUserPrompt"/>
/// itself lives in the dependency-free <c>BotNexus.Domain.Wire</c> (#2329, #2334). That split is
/// the whole point: <see cref="AskUserRequest"/> carries Vogen value objects
/// (<see cref="ConversationId"/>, <c>SessionId</c>, <c>AgentId</c>), and Vogen flows as a RUNTIME
/// asset, so anything touching those types would drag <c>Vogen.SharedTypes.dll</c> into the
/// Blazor WebAssembly payload downloaded by every browser.
/// </para>
/// <para>
/// So the typed/untyped boundary is drawn HERE, on the server, rather than inside the shared wire
/// shape. The prompt travels as plain strings; validation of a conversation id happens when it is
/// converted back through <see cref="ToConversationId"/>, which is where a bad id should be
/// rejected anyway.
/// </para>
/// </remarks>
public static class AskUserPromptProjection
{
    /// <summary>Projects a gateway ask-user request onto the render-ready prompt model.</summary>
    public static AskUserPrompt ToPrompt(this AskUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AskUserPrompt
        {
            RequestId = request.RequestId,
            ConversationId = request.ConversationId.Value,
            Prompt = request.Prompt,
            InputType = request.InputType.ToString(),
            Choices = request.Choices is { Count: > 0 }
                ? request.Choices
                    .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
                    .Select(choice => new AskUserPromptChoice(
                        choice.Value,
                        string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label!,
                        choice.Description))
                    .ToList()
                : null,
            AllowMultiple = request.AllowMultiple,
            AllowFreeForm = request.AllowFreeForm,
            ExpiresAt = request.Timeout is { } timeout ? DateTimeOffset.UtcNow.Add(timeout) : null
        };
    }

    /// <summary>
    /// Converts the prompt's wire-shaped conversation id back to the typed value object, or
    /// <c>null</c> when the prompt carries no id. Reconciliation can legitimately produce a prompt
    /// without one, so this is a nullable conversion rather than a required parse.
    /// </summary>
    public static ConversationId? ToConversationId(this AskUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return string.IsNullOrWhiteSpace(prompt.ConversationId)
            ? null
            : ConversationId.From(prompt.ConversationId);
    }
}
