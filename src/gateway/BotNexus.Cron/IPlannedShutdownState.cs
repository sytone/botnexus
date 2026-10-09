namespace BotNexus.Cron;

/// <summary>
/// Exposes the gateway lifecycle classification needed to give interrupted cron runs a truthful
/// durable disposition without coupling the cron scheduler to the gateway host implementation.
/// </summary>
public interface IPlannedShutdownState
{
    /// <summary>Whether the current host stop was explicitly requested as a planned shutdown.</summary>
    bool CurrentShutdownIsPlanned { get; }

    /// <summary>Whether startup classified the immediately preceding host stop as planned.</summary>
    bool PreviousShutdownWasPlanned { get; }
}
