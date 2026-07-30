using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Validation;
using System.Collections.Concurrent;
using System.Diagnostics;
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

            // #2518: the pre-tool-call hook is the pre-execution policy gate (it enforces the
            // tool-approval posture shipped in #2397). An approval provider that wedges -- a stalled
            // prompt, an unreachable policy service, a deadlocked store -- would otherwise hang the
            // whole agent turn, because a cron or channel turn may carry no ambient deadline at all.
            // Bound it, and on breach fail CLOSED: block the call, exactly like the exception path
            // below. Allowing execution on a timeout would turn a liveness bug into a policy bypass.
            var budget = config.BeforeToolCallTimeout ?? AgentLoopConfig.DefaultBeforeToolCallTimeout;
            var budgetEnabled = budget > TimeSpan.Zero && budget != Timeout.InfiniteTimeSpan;

            using var hookCts = budgetEnabled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (hookCts is not null)
            {
                hookCts.CancelAfter(budget);
            }

            var hookToken = hookCts?.Token ?? cancellationToken;
            var startedAt = Stopwatch.GetTimestamp();

            try
            {
                beforeResult = await config.BeforeToolCall(beforeContext, hookToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                hookCts is not null &&
                hookCts.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                // Budget breach, not turn cancellation. The ambient token is untouched, so this is
                // unambiguously the hook overrunning its own deadline.
                return BuildBeforeToolCallTimeout(config, toolCall, budget, startedAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ToolPreparation(
                    null,
                    BuildErrorResult($"BeforeToolCall hook failed: {ex.Message}"),
                    true);
            }

            // A hook that swallows its cancellation token and returns normally after the budget
            // elapsed must not be treated as a policy decision either -- it produced its answer
            // outside the window it was given.
            if (hookCts is not null && hookCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return BuildBeforeToolCallTimeout(config, toolCall, budget, startedAt);
            }

            // Genuine turn cancellation still propagates as cancellation, never as a hook timeout.
            cancellationToken.ThrowIfCancellationRequested();

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
    /// Builds the fail-closed outcome for a pre-tool-call hook that exceeded its budget (#2518),
    /// and reports the breach through the diagnostic sink with the elapsed time and the tool
    /// identity so a slow policy provider is nameable rather than merely mysterious.
    /// </summary>
    private static ToolPreparation BuildBeforeToolCallTimeout(
        AgentLoopConfig config,
        ToolCallContent toolCall,
        TimeSpan budget,
        long startedAt)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var message =
            $"BeforeToolCall hook timed out after {elapsed.TotalSeconds:F1}s " +
            $"(budget {budget.TotalSeconds:F1}s) for tool '{toolCall.Name}' (call {toolCall.Id}). " +
            "Tool call blocked because no policy decision was reached.";

        try
        {
            config.OnDiagnostic?.Invoke(message);
        }
        catch
        {
            // A misbehaving diagnostic sink must never mask the fail-closed outcome.
        }

        return new ToolPreparation(null, BuildErrorResult(message), true);
    }

    /// <summary>
    /// Projects the validated arguments into the shape dispatched to the tool. When the validator
    /// coerced one or more losslessly-safe shape mismatches (issue #1552) the coerced JSON object is
    /// unpacked into cloned <see cref="JsonElement"/> values (the shape every tool argument reader
    /// already accepts). Independently — whether or not any coercion fired — boxed CLR numbers are
    /// normalised to <see cref="JsonElement"/> numbers: streaming tool-call parsing boxes JSON numbers
    /// as CLR <see cref="long"/>/<see cref="double"/>, and a tool argument reader that only recognised
    /// <see cref="JsonElement"/> numbers rejected them, so a valid numeric argument (e.g. an
    /// <c>agent_converse</c> <c>timeoutSeconds</c>) failed unless an unrelated sibling argument
    /// happened to trigger coercion (issue #2415). On the no-coercion path, non-numeric values keep
    /// their original CLR representation so no other dispatch behaviour changes; when coercion fires,
    /// every value is taken from the coerced <see cref="JsonElement"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ApplyCoercedArguments(
        IReadOnlyDictionary<string, object?> rawArgs,
        JsonElement coercedElement)
    {
        if (coercedElement.ValueKind != JsonValueKind.Object)
        {
            return NormalizeBoxedNumbers(rawArgs);
        }

        var original = JsonSerializer.SerializeToElement(rawArgs);
        if (string.Equals(original.GetRawText(), coercedElement.GetRawText(), StringComparison.Ordinal))
        {
            return NormalizeBoxedNumbers(rawArgs);
        }

        var coercedArgs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in coercedElement.EnumerateObject())
        {
            coercedArgs[property.Name] = property.Value.Clone();
        }

        return coercedArgs;
    }

    /// <summary>
    /// Returns a copy of <paramref name="args"/> where each boxed CLR number produced by streaming
    /// tool-call argument parsing is replaced with an equivalent <see cref="JsonElement"/> number, so
    /// tool argument readers see a uniform <see cref="JsonElement"/> regardless of how the provider
    /// boxed the value (issue #2415). Values that are already <see cref="JsonElement"/> or are
    /// non-numeric are preserved verbatim. The original instance is returned when nothing needs
    /// normalising to avoid needless reallocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="long"/> and finite <see cref="double"/> are normalised: <c>StreamingJsonParser</c>
    /// boxes JSON integers as <see cref="long"/> and non-integers as <see cref="double"/>, so those are
    /// the only CLR numeric types a boxed JSON number can arrive as. A boxed <see cref="int"/> is left
    /// untouched — every numeric tool argument reader already accepts <see cref="int"/> directly, and
    /// rewriting it would needlessly perturb readers that pattern-match <c>is int</c>. A non-finite
    /// <see cref="double"/> has no JSON number representation and is left boxed rather than serialised
    /// here; this arm is unreachable in practice because <c>StreamingJsonParser</c> cannot produce
    /// <c>NaN</c>/<c>Infinity</c> from JSON input.
    /// </para>
    /// <para>
    /// Normalisation is intentionally top-level only. <c>StreamingJsonParser</c> maps nested JSON
    /// objects to nested <see cref="Dictionary{TKey, TValue}"/> instances, so a boxed number nested
    /// inside an object argument is not rewritten here; no current tool takes an object-valued numeric
    /// argument, so this is a latent limitation rather than a live gap.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> NormalizeBoxedNumbers(IReadOnlyDictionary<string, object?> args)
    {
        var needsNormalization = false;
        foreach (var value in args.Values)
        {
            if (value is long || (value is double d && double.IsFinite(d)))
            {
                needsNormalization = true;
                break;
            }
        }

        if (!needsNormalization)
        {
            return args;
        }

        var normalized = new Dictionary<string, object?>(args.Count, StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            normalized[key] = value switch
            {
                long l => JsonSerializer.SerializeToElement(l),
                double d when double.IsFinite(d) => JsonSerializer.SerializeToElement(d),
                _ => value
            };
        }

        return normalized;
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
