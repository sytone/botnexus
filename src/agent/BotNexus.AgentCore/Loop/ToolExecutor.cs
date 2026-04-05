using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using System.Collections.Concurrent;

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

            var preparation = await PrepareToolCallAsync(
                    context,
                    assistantMessage,
                    toolCall,
                    rawArgs,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);

            var (result, isError) = preparation.Prepared is null
                ? (preparation.Result!, preparation.IsError)
                : await ExecutePreparedToolCallAsync(preparation.Prepared, emit, cancellationToken).ConfigureAwait(false);

            if (preparation.Prepared is not null)
            {
                (result, isError) = await ApplyAfterToolCallAsync(
                        context,
                        assistantMessage,
                        toolCall,
                        preparation.Prepared.ValidatedArgs,
                        result,
                        isError,
                        config,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await emit(new ToolExecutionEndEvent(
                toolCall.Id,
                toolCall.Name,
                result,
                isError,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);

            results.Add(await EmitToolResultMessageAsync(
                    toolCall,
                    result,
                    isError,
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
        var preparedItems = new List<PreparedToolWorkItem>(toolCalls.Count);
        var completedItems = new List<ToolExecutionOutcome>(toolCalls.Count);

        foreach (var (toolCall, index) in toolCalls.Select((toolCall, index) => (toolCall, index)))
        {
            var rawArgs = new Dictionary<string, object?>(toolCall.Arguments, StringComparer.Ordinal);
            await emit(new ToolExecutionStartEvent(
                toolCall.Id,
                toolCall.Name,
                rawArgs,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);

            var preparation = await PrepareToolCallAsync(
                    context,
                    assistantMessage,
                    toolCall,
                    rawArgs,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);

            if (preparation.Prepared is null)
            {
                completedItems.Add(new ToolExecutionOutcome(index, toolCall, preparation.Result!, preparation.IsError, null, false));
            }
            else
            {
                preparedItems.Add(new PreparedToolWorkItem(index, preparation.Prepared));
            }
        }

        var executionTasks = preparedItems.Select(async item =>
        {
            var execution = await ExecutePreparedToolCallAsync(item.Prepared, emit, cancellationToken).ConfigureAwait(false);
            return new ToolExecutionOutcome(
                item.Index,
                item.Prepared.ToolCall,
                execution.Result,
                execution.IsError,
                item.Prepared.ValidatedArgs,
                true);
        });

        completedItems.AddRange(await Task.WhenAll(executionTasks).ConfigureAwait(false));
        var ordered = completedItems.OrderBy(result => result.Index).ToList();

        var results = new List<ToolResultAgentMessage>(ordered.Count);
        foreach (var outcome in ordered)
        {
            var result = outcome.Result;
            var isError = outcome.IsError;

            if (outcome.ApplyAfterHook && outcome.ValidatedArgs is not null)
            {
                (result, isError) = await ApplyAfterToolCallAsync(
                        context,
                        assistantMessage,
                        outcome.ToolCall,
                        outcome.ValidatedArgs,
                        outcome.Result,
                        outcome.IsError,
                        config,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await emit(new ToolExecutionEndEvent(
                outcome.ToolCall.Id,
                outcome.ToolCall.Name,
                result,
                isError,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);

            results.Add(await EmitToolResultMessageAsync(
                    outcome.ToolCall,
                    result,
                    isError,
                    emit,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<ToolPreparation> PrepareToolCallAsync(
        AgentContext context,
        AssistantAgentMessage assistantMessage,
        ToolCallContent toolCall,
        IReadOnlyDictionary<string, object?> rawArgs,
        AgentLoopConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tool = context.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            return new ToolPreparation(null, BuildErrorResult($"Tool '{toolCall.Name}' is not registered."), true);
        }

        IReadOnlyDictionary<string, object?> validatedArgs;
        try
        {
            validatedArgs = await tool.PrepareArgumentsAsync(rawArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ToolPreparation(null, BuildErrorResult($"Invalid arguments for '{toolCall.Name}': {ex.Message}"), true);
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
                return new ToolPreparation(null, BuildErrorResult(reason), true);
            }
        }

        return new ToolPreparation(
            new PreparedToolCall(toolCall, tool, validatedArgs),
            null,
            false);
    }

    private static async Task<(AgentToolResult Result, bool IsError)> ExecutePreparedToolCallAsync(
        PreparedToolCall prepared,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        AgentToolResult result;
        var isError = false;
        var updateTasks = new ConcurrentBag<Task>();

        try
        {
            result = await prepared.Tool.ExecuteAsync(
                prepared.ToolCall.Id,
                prepared.ValidatedArgs,
                cancellationToken,
                partialResult => updateTasks.Add(emit(new ToolExecutionUpdateEvent(
                    prepared.ToolCall.Id,
                    prepared.ToolCall.Name,
                    prepared.ValidatedArgs,
                    partialResult,
                    DateTimeOffset.UtcNow)))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = BuildErrorResult($"Tool '{prepared.ToolCall.Name}' failed: {ex.Message}");
            isError = true;
        }

        if (!updateTasks.IsEmpty)
        {
            await Task.WhenAll(updateTasks).ConfigureAwait(false);
        }

        return (result, isError);
    }

    private static async Task<(AgentToolResult Result, bool IsError)> ApplyAfterToolCallAsync(
        AgentContext context,
        AssistantAgentMessage assistantMessage,
        ToolCallContent toolCall,
        IReadOnlyDictionary<string, object?> validatedArgs,
        AgentToolResult result,
        bool isError,
        AgentLoopConfig config,
        CancellationToken cancellationToken)
    {
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

    private sealed record PreparedToolWorkItem(
        int Index,
        PreparedToolCall Prepared);

    private sealed record PreparedToolCall(
        ToolCallContent ToolCall,
        IAgentTool Tool,
        IReadOnlyDictionary<string, object?> ValidatedArgs);

    private sealed record ToolPreparation(
        PreparedToolCall? Prepared,
        AgentToolResult? Result,
        bool IsError);

    private sealed record ToolExecutionOutcome(
        int Index,
        ToolCallContent ToolCall,
        AgentToolResult Result,
        bool IsError,
        IReadOnlyDictionary<string, object?>? ValidatedArgs,
        bool ApplyAfterHook);
}
