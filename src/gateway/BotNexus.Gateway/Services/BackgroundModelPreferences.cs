using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Services;

/// <summary>
/// The models background LLM work should reach for first, in preference order.
/// </summary>
/// <remarks>
/// <para>
/// Titling a conversation and summarising one for compaction are small, low-stakes jobs that no
/// user is waiting on and that a cheap model does perfectly well. Running them on whichever model
/// happens to be to hand is the single easiest way to spend frontier-model rates on a five-word
/// title.
/// </para>
/// <para>
/// The list lives here rather than beside either caller so the two agree on what counts as cheap
/// enough. It carries both the Copilot-flavoured and the dated Anthropic id for Haiku, because an
/// installation configured with only one of those routes would otherwise fall past the whole list.
/// </para>
/// <para>
/// Selection iterates the preference order, never the registry's order: the model registry is a
/// concurrent dictionary whose enumeration order is not stable, so anything that picks "the first
/// available model" is picking an arbitrary one that can differ between runs.
/// </para>
/// </remarks>
public static class BackgroundModelPreferences
{
    /// <summary>
    /// Model ids to try, cheapest and most broadly available first.
    /// </summary>
    public static IReadOnlyList<string> PreferredIds { get; } =
    [
        "gpt-4.1-mini",
        "gpt-5-mini",
        "claude-haiku-4.5",
        "claude-haiku-4-5-20251001",
        "gpt-4.1"
    ];

    /// <summary>
    /// First preferred model present in <paramref name="available"/>, or <c>null</c> when none of
    /// them is registered.
    /// </summary>
    /// <param name="available">Every registered model.</param>
    public static LlmModel? FirstAvailable(IReadOnlyList<LlmModel> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        foreach (var preferredId in PreferredIds)
        {
            foreach (var model in available)
            {
                if (string.Equals(model.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                    return model;
            }
        }

        return null;
    }
}
