using System.Globalization;
using System.Text;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Renders an <c>ask_user</c> prompt for Telegram (#2323): the visible message body plus, when the
/// choices fit Telegram's constraints, an inline keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Graceful degradation is the point.</b> An inline keyboard is a convenience; the prompt itself
/// is not. Whenever a keyboard cannot be built - too many choices, or a callback token that would
/// breach the 64-byte cap (see <see cref="TelegramAskUserCallbackToken"/>) - the choices are
/// rendered as a numbered text list the user can answer by typing. Failing the send instead would
/// leave the agent blocked on a prompt nobody ever saw.
/// </para>
/// </remarks>
internal static class TelegramAskUserPromptRenderer
{
    /// <summary>
    /// Maximum number of choices rendered as buttons. Telegram itself allows more, but a keyboard
    /// past this is unusable on a phone and the numbered text list reads better.
    /// </summary>
    internal const int MaxKeyboardChoices = 24;

    /// <summary>Buttons per keyboard row.</summary>
    private const int ButtonsPerRow = 2;

    /// <summary>The rendered prompt: MarkdownV2 body plus an optional inline keyboard.</summary>
    /// <param name="Text">MarkdownV2-escaped message body.</param>
    /// <param name="Keyboard">Inline keyboard, or null when the prompt degraded to a text list.</param>
    /// <param name="ChoiceValues">
    /// The machine-stable choice values in button order, so an inbound callback carrying only an
    /// index can be resolved back to the value the tool expects.
    /// </param>
    internal sealed record RenderedPrompt(
        string Text,
        InlineKeyboardMarkup? Keyboard,
        IReadOnlyList<string> ChoiceValues);

    /// <summary>Renders the prompt for the supplied ask-user request.</summary>
    internal static RenderedPrompt Render(AskUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var choices = (request.Choices ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToArray();

        var values = choices.Select(c => c.Value).ToArray();

        var keyboard = choices.Length is > 0 and <= MaxKeyboardChoices
            ? TryBuildKeyboard(request.RequestId, choices)
            : null;

        var builder = new StringBuilder();
        builder.Append(TelegramMarkdownFormatter.Convert(request.Prompt));

        if (choices.Length > 0 && keyboard is null)
        {
            // Degraded path: no keyboard, so the choices must still be visible and answerable.
            builder.AppendLine();
            builder.AppendLine();
            for (var i = 0; i < choices.Length; i++)
            {
                builder.Append(TelegramMarkdownFormatter.EscapeMarkdownV2(
                    (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + LabelOf(choices[i])));
                builder.AppendLine();
            }

            builder.AppendLine();
            builder.Append(TelegramMarkdownFormatter.EscapeMarkdownV2("Reply with your choice."));
        }
        else if (choices.Length == 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(TelegramMarkdownFormatter.EscapeMarkdownV2("Reply with your answer."));
        }

        return new RenderedPrompt(builder.ToString(), keyboard, values);
    }

    private static InlineKeyboardMarkup? TryBuildKeyboard(string requestId, IReadOnlyList<AskUserChoice> choices)
    {
        var buttons = new List<InlineKeyboardButton>(choices.Count);
        for (var i = 0; i < choices.Count; i++)
        {
            // All-or-nothing: one over-long token means the whole keyboard is abandoned, because a
            // partial keyboard would silently hide the choices that did not fit.
            if (!TelegramAskUserCallbackToken.TryEncode(requestId, i, out var data))
                return null;

            buttons.Add(new InlineKeyboardButton
            {
                // Button labels are NOT parsed with parse_mode, so they are sent literally.
                Text = LabelOf(choices[i]),
                CallbackData = data
            });
        }

        var rows = new List<IReadOnlyList<InlineKeyboardButton>>();
        for (var i = 0; i < buttons.Count; i += ButtonsPerRow)
            rows.Add(buttons.GetRange(i, Math.Min(ButtonsPerRow, buttons.Count - i)));

        return new InlineKeyboardMarkup { InlineKeyboard = rows };
    }

    private static string LabelOf(AskUserChoice choice)
        => string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label!;
}
