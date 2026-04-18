using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Tools;
using BotNexus.CodingAgent.Session;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.CodingAgent.Extensions;

/// <summary>
/// Extension contract for adding capabilities to the coding agent.
/// Extensions are loaded from assemblies in the extensions directory.
/// </summary>
public interface IExtension
{
    /// <summary>
    /// Extension name for display and logging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns the tools this extension provides.
    /// </summary>
    IReadOnlyList<IAgentTool> GetTools();

    ValueTask<BeforeToolCallResult?> OnToolCallAsync(
        ToolCallLifecycleContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<BeforeToolCallResult?>(null);

    ValueTask<AfterToolCallResult?> OnToolResultAsync(
        ToolResultLifecycleContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AfterToolCallResult?>(null);

    ValueTask OnSessionStartAsync(
        SessionLifecycleContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask OnSessionEndAsync(
        SessionLifecycleContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask<string?> OnCompactionAsync(
        CompactionLifecycleContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    ValueTask<object?> OnModelRequestAsync(
        ModelRequestLifecycleContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<object?>(null);
}

public enum ToolCallLifecycleStage
{
    BeforeExecution,
    AfterExecution
}

public sealed record ToolCallLifecycleContext(
    ToolCallLifecycleStage Stage,
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    bool IsError = false);

public sealed record ToolResultLifecycleContext(
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    BotNexus.Agent.Core.Types.AgentToolResult Result,
    bool IsError);

public sealed record SessionLifecycleContext(
    SessionInfo Session,
    string WorkingDirectory,
    string ModelId);

public sealed record CompactionLifecycleContext(
    IReadOnlyList<BotNexus.Agent.Core.Types.AgentMessage> MessagesToSummarize,
    IReadOnlyList<BotNexus.Agent.Core.Types.AgentMessage> RecentMessages,
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles,
    string Summary);

public sealed record ModelRequestLifecycleContext(
    object Payload,
    LlmModel Model);
