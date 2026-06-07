using System.Diagnostics;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>
/// Resolves the <c>X-Interaction-Id</c> value a Copilot request should carry.
/// Captures show the real Copilot CLI sends either a fresh GUID per call or
/// the current trace context — never a reused id — so this helper deliberately
/// generates a new value when no explicit override is supplied.
/// </summary>
public static class CopilotInteractionId
{
    /// <summary>
    /// Return the explicit override if non-empty; otherwise prefer the
    /// current <see cref="Activity"/> id (so the Copilot side correlates with
    /// the local trace), and fall back to a fresh GUID when no Activity is
    /// active.
    /// </summary>
    public static string Resolve(CopilotHeaderOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.InteractionId))
            return options!.InteractionId!;

        var current = Activity.Current;
        if (current is { Id.Length: > 0 } a)
            return a.Id!;

        return Guid.NewGuid().ToString("D");
    }

    /// <summary>
    /// Return a copy of <paramref name="options"/> with
    /// <see cref="CopilotHeaderOptions.InteractionId"/> populated. When
    /// <paramref name="options"/> is null this returns null so the default
    /// header set is preserved unchanged.
    /// </summary>
    public static CopilotHeaderOptions? WithResolvedInteractionId(CopilotHeaderOptions? options)
    {
        if (options is null)
            return null;

        if (!string.IsNullOrEmpty(options.InteractionId))
            return options;

        return options with { InteractionId = Resolve(options) };
    }
}
