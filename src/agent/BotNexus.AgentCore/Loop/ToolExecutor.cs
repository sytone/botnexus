using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Loop;

/// <summary>
/// Executes tool calls from assistant messages in sequential or parallel mode.
/// </summary>
/// <remarks>
/// Coordinates argument validation, hook execution, and result collection.
/// Emits ToolExecutionStartEvent and ToolExecutionEndEvent for each tool.
/// In parallel mode, events are emitted in deterministic order (all starts, then all ends).
/// </remarks>
internal static class ToolExecutor
{
    /// <summary>
    /// Execute all tool calls from an assistant message.
    /// </summary>
    /// <param name="context">The current agent context.</param>
    /// <param name="assistantMessage">The assistant message containing tool calls.</param>
    /// <param name="config">The loop configuration.</param>
    /// <param name="emit">The event emission callback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Tool result messages in assistant source order.</returns>
    public static async Task<IReadOnlyList<ToolResultAgentMessage>> ExecuteAsync(
        AgentContext context,
        AssistantAgentMessage assistantMessage,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var toolCalls = assistantMessage.ToolCalls;
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return [];
        }

        return config.ToolExecutionMode == ToolExecutionMode.Sequential
            ? await ExecuteSequentialAsync(context, assistantMessage, toolCalls, config, emit, cancellationToken).ConfigureAwait(false)
            : await ExecuteParallelAsync(context, assistantMessage, toolCalls, config, emit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ToolResultAgentMessage>> ExecuteSequentialAsync(
        AgentContext context,
        AssistantAgentMessage assistantMessage,
        IReadOnlyList<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var results = new List<ToolResultAgentMessage>(toolCalls.Count);

        foreach (var toolCall in toolCalls)
        {
            var rawArgs = new Dictionary<string, object?>(toolCall.Arguments, StringComparer.Ordinal);
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, rawArgs, DateTimeOffset.UtcNow))
                .ConfigureAwait(false);

            var outcome = await ExecuteToolCallCoreAsync(context, assistantMessage, toolCall, rawArgs, config, emit, cancellationToken)
                .ConfigureAwait(false);

            await emit(new ToolExecutionEndEvent(
                toolCall.Id,
                toolCall.Name,
                outcome.Result,
                outcome.IsError,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);

            results.Add(await EmitToolResultMessageAsync(
                    toolCall,
                    outcome.Result,
                    outcome.IsError,
                    emit,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<IReadOnlyList<ToolResultAgentMessage>> ExecuteParallelAsync(
        AgentContext context,
        AssistantAgentMessage assistantMessage,
        IReadOnlyList<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var workItems = toolCalls.Select((toolCall, index) => new ToolWorkItem(
            index,
            toolCall,
            new Dictionary<string, object?>(toolCall.Arguments, StringComparer.Ordinal)))
            .ToList();

        foreach (var item in workItems)
        {
            await emit(new ToolExecutionStartEvent(
                item.ToolCall.Id,
                item.ToolCall.Name,
                item.RawArgs,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        var tasks = workItems.Select(async item =>
        {
            var outcome = await ExecuteToolCallCoreAsync(
                    context,
                    assistantMessage,
                    item.ToolCall,
                    item.RawArgs,
                    config,
                    emit,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ToolExecutionOutcome(item.Index, item.ToolCall, outcome.Result, outcome.IsError);
        });

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        var ordered = completed.OrderBy(result => result.Index).ToList();

        var results = new List<ToolResultAgentMessage>(ordered.Count);
        foreach (var outcome in ordered)
        {
            await emit(new ToolExecutionEndEvent(
                outcome.ToolCall.Id,
                outcome.ToolCall.Name,
                outcome.Result,
                outcome.IsError,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);

            results.Add(await EmitToolResultMessageAsync(
                    outcome.ToolCall,
                    outcome.Result,
                    outcome.IsError,
                    emit,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<(AgentToolResult Result, bool IsError)> ExecuteToolCallCoreAsync(
        AgentContext context,
        AssistantAgentMessage assistantMessage,
        ToolCallContent toolCall,
        IReadOnlyDictionary<string, object?> rawArgs,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tool = context.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            return (BuildErrorResult($"Tool '{toolCall.Name}' is not registered."), true);
        }

        IReadOnlyDictionary<string, object?> validatedArgs;
        try
        {
            validatedArgs = await tool.PrepareArgumentsAsync(rawArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (BuildErrorResult($"Invalid arguments for '{toolCall.Name}': {ex.Message}"), true);
        }

        if (config.BeforeToolCall is not null)
        {
            var beforeContext = new BeforeToolCallContext(assistantMessage, toolCall, validatedArgs, context);
            var beforeResult = await config.BeforeToolCall(beforeContext, cancellationToken).ConfigureAwait(false);
            if (beforeResult?.Block == true)
            {
                var reason = string.IsNullOrWhiteSpace(beforeResult.Reason)
                    ? "Tool call was blocked by policy."
                    : beforeResult.Reason!;
                return (BuildErrorResult(reason), true);
            }
        }

        AgentToolResult result;
        var isError = false;
        try
        {
            result = await tool.ExecuteAsync(
                toolCall.Id,
                validatedArgs,
                cancellationToken,
                partialResult =>
                    emit(new ToolExecutionUpdateEvent(
                        toolCall.Id,
                        toolCall.Name,
                        validatedArgs,
                        partialResult,
                        DateTimeOffset.UtcNow)).GetAwaiter().GetResult()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = BuildErrorResult($"Tool '{toolCall.Name}' failed: {ex.Message}");
            isError = true;
        }

        if (config.AfterToolCall is not null)
        {
            var afterContext = new AfterToolCallContext(
                assistantMessage,
                toolCall,
                validatedArgs,
                result,
                isError,
                context);

            var afterResult = await config.AfterToolCall(afterContext, cancellationToken).ConfigureAwait(false);
            if (afterResult is not null)
            {
                var content = afterResult.Content ?? result.Content;
                var details = afterResult.Details ?? result.Details;
                result = new AgentToolResult(content, details);
                isError = afterResult.IsError ?? isError;
            }
        }

        return (result, isError);
    }

    private static AgentToolResult BuildErrorResult(string message)
    {
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
    }

    private static async Task<ToolResultAgentMessage> EmitToolResultMessageAsync(
        ToolCallContent toolCall,
        AgentToolResult result,
        bool isError,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = DateTimeOffset.UtcNow;
        var message = new ToolResultAgentMessage(
            ToolCallId: toolCall.Id,
            ToolName: toolCall.Name,
            Result: result,
            IsError: isError,
            Timestamp: timestamp);

        await emit(new MessageStartEvent(message, timestamp)).ConfigureAwait(false);
        await emit(new MessageEndEvent(message, timestamp)).ConfigureAwait(false);
        return message;
    }

    private sealed record ToolWorkItem(
        int Index,
        ToolCallContent ToolCall,
        IReadOnlyDictionary<string, object?> RawArgs);

    private sealed record ToolExecutionOutcome(
        int Index,
        ToolCallContent ToolCall,
        AgentToolResult Result,
        bool IsError);
}
