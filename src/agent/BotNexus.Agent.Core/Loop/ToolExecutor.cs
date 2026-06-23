using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Validation;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BotNexus.Agent.Core.Loop;

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
                : await ExecutePreparedToolCallAsync(preparation.Prepared, emit, cancellationToken, config.ToolTimeout).ConfigureAwait(false);

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
        var resultSlots = new ToolResultAgentMessage?[toolCalls.Count];

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
                var immediateResult = preparation.Result!;
                await emit(new ToolExecutionEndEvent(
                    toolCall.Id,
                    toolCall.Name,
                    immediateResult,
                    preparation.IsError,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);

                resultSlots[index] = await EmitToolResultMessageAsync(
                        toolCall,
                        immediateResult,
                        preparation.IsError,
                        emit,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                preparedItems.Add(new PreparedToolWorkItem(index, preparation.Prepared));
            }
        }

        var executionTasks = preparedItems.Select(async item =>
        {
            var execution = await ExecutePreparedToolCallAsync(item.Prepared, emit, cancellationToken, config.ToolTimeout).ConfigureAwait(false);
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

            resultSlots[outcome.Index] = await EmitToolResultMessageAsync(
                    outcome.ToolCall,
                    result,
                    isError,
                    emit,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return resultSlots.Where(result => result is not null).Select(result => result!).ToList();
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

        var argumentElement = JsonSerializer.SerializeToElement(rawArgs);
        var (isValid, errors) = ToolCallValidator.Validate(argumentElement, tool.Definition.Parameters, out var coercedElement);
        if (!isValid)
        {
            return new ToolPreparation(
                null,
                BuildErrorResult($"Invalid arguments for '{toolCall.Name}': {string.Join("; ", errors)}"),
                true);
        }

        // The validator may have coerced losslessly-safe shape mismatches (e.g. a
        // string-encoded integer or a scalar where an array was expected, issue #1552).
        // Dispatch the corrected shape so the tool receives the fixed arguments rather
        // than the original — otherwise a coerced-but-dropped value (e.g. a scalar tag)
        // would be silently lost downstream.
        var dispatchArgs = ApplyCoercedArguments(rawArgs, coercedElement);

        IReadOnlyDictionary<string, object?> validatedArgs;
        try
        {
            validatedArgs = await tool.PrepareArgumentsAsync(dispatchArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ToolPreparation(null, BuildErrorResult($"Invalid arguments for '{toolCall.Name}': {ex.Message}"), true);
        }

        if (config.BeforeToolCall is not null)
        {
            var beforeContext = new BeforeToolCallContext(assistantMessage, toolCall, validatedArgs, context);
            BeforeToolCallResult? beforeResult;
            try
            {
                beforeResult = await config.BeforeToolCall(beforeContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ToolPreparation(
                    null,
                    BuildErrorResult($"BeforeToolCall hook failed: {ex.Message}"),
                    true);
            }

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
        CancellationToken cancellationToken,
        TimeSpan? toolTimeout = null)
    {
        AgentToolResult result;
        var isError = false;
        var updateTasks = new ConcurrentBag<Task>();

        // If the tool call includes an explicit timeout argument, respect it.
        // Tools like ShellTool (timeout: seconds) and ExecTool (timeoutMs: ms) expose this.
        // Also check tool.DefaultTimeout — tools declare their own expected duration.
        // Use the largest of: configured safety cap, tool default, agent-requested arg timeout.
        var effectiveTimeout = toolTimeout;

        // Tool-declared default — long-running tools (shell, exec, mcp) set this
        if (prepared.Tool.DefaultTimeout.HasValue)
        {
            effectiveTimeout = effectiveTimeout.HasValue
                ? (TimeSpan?)TimeSpan.FromTicks(Math.Max(effectiveTimeout.Value.Ticks, prepared.Tool.DefaultTimeout.Value.Ticks))
                : prepared.Tool.DefaultTimeout;
        }

        // Agent-specified timeout in arguments (timeout: seconds or timeoutMs: ms)
        // Honours explicit agent intent — e.g. "run this deploy script, timeout: 600"
        if (effectiveTimeout.HasValue)
        {
            TimeSpan? requested = null;
            if (prepared.ValidatedArgs.TryGetValue("timeout", out var rawSec) && rawSec is not null
                && int.TryParse(rawSec.ToString(), out var sec) && sec > 0)
            {
                requested = TimeSpan.FromSeconds(sec);
            }
            else if (prepared.ValidatedArgs.TryGetValue("timeoutMs", out var rawMs) && rawMs is not null
                && int.TryParse(rawMs.ToString(), out var ms) && ms > 0)
            {
                requested = TimeSpan.FromMilliseconds(ms);
            }

            if (requested.HasValue && toolTimeout.HasValue && requested.Value > toolTimeout.Value)
            {
                // Agent explicitly requested a longer timeout — honour it with a 10s buffer
                // so the tool's own timeout fires before the safety cap.
                effectiveTimeout = requested.Value + TimeSpan.FromSeconds(10);
            }
        }

        // Create a linked CancellationTokenSource for the per-tool timeout if configured.
        using var timeoutCts = effectiveTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutCts is not null && effectiveTimeout.HasValue)
        {
            timeoutCts.CancelAfter(effectiveTimeout.Value);
        }
        var effectiveToken = timeoutCts?.Token ?? cancellationToken;

        try
        {
            result = await prepared.Tool.ExecuteAsync(
                prepared.ToolCall.Id,
                prepared.ValidatedArgs,
                effectiveToken,
                partialResult => updateTasks.Add(emit(new ToolExecutionUpdateEvent(
                    prepared.ToolCall.Id,
                    prepared.ToolCall.Name,
                    prepared.ValidatedArgs,
                    partialResult,
                    DateTimeOffset.UtcNow)))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Tool timed out (not user/turn cancellation) — return structured error to LLM.
            result = BuildErrorResult($"Tool '{prepared.ToolCall.Name}' timed out after {effectiveTimeout!.Value.TotalSeconds:0}s. The operation did not complete.");
            isError = true;
        }
        catch (Exception ex)
        {
            result = BuildErrorResult(ex.Message);
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

            AfterToolCallResult? afterResult;
            try
            {
                afterResult = await config.AfterToolCall(afterContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (result, isError);
            }

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

    /// <summary>
    /// Returns the dispatch arguments for the tool. When the validator coerced one or more
    /// values (issue #1552), the coerced JSON object is unpacked back into a
    /// <see cref="Dictionary{TKey, TValue}"/> (each value a cloned <see cref="JsonElement"/>,
    /// which every tool argument reader already accepts) so the tool sees the corrected
    /// shape. When nothing was coerced the original <paramref name="rawArgs"/> is returned
    /// unchanged to avoid needless reallocation.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ApplyCoercedArguments(
        IReadOnlyDictionary<string, object?> rawArgs,
        JsonElement coercedElement)
    {
        if (coercedElement.ValueKind != JsonValueKind.Object)
        {
            return rawArgs;
        }

        var original = JsonSerializer.SerializeToElement(rawArgs);
        if (string.Equals(original.GetRawText(), coercedElement.GetRawText(), StringComparison.Ordinal))
        {
            return rawArgs;
        }

        var coercedArgs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in coercedElement.EnumerateObject())
        {
            coercedArgs[property.Name] = property.Value.Clone();
        }

        return coercedArgs;
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
