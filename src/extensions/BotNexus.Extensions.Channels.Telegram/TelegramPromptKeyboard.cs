using System.Text;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Pure rendering and callback-token logic for <c>ask_user</c> prompts on Telegram (#2323).
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from <see cref="TelegramChannelAdapter"/> and free of any I/O so
/// the two things most likely to break silently - the 64-byte callback-data ceiling and the
/// degrade-to-text threshold - are directly testable without an HTTP harness.
/// </para>
/// <para>
/// <b>Why a token rather than the choice value.</b> The Bot API caps <c>callback_data</c> at
/// <b>64 bytes</b> and rejects the whole <c>sendMessage</c> when it is exceeded, so packing the
/// request id and the choice value into the button would make button delivery a function of how
/// verbose the agent's choice labels happen to be. Instead each rendered prompt takes a short
/// adapter-local token and the button carries only <c>token + ordinal</c>; the real choice is
/// recovered from the pending prompt state the adapter holds. Callback data is therefore a fixed
/// small size regardless of prompt content.
/// </para>
/// </remarks>
internal static class TelegramPromptKeyboard
{
    /// <summary>Hard Bot API ceiling on <c>callback_data</c>, in bytes of UTF-8.</summary>
    internal const int MaxCallbackDataBytes = 64;

    /// <summary>Prefix marking a callback as belonging to a BotNexus ask_user prompt.</summary>
    private const string Prefix = "bnq";

    /// <summary>Payload marking the explicit cancel affordance.</summary>
    private const string CancelPayload = "c";

    /// <summary>Payload marking the explicit multi-select submit affordance.</summary>
    private const string SubmitPayload = "s";

    /// <summary>
    /// Maximum number of choice buttons rendered as a keyboard. Beyond this a keyboard becomes
    /// unusable on a phone (and Telegram itself starts rejecting oversized markup), so the prompt
    /// degrades to the shared numbered-text rendering instead of failing the send outright.
    /// </summary>
    internal const int MaxKeyboardChoices = 30;

    /// <summary>
    /// True when the prompt's choices should render as an inline keyboard. False means the caller
    /// must degrade to <see cref="AskUserPromptTextRenderer.Render"/> - either because there are no
    /// choices at all (free-form) or because there are too many to draw as buttons.
    /// </summary>
    internal static bool ShouldRenderKeyboard(AskUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return prompt.HasChoices && prompt.Choices!.Count <= MaxKeyboardChoices;
    }

    /// <summary>Builds the callback data for selecting the choice at <paramref name="index"/>.</summary>
    internal static string ChoiceCallbackData(string token, int index)
        => $"{Prefix}:{token}:{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Builds the callback data for the cancel affordance.</summary>
    internal static string CancelCallbackData(string token) => $"{Prefix}:{token}:{CancelPayload}";

    /// <summary>Builds the callback data for the multi-select submit affordance.</summary>
    internal static string SubmitCallbackData(string token) => $"{Prefix}:{token}:{SubmitPayload}";

    /// <summary>
    /// Parses callback data produced by this class. Returns false for anything not emitted by a
    /// BotNexus prompt, so unrelated callbacks from other features pass through untouched.
    /// </summary>
    internal static bool TryParseCallbackData(
        string? data,
        out string token,
        out TelegramPromptCallbackKind kind,
        out int choiceIndex)
    {
        token = string.Empty;
        kind = TelegramPromptCallbackKind.Unknown;
        choiceIndex = -1;

        if (string.IsNullOrWhiteSpace(data))
            return false;

        var parts = data.Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(parts[1]))
            return false;

        token = parts[1];

        if (string.Equals(parts[2], CancelPayload, StringComparison.Ordinal))
        {
            kind = TelegramPromptCallbackKind.Cancel;
            return true;
        }

        if (string.Equals(parts[2], SubmitPayload, StringComparison.Ordinal))
        {
            kind = TelegramPromptCallbackKind.Submit;
            return true;
        }

        if (int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedIndex) &&
            parsedIndex >= 0)
        {
            kind = TelegramPromptCallbackKind.Choice;
            choiceIndex = parsedIndex;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the inline keyboard for a prompt: one row per choice, a submit row when the prompt
    /// accepts multiple selections, and always a cancel row.
    /// </summary>
    /// <param name="prompt">The prompt being rendered.</param>
    /// <param name="token">Adapter-local token identifying the pending prompt.</param>
    /// <param name="selected">
    /// Values already selected in a multi-select prompt, marked with a check so the user can see
    /// their accumulated selection before submitting.
    /// </param>
    internal static InlineKeyboardMarkup BuildKeyboard(
        AskUserPrompt prompt,
        string token,
        IReadOnlyCollection<string>? selected = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var rows = new List<IReadOnlyList<InlineKeyboardButton>>();
        var choices = prompt.Choices ?? [];

        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            var isSelected = prompt.AllowMultiple
                && selected is not null
                && selected.Contains(choice.Value, StringComparer.Ordinal);

            // Button labels are rendered by Telegram as literal text - no parse_mode is applied to
            // reply_markup - so the label is passed through unescaped by design.
            var label = isSelected ? $"\u2705 {choice.Label}" : choice.Label;
            rows.Add([new InlineKeyboardButton { Text = label, CallbackData = ChoiceCallbackData(token, index) }]);
        }

        if (prompt.AllowMultiple)
            rows.Add([new InlineKeyboardButton { Text = "\u2714\uFE0F Submit", CallbackData = SubmitCallbackData(token) }]);

        // Cancel is always offered, matching the portal's cancel affordance: the agent is blocked
        // until the prompt resolves, so the user must always have a way out that is not a timeout.
        rows.Add([new InlineKeyboardButton { Text = "\u2716\uFE0F Cancel", CallbackData = CancelCallbackData(token) }]);

        return new InlineKeyboardMarkup { InlineKeyboard = rows };
    }

    /// <summary>
    /// Renders the confirmation text a resolved prompt is edited to, so the chat history shows what
    /// was answered rather than a stale, still-tappable question.
    /// </summary>
    internal static string RenderResolvedText(AskUserPrompt prompt, IReadOnlyList<string>? selectedValues, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var builder = new StringBuilder();
        builder.Append(prompt.Prompt);
        builder.AppendLine();
        builder.AppendLine();

        if (cancelled)
        {
            builder.Append("\u2716\uFE0F Cancelled");
            return builder.ToString();
        }

        var labels = new List<string>();
        foreach (var value in selectedValues ?? [])
        {
            var match = prompt.Choices?.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.Ordinal));
            labels.Add(match is null ? value : match.Label);
        }

        builder.Append("\u2705 ");
        builder.Append(labels.Count == 0 ? "Answered" : string.Join(", ", labels));
        return builder.ToString();
    }
}

/// <summary>Classification of a parsed BotNexus prompt callback.</summary>
internal enum TelegramPromptCallbackKind
{
    /// <summary>Not a BotNexus prompt callback, or malformed.</summary>
    Unknown,

    /// <summary>A choice button at a specific ordinal was tapped.</summary>
    Choice,

    /// <summary>The multi-select submit button was tapped.</summary>
    Submit,

    /// <summary>The cancel button was tapped.</summary>
    Cancel
}
