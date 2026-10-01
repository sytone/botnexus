namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Optional lifecycle notification for stateful tools whose cached model-context assumptions must
/// be invalidated when the agent loop replaces its live message context after durable compaction.
/// </summary>
public interface IContextReplacementAwareTool
{
    /// <summary>Notifies the tool that prior model-visible tool results are no longer in context.</summary>
    void OnContextReplaced();
}
