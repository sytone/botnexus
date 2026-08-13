namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Outcome of a guard decision (#3030).
/// </summary>
/// <remarks>
/// A struct with an explicit <see cref="IsAllowed"/> flag rather than a nullable reason string:
/// a caller that forgets to check a nullable reason silently proceeds, whereas the shape here
/// forces the allowed/denied branch to be written.
/// </remarks>
public readonly struct BrowserGuardResult
{
    /// <summary>Whether the guarded operation may proceed.</summary>
    public bool IsAllowed { get; }

    /// <summary>Human-readable reason the operation was denied; <c>null</c> when allowed.</summary>
    public string? Reason { get; }

    private BrowserGuardResult(bool isAllowed, string? reason)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    /// <summary>The operation may proceed.</summary>
    public static BrowserGuardResult Allowed { get; } = new(true, null);

    /// <summary>The operation is denied for the supplied reason.</summary>
    public static BrowserGuardResult Denied(string reason) => new(false, reason);
}
