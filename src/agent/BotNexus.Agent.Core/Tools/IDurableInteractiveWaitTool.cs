namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Marks an interactive tool whose wait is durably persisted and remains independently answerable
/// or cancellable after the original executing turn disappears.
/// </summary>
/// <remarks>
/// Omitting the tool's declared timeout argument means no caller expiry, so the executor does not
/// apply its generic per-tool deadline. Supplying that argument restores ordinary executor timeout
/// budgeting around the tool's own terminal expiry behavior.
/// </remarks>
public interface IDurableInteractiveWaitTool : IAgentTool;
