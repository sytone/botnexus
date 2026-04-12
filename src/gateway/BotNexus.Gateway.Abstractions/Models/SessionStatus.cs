namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents the lifecycle status of a gateway session.
/// </summary>
public enum SessionStatus
{
    /// <summary>The session is active and available for new messages.</summary>
    Active,

    /// <summary>The session is temporarily paused but may be resumed.</summary>
    Suspended,

    /// <summary>The session expired due to inactivity or retention policy.</summary>
    Expired,

    /// <summary>The session was sealed and should not be reused.</summary>
    Sealed
}
