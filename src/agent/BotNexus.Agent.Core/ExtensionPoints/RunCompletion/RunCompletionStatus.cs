namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

/// <summary>
/// Classifies host-owned work during completion evaluation and the outcome reported at run end.
/// </summary>
public enum RunCompletionStatus
{
    /// <summary>Actionable work remains and requires another bounded continuation turn.</summary>
    Working,
    /// <summary>Work is paused with a stop reason, evidence, continuation owner, and wake condition.</summary>
    Parked,
    /// <summary>The run ended with unfinished work and no valid structured stop disposition.</summary>
    IncompleteWithoutStopReason,
    /// <summary>The run completed without remaining actionable work.</summary>
    Completed,
    /// <summary>The run ended because execution failed.</summary>
    Failed,
    /// <summary>The run ended because execution was cancelled.</summary>
    Cancelled,
}