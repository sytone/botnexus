namespace BotNexus.Agent.Core.ExtensionPoints.ToolExecution;

/// <summary>
/// Reports whether a validated, audited tool call will proceed to execution.
/// </summary>
/// <param name="toolCallId">Provider tool-call correlation id.</param>
/// <param name="willExecute">True only after the policy gate permits execution.</param>
public delegate void ToolExecutionDecisionObserver(string toolCallId, bool willExecute);