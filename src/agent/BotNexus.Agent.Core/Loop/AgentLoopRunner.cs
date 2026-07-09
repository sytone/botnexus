using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Agent loop that works with AgentMessage throughout.
/// Transforms to provider Message[] only at the LLM call boundary.
/// </summary>
/// <remarks>
/// Implements the core turn loop: drain steering → call LLM → execute tools → drain steering → repeat.
/// Follow-up messages trigger an additional loop cycle after all turns settle.
/// </remarks>
public static class AgentLoopRunner
{
    /// <summary>
    /// Start a new agent run by appending prompts to the context timeline.
    /// </summary>
    /// <param name="prompts">The messages to append before running the loop.</param>
    /// <param name="context">The current agent context.</param>
    /// <param name="config">The loop configuration.</param>
    /// <param name="emit">The event emission callback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All new messages produced during the run.</returns>
    /// <remarks>
    /// Emits AgentStartEvent, TurnStartEvent, then MessageStart/End for each prompt,
    /// then enters the main loop. Used by Agent.PromptAsync.
    /// </remarks>
    public static async Task<IReadOnlyList<AgentMessage>> RunAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var runStartIndex = context.Messages.Count;
        var timeline = context.Messages.ToList();
        var newMessages = new List<AgentMessage>(prompts.Count);
        var metrics = new RunMetricsAccumulator(DateTimeOffset.UtcNow);

        await emit(new AgentStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await emit(new TurnStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);

        foreach (var prompt in prompts)
        {
            await emit(new MessageStartEvent(prompt, DateTimeOffset.UtcNow)).ConfigureAwait(false);
            timeline.Add(prompt);
            newMessages.Add(prompt);
            await emit(new MessageEndEvent(prompt, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        await RunLoopAsync(
                new AgentContext(context.SystemPrompt, timeline, context.Tools),
                newMessages,
                config,
                emit,
                metrics,
                cancellationToken,
                runStartIndex,
                firstTurn: true)
            .ConfigureAwait(false);

        return newMessages;
    }

    /// <summary>
    /// Continue an agent loop from the current context without adding a new message.
    /// </summary>
    /// <param name="context">The current agent context.</param>
    /// <param name="config">The loop configuration.</param>
    /// <param name="emit">The event emission callback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All new messages produced during the run.</returns>
    /// <remarks>
    /// <para>
    /// Used for retries when the context already has a user message or tool results.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> The last message in context must convert to a user or tool result message
    /// via ConvertToLlm. If it doesn't, the LLM provider will reject the request.
    /// </para>
    /// <para>
    /// Throws InvalidOperationException if the last message is from the assistant.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<AgentMessage>> ContinueAsync(
        AgentContext context,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Messages.Count == 0)
        {
            throw new InvalidOperationException("Cannot continue: no messages in context");
        }

        if (context.Messages[^1] is AssistantAgentMessage)
        {
            throw new InvalidOperationException("Cannot continue when the last message is from the assistant.");
        }

        var runStartIndex = context.Messages.Count;
        var newMessages = new List<AgentMessage>();
        var metrics = new RunMetricsAccumulator(DateTimeOffset.UtcNow);
        await emit(new AgentStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await emit(new TurnStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);

        await RunLoopAsync(context, newMessages, config, emit, metrics, cancellationToken, runStartIndex, firstTurn: true)
            .ConfigureAwait(false);

        return newMessages;
    }

    private static async Task RunLoopAsync(
        AgentContext currentContext,
        List<AgentMessage> newMessages,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        RunMetricsAccumulator metrics,
        CancellationToken cancellationToken,
        int runStartIndex,
        bool firstTurn)
    {
        var messages = currentContext.Messages.ToList();
        IReadOnlyList<AgentMessage> followUpSeed = [];

        // Post-turn claim auditor (#1600, #1661): each completed turn is audited against the
        // tools that executed ON THAT TURN, so a no-tool fabrication turn is flagged even
        // when an earlier turn in the same run used a backing tool. Auditing is turn-scoped
        // rather than run-scoped to match the prompt trip-wire ("a matching tool result in
        // THIS turn"); the final turn is audited by the same per-turn pass.

        while (true)
        {
            // #1710: re-check auto-compaction at the top of every outer iteration. A single long
            // dispatch (cron / autonomous follow-up loop) can blow past the token threshold mid-run;
            // pre-turn ShouldCompact at the gateway never sees it, so the transcript grew unbounded
            // until provider overflow. The hook compacts off-loop and resyncs history; best-effort so
            // a compactor failure never aborts the run.
            await MaybeCompactAsync(config, cancellationToken).ConfigureAwait(false);

            var pendingMessages = followUpSeed.Count > 0
                ? followUpSeed.ToList()
                : (config.SkipInitialSteeringPoll
                    ? []
                    : (await GetMessagesAsync(config.GetSteeringMessages, cancellationToken).ConfigureAwait(false)).ToList());
            config = config with { SkipInitialSteeringPoll = false };
            followUpSeed = [];

            var hasMoreToolCalls = true;

            // #1845: system-injected side turns (e.g. a pre-compaction memory flush) arrive as
            // steered messages carrying UserMessage.DeferWhileBusy. Held aside here while the run
            // still has pending tool calls so the flush cannot consume the loop's continuation and
            // abandon the original in-flight task. Released only at a genuine idle boundary, where
            // it runs as a normal user turn after the original work has settled.
            var deferredMessages = new List<AgentMessage>();

            while (hasMoreToolCalls || pendingMessages.Count > 0 || deferredMessages.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Release deferred (defer-while-busy) messages only once the original run is idle:
                // no more tool calls in flight and nothing else already queued for this turn.
                if (!hasMoreToolCalls && pendingMessages.Count == 0 && deferredMessages.Count > 0)
                {
                    pendingMessages = deferredMessages;
                    deferredMessages = new List<AgentMessage>();
                }

                if (!firstTurn)
                {
                    await emit(new TurnStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);
                }
                else
                {
                    firstTurn = false;
                }

                foreach (var pending in pendingMessages)
                {
                    await emit(new MessageStartEvent(pending, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    messages.Add(pending);
                    newMessages.Add(pending);
                    await emit(new MessageEndEvent(pending, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                }

                pendingMessages.Clear();

                var streamOptions = await BuildStreamOptionsAsync(config, cancellationToken).ConfigureAwait(false);
                var messageCountBeforeAssistant = messages.Count;
                var assistantMessage = await ExecuteWithRetryAsync(
                        messages,
                        currentContext.SystemPrompt,
                        currentContext.Tools,
                        config,
                        streamOptions,
                        emit,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (messages.Count > messageCountBeforeAssistant)
                {
                    messages[messages.Count - 1] = assistantMessage;
                }
                else
                {
                    messages.Add(assistantMessage);
                }

                newMessages.Add(assistantMessage);

                // #1709: opus (via github-copilot) sometimes leaks a tool call as invoke/tool_use
                // XML in the assistant TEXT channel with a non-ToolUse finish reason, so the
                // continuation guard below never dispatches it. Recover such leaked calls before the
                // guard: parse the markup into real tool calls, strip it from the text, and promote
                // the turn to ToolUse. Behaviour-preserving for a genuine ToolUse turn or any turn
                // with no recoverable markup. Complements the Tier 1 sanitizer (#1699) that only
                // strips the markup for delivery.
                if (assistantMessage.FinishReason != StopReason.ToolUse
                    && assistantMessage.ToolCalls is not { Count: > 0 })
                {
                    var recovery = LeakedToolCallRecovery.Recover(assistantMessage.Content);
                    if (recovery.RecoveredCalls.Count > 0)
                    {
                        assistantMessage = assistantMessage with
                        {
                            Content = recovery.CleanedText,
                            ToolCalls = recovery.RecoveredCalls,
                            FinishReason = StopReason.ToolUse,
                        };
                        messages[messages.Count - 1] = assistantMessage;
                        newMessages[newMessages.Count - 1] = assistantMessage;
                    }
                }
                if (assistantMessage.FinishReason is StopReason.Error or StopReason.Aborted)
                {
                    metrics.IncrementTurns();
                    metrics.AddTokens(assistantMessage.Usage?.InputTokens, assistantMessage.Usage?.OutputTokens);
                    await emit(new TurnEndEvent(assistantMessage, [], DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    var endTime = DateTimeOffset.UtcNow;
                    await emit(new AgentEndEvent(messages.Skip(runStartIndex).ToList(), metrics.ToMetrics(endTime), endTime)).ConfigureAwait(false);
                    return;
                }

                // #1666: dispatch tool calls only when the provider terminated the turn with
                // a ToolUse stop reason. A truncated turn (Length/content filter/stream EOF)
                // can still surface a parsed -- but half-formed -- tool call; executing it
                // would run with incomplete/garbled arguments. Every provider promotes a
                // legitimate, complete tool call to ToolUse at the parser/provider layer, so
                // this guard blocks only the truncated case and never a real tool turn.
                hasMoreToolCalls = assistantMessage.FinishReason == StopReason.ToolUse
                    && assistantMessage.ToolCalls is { Count: > 0 };
                var executionContext = new AgentContext(
                    currentContext.SystemPrompt,
                    messages,
                    currentContext.Tools);

                var toolResults = hasMoreToolCalls
                    ? await ToolExecutor.ExecuteAsync(
                            executionContext,
                            assistantMessage,
                            config,
                            emit,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : [];

                var turnToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var toolResult in toolResults)
                {
                    messages.Add(toolResult);
                    newMessages.Add(toolResult);
                    turnToolNames.Add(toolResult.ToolName);
                }

                metrics.IncrementTurns();
                metrics.AddTokens(assistantMessage.Usage?.InputTokens, assistantMessage.Usage?.OutputTokens);
                metrics.AddToolCalls(toolResults.Count);

                await emit(new TurnEndEvent(assistantMessage, toolResults, DateTimeOffset.UtcNow))
                    .ConfigureAwait(false);

                // Audit this turn's user-facing message against the tools that ran on THIS
                // turn only (#1661). Run-scoped auditing previously let an earlier tool turn
                // "back" a later no-tool fabrication turn for the whole run.
                await AuditClaimsAsync(config, assistantMessage, turnToolNames, emit).ConfigureAwait(false);

                var drained = (await GetMessagesAsync(config.GetSteeringMessages, cancellationToken).ConfigureAwait(false))
                    .ToList();

                // #1845: a defer-while-busy message (memory flush) that lands mid-flight is pulled
                // out of this turn's injection set and held until the loop reaches idle. Only the
                // mid-loop drain is filtered; the initial steering poll is left untouched so the
                // genuinely-idle flush path is unchanged.
                ExtractDeferWhileBusy(drained, deferredMessages);
                pendingMessages = drained;
            }

            var followUps = await GetMessagesAsync(config.GetFollowUpMessages, cancellationToken).ConfigureAwait(false);
            if (followUps.Count > 0)
            {
                followUpSeed = followUps;
                continue;
            }

            break;
        }

        var endTime2 = DateTimeOffset.UtcNow;
        await emit(new AgentEndEvent(messages.Skip(runStartIndex).ToList(), metrics.ToMetrics(endTime2), endTime2)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the post-turn claim auditor (#1600, #1661) over a single completed turn's
    /// user-facing assistant message and emits a <see cref="ClaimAuditEvent"/> when one or
    /// more artifact-shaped claims have no backing tool call <em>on that turn</em>. No-op
    /// when the auditor is not configured, is disabled, the turn produced no assistant
    /// message, or the message was an error/abort placeholder.
    /// </summary>
    /// <param name="config">The loop configuration carrying the auditor options.</param>
    /// <param name="turnMessage">The assistant message produced on the turn being audited.</param>
    /// <param name="turnToolNames">The names of the tools that executed on this turn only.</param>
    /// <param name="emit">The event emission callback.</param>
    private static async Task AuditClaimsAsync(
        AgentLoopConfig config,
        AssistantAgentMessage? turnMessage,
        IReadOnlySet<string> turnToolNames,
        Func<AgentEvent, Task> emit)
    {
        if (config.ClaimAudit is not { Enabled: true } options || turnMessage is null)
        {
            return;
        }

        // Only audit a real, completed user-facing message. Error/abort placeholders are
        // surfaced through their own paths and are not narration to verify.
        if (turnMessage.FinishReason is StopReason.Error or StopReason.Aborted)
        {
            return;
        }

        var result = ClaimAuditor.Audit(turnMessage.Content, turnToolNames, options);
        if (!result.HasUnbackedClaims)
        {
            return;
        }

        await emit(new ClaimAuditEvent(result, turnMessage, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    private static async Task<SimpleStreamOptions> BuildStreamOptionsAsync(
        AgentLoopConfig config,
        CancellationToken cancellationToken)
    {
        var options = CloneOptions(config.GenerationSettings, cancellationToken);
        var apiKey = await config.GetApiKey(config.Model.Provider, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options = options with { ApiKey = apiKey };
        }

        return options;
    }

    private static SimpleStreamOptions CloneOptions(SimpleStreamOptions source, CancellationToken cancellationToken)
    {
        return source with
        {
            CancellationToken = cancellationToken,
            Headers = source.Headers is null ? null : new Dictionary<string, string>(source.Headers),
            Metadata = source.Metadata is null ? null : new Dictionary<string, object>(source.Metadata),
        };
    }

    private static async Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
        GetMessagesDelegate? getMessages,
        CancellationToken cancellationToken)
    {
        if (getMessages is null)
        {
            return [];
        }

        return await getMessages(cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Moves any defer-while-busy user messages (#1845 - e.g. a pre-compaction memory flush)
    /// out of <paramref name="drained"/> and into <paramref name="deferred"/>, preserving order.
    /// A defer-while-busy message must not be injected into a turn while the run still has
    /// pending tool calls, because the model typically answers such a system side turn with plain
    /// text; injecting it mid-chain would let that plain-text turn terminate the loop and abandon
    /// the original in-flight task. Non-deferred messages are left in place and drive turns as usual.
    /// </summary>
    private static void ExtractDeferWhileBusy(List<AgentMessage> drained, List<AgentMessage> deferred)
    {
        if (drained.Count == 0)
        {
            return;
        }

        for (var i = drained.Count - 1; i >= 0; i--)
        {
            if (drained[i] is BotNexus.Agent.Core.Types.UserMessage { DeferWhileBusy: true })
            {
                deferred.Insert(0, drained[i]);
                drained.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Best-effort mid-loop auto-compaction (#1710). Awaits <see cref="AgentLoopConfig.MaybeCompactAsync"/>
    /// when configured so a long dispatch re-checks the compaction threshold between outer iterations.
    /// A failure is swallowed so the loop continues; cancellation propagates.
    /// </summary>
    private static async Task MaybeCompactAsync(AgentLoopConfig config, CancellationToken cancellationToken)
    {
        if (config.MaybeCompactAsync is null)
        {
            return;
        }

        try
        {
            await config.MaybeCompactAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Compaction is best-effort: a failure must never abort the run.
        }
    }

    private static async Task<AssistantAgentMessage> ExecuteWithRetryAsync(
        List<AgentMessage> messages,
        string? systemPrompt,
        IReadOnlyList<IAgentTool> tools,
        AgentLoopConfig config,
        SimpleStreamOptions streamOptions,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        var attempt = 0;
        var backoffMs = 500;
        var overflowRecovered = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-run transforms per attempt so context overflow compaction is visible.
            var transformedMessages = config.TransformContext is null
                ? messages
                : await config.TransformContext(messages, cancellationToken).ConfigureAwait(false);
            var transformedContext = new AgentContext(systemPrompt, transformedMessages, tools);
            var providerContext = await ContextConverter.ToProviderContext(
                    transformedContext,
                    config.ConvertToLlm,
                    cancellationToken)
                .ConfigureAwait(false);

            var messageCountBeforeStream = messages.Count;
            try
            {
                var stream = config.LlmClient.StreamSimple(config.Model, providerContext, streamOptions);
                return await StreamAccumulator.AccumulateAsync(stream, emit, cancellationToken, messages).ConfigureAwait(false);
            }
            catch (Exception ex) when (ContextOverflowDetector.IsContextOverflow(ex) && !overflowRecovered)
            {
                RestoreMessagesAfterFailedStream(messages, messageCountBeforeStream);
                overflowRecovered = true;
                var compacted = CompactForOverflow(messages);
                messages.Clear();
                messages.AddRange(compacted);
                continue;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < maxAttempts - 1)
            {
                RestoreMessagesAfterFailedStream(messages, messageCountBeforeStream);
                var retryAfterDelay = (ex as ProviderRateLimitException)?.RetryAfter;
                var delayMs = retryAfterDelay is not null
                    ? (int)retryAfterDelay.Value.TotalMilliseconds
                    : config.MaxRetryDelayMs is > 0
                        ? Math.Min(backoffMs, config.MaxRetryDelayMs.Value)
                        : backoffMs;
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                attempt++;
                backoffMs *= 2;
                continue;
            }
            catch
            {
                RestoreMessagesAfterFailedStream(messages, messageCountBeforeStream);
                throw;
            }
        }
    }

    private static void RestoreMessagesAfterFailedStream(List<AgentMessage> messages, int expectedCount)
    {
        if (messages.Count <= expectedCount)
        {
            return;
        }

        messages.RemoveRange(expectedCount, messages.Count - expectedCount);
    }

    private static IReadOnlyList<AgentMessage> CompactForOverflow(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count <= 12)
        {
            return messages.ToList(); // Return a copy to avoid aliasing with the source list
        }

        var keep = Math.Max(8, messages.Count / 3);
        return messages.Skip(messages.Count - keep).ToList();
    }

    private static bool IsTransientError(Exception exception)
    {
        if (exception is ProviderRateLimitException)
            return true;

        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("504", StringComparison.OrdinalIgnoreCase);
    }
}
