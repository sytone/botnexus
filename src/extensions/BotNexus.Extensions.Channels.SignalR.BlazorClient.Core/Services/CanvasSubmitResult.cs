namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Outcome of a canvas <c>submitToAgent</c> request (#2449).
/// </summary>
/// <param name="Accepted">
/// <see langword="true"/> when the prompt was injected into the owning conversation as a
/// genuine user turn; <see langword="false"/> when a guard rejected it.
/// </param>
/// <param name="Reason">
/// Human-readable rejection reason, surfaced to the iframe as a Promise rejection. Always
/// <see langword="null"/> when <paramref name="Accepted"/> is <see langword="true"/>.
/// </param>
public sealed record CanvasSubmitResult(bool Accepted, string? Reason)
{
    /// <summary>Creates an accepted result.</summary>
    public static CanvasSubmitResult Ok() => new(true, null);

    /// <summary>Creates a rejected result carrying the guard that refused the submission.</summary>
    public static CanvasSubmitResult Rejected(string reason) => new(false, reason);
}
