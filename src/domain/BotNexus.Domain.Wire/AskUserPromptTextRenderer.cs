using System.Text;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Renders an <see cref="AskUserPrompt"/> as plain text for channels that cannot draw
/// structured affordances (buttons, checkboxes, cancel controls) - the text-degraded
/// fallback required by #2322.
/// </summary>
/// <remarks>
/// A channel reporting <c>SupportsInteractivePrompts == false</c> still has to give the user
/// something answerable, because the agent is genuinely blocked. Rendering choices as a stable
/// numbered list lets the user reply with either the number or the choice value, and both are
/// resolvable by <see cref="MatchChoice"/> on the way back in.
/// </remarks>
public static class AskUserPromptTextRenderer
{
    /// <summary>
    /// Renders the prompt, its numbered choices, and the answering hint as a single text block.
    /// </summary>
    public static string Render(AskUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var builder = new StringBuilder();
        builder.Append(prompt.Prompt);

        if (!prompt.HasChoices)
            return builder.ToString();

        builder.AppendLine();
        for (var index = 0; index < prompt.Choices!.Count; index++)
        {
            var choice = prompt.Choices[index];
            builder.AppendLine();
            builder.Append($"{index + 1}. {choice.Label}");
            if (!string.IsNullOrWhiteSpace(choice.Description))
                builder.Append($" - {choice.Description}");
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.Append(prompt.AllowMultiple
            ? "Reply with the numbers of your choices (comma separated)"
            : "Reply with the number of your choice");

        if (prompt.AllowFreeForm)
            builder.Append(", or type your own answer");

        builder.Append('.');
        return builder.ToString();
    }

    /// <summary>
    /// Resolves a free-text reply on a text-degraded channel back to a structured choice value,
    /// accepting either the 1-based ordinal shown by <see cref="Render"/>, the choice value, or
    /// the choice label (case-insensitive). Returns <c>null</c> when nothing matches, in which
    /// case the reply should be treated as free-form text.
    /// </summary>
    public static string? MatchChoice(AskUserPrompt prompt, string? reply)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!prompt.HasChoices || string.IsNullOrWhiteSpace(reply))
            return null;

        var trimmed = reply.Trim();

        if (int.TryParse(trimmed, out var ordinal) && ordinal >= 1 && ordinal <= prompt.Choices!.Count)
            return prompt.Choices[ordinal - 1].Value;

        foreach (var choice in prompt.Choices!)
        {
            if (string.Equals(choice.Value, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(choice.Label, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return choice.Value;
            }
        }

        return null;
    }
}
