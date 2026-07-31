namespace BotNexus.Extensions.ProcessTool;

/// <summary>Outcome of a termination request against a <see cref="ManagedProcess"/>.</summary>
public enum ProcessKillState
{
    /// <summary>No termination has been requested.</summary>
    NotRequested = 0,

    /// <summary>Termination was requested and the process was observed to exit.</summary>
    Confirmed = 1,

    /// <summary>
    /// Termination was requested but exit was never observed within the grace period. The process
    /// or one of its descendants may still be alive, so the registration must be retained.
    /// </summary>
    Unconfirmed = 2,
}
