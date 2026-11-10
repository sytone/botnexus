using System.Globalization;

namespace BotNexus.Agent.Core.ExtensionPoints.Messages;

/// <summary>Provider-neutral live executed-result capacity; never estimates token or wall-clock budget.</summary>
public static class DefaultProviderRequestContextProvider
{
    /// <summary>Renders factual capacity and phase-specific guidance without changing execution policy.</summary>
    /// <param name="context">Current authoritative run accounting.</param>
    /// <param name="cancellationToken">Request cancellation, which propagates before rendering.</param>
    /// <returns>Exactly one dynamic capacity section.</returns>
    public static Task<string?> RenderAsync(ProviderRequestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capacity = context.ToolCapacity;
        var guidance = capacity.Remaining == 0
            ? "Capacity is exhausted. Summarize existing evidence and unresolved work only; do not request tools or claim completion."
            : capacity.Used * 100 >= capacity.Limit * 90L
                ? "Capacity is nearly exhausted. Prioritize essential work and retain evidence for an honest handoff."
                : capacity.Used * 100 >= capacity.Limit * 75L
                    ? "Capacity is running low. Avoid redundant work and prioritize the remaining required checks."
                    : "Use tools deliberately; this is an executed-result budget, not a token or time estimate.";
        return Task.FromResult<string?>(string.Create(CultureInfo.InvariantCulture,
            $"[Live tool capacity] used={capacity.Used}; limit={capacity.Limit}; remaining={capacity.Remaining}; phase={capacity.Phase}. {guidance} Do not skip required checks or automatically restart to evade this limit. Avoid repeating these counters in normal replies."));
    }
}
