namespace BotNexus.Agent.Core.Tools;

/// <summary>Reports whether a command tool retains active or uncertain owned execution.</summary>
/// <remarks>Unknown is not stopped. Used only while the host also holds run admission. Implementations
/// must retain uncertainty after timeout/cancellation and never inspect or attach arbitrary OS PIDs.</remarks>
public interface ICommandRecoveryState
{
    /// <summary>Safe only after confirmed tracked completion; otherwise a fixed reason code.</summary>
    string RecoverySafety { get; }
}
