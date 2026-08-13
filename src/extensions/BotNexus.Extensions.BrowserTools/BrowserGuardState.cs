namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Whether the guard layer initialised successfully, and why not when it did not (#3030 AC4).
/// </summary>
/// <remarks>
/// This type exists so that "the guards are not available" is a VALUE the session must carry and
/// branch on, rather than an exception someone can catch and continue past. Guard initialisation
/// failing is precisely the moment at which continuing is least safe, so the only state
/// reachable without a successful initialisation is the one that denies everything.
/// </remarks>
public sealed class BrowserGuardState
{
    private BrowserGuardState(bool isReady, string? failureReason)
    {
        IsReady = isReady;
        FailureReason = failureReason;
    }

    /// <summary>Whether the guards initialised and may admit calls.</summary>
    public bool IsReady { get; }

    /// <summary>Why initialisation failed; <c>null</c> when <see cref="IsReady"/> is true.</summary>
    public string? FailureReason { get; }

    /// <summary>The guards are initialised.</summary>
    public static BrowserGuardState Ready { get; } = new(true, null);

    /// <summary>The guards failed to initialise; every guarded call must be denied.</summary>
    public static BrowserGuardState Failed(string reason) => new(false, reason);

    /// <summary>
    /// Runs <paramref name="initialise"/> and converts ANY failure - thrown or reported - into a
    /// failed state. Catching broadly is correct here and nowhere else: an unanticipated
    /// initialisation fault is exactly the case where a narrow catch list would let the process
    /// continue unguarded.
    /// </summary>
    public static BrowserGuardState Initialise(Action initialise)
    {
        ArgumentNullException.ThrowIfNull(initialise);
        try
        {
            initialise();
            return Ready;
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }
}
