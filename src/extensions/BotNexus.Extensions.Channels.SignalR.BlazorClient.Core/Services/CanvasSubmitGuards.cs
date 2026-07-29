namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Pure, transport-free guards for the canvas <c>submitToAgent</c> bridge verb (#2449).
///
/// The canvas iframe is sandboxed but its HTML may embed or fetch third-party content, so every
/// value that reaches the conversation transport must be either (a) derived from the host's own
/// binding, or (b) normalised here. Holding that half separately lets the guards be asserted
/// directly without a hub, an HttpClient, or a rendered component.
/// </summary>
/// <remarks>
/// Deliberately absent: provenance. A canvas submission is marked as canvas-originated by the
/// server-stamped <c>MessageKind.CanvasSubmission</c> on the turn itself, reusing the #2300
/// provenance vocabulary at the message level. It is NOT expressed by any literal in the message
/// text - any message can contain any literal, so a text marker proves nothing.
///
/// Also deliberately absent: rate limiting. Per Jon's product decision on #2449 there is no
/// min-interval, in-flight tracking or throttle machinery. <c>submitToAgent</c> is user-initiated
/// only; that is a documented instruction on the tool and the bridge SDK, not enforced code.
/// </remarks>
public static class CanvasSubmitGuards
{
    /// <summary>
    /// Maximum accepted prompt length in characters.
    /// </summary>
    /// <remarks>
    /// An <b>arbitrary guardrail, not a contract.</b> The prompt is instruction text: the canvas
    /// stores data in canvas state and the agent reads it back from there, so this bound comfortably
    /// fits any real instruction and would only be hit by someone inlining a data dump. Nothing
    /// inspects the prompt's contents - the instructions-not-payload rule is enforced by
    /// documentation.
    /// </remarks>
    public const int MaxPromptLength = 2000;

    /// <summary>
    /// Maximum accepted length of the optional <c>instructions</c> field, appended after the prompt.
    /// The same arbitrary-guardrail rationale as <see cref="MaxPromptLength"/> applies.
    /// </summary>
    public const int MaxInstructionsLength = 1000;

    /// <summary>
    /// Normalises iframe-supplied text into a single-line, length-bounded fragment.
    /// Carriage returns, line feeds and other control characters are collapsed to spaces so the
    /// text cannot fabricate additional transcript lines or trailer-shaped suffixes.
    /// </summary>
    /// <returns><see langword="null"/> when the text is null, blank, or exceeds <paramref name="maxLength"/>.</returns>
    public static string? TryNormalise(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Length > maxLength)
            return null;

        var buffer = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            buffer.Append(char.IsControl(ch) ? ' ' : ch);

        var collapsed = buffer.ToString().Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>
    /// Composes the content of the injected user turn from the validated prompt and optional
    /// instructions. Carries no provenance marker: provenance lives on the turn's
    /// <c>MessageKind</c>, not in its text.
    /// </summary>
    public static string ComposeContent(string prompt, string? instructions) =>
        instructions is null ? prompt : $"{prompt} {instructions}";
}
