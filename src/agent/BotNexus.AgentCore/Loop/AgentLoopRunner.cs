using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.AgentCore.Loop;

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
        await emit(new AgentStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await emit(new TurnStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);

        await RunLoopAsync(context, newMessages, config, emit, cancellationToken, runStartIndex, firstTurn: true)
            .ConfigureAwait(false);

        return newMessages;
    }

    private static async Task RunLoopAsync(
        AgentContext currentContext,
        List<AgentMessage> newMessages,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken,
        int runStartIndex,
        bool firstTurn)
    {
        var messages = currentContext.Messages.ToList();
        IReadOnlyList<AgentMessage> followUpSeed = [];

        while (true)
        {
            var pendingMessages = followUpSeed.Count > 0
                ? followUpSeed.ToList()
                : (config.SkipInitialSteeringPoll
                    ? []
                    : (await GetMessagesAsync(config.GetSteeringMessages, cancellationToken).ConfigureAwait(false)).ToList());
            config = config with { SkipInitialSteeringPoll = false };
            followUpSeed = [];

            var hasMoreToolCalls = true;

            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

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

                var transformedMessages = config.TransformContext is null
                    ? messages
                    : await config.TransformContext(messages, cancellationToken).ConfigureAwait(false);
                var transformedContext = new AgentContext(
                    currentContext.SystemPrompt,
                    transformedMessages,
                    currentContext.Tools);

                var providerContext = await ContextConverter.ToProviderContext(
                        transformedContext,
                        config.ConvertToLlm,
                        cancellationToken)
                    .ConfigureAwait(false);

                var streamOptions = await BuildStreamOptionsAsync(config, cancellationToken).ConfigureAwait(false);
                var assistantMessage = await ExecuteWithRetryAsync(
                        messages,
                        config,
                        providerContext,
                        streamOptions,
                        emit,
                        cancellationToken)
                    .ConfigureAwait(false);

                messages.Add(assistantMessage);
                newMessages.Add(assistantMessage);

                if (assistantMessage.FinishReason is StopReason.Error or StopReason.Aborted)
                {
                    await emit(new TurnEndEvent(assistantMessage, [], DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    await emit(new AgentEndEvent(messages.Skip(runStartIndex).ToList(), DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    return;
                }

                hasMoreToolCalls = assistantMessage.ToolCalls is { Count: > 0 };
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

                foreach (var toolResult in toolResults)
                {
                    messages.Add(toolResult);
                    newMessages.Add(toolResult);
                }

                await emit(new TurnEndEvent(assistantMessage, toolResults, DateTimeOffset.UtcNow))
                    .ConfigureAwait(false);

                pendingMessages = (await GetMessagesAsync(config.GetSteeringMessages, cancellationToken).ConfigureAwait(false))
                    .ToList();
            }

            var followUps = await GetMessagesAsync(config.GetFollowUpMessages, cancellationToken).ConfigureAwait(false);
            if (followUps.Count > 0)
            {
                followUpSeed = followUps;
                continue;
            }

            break;
        }

        await emit(new AgentEndEvent(messages.Skip(runStartIndex).ToList(), DateTimeOffset.UtcNow)).ConfigureAwait(false);
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

    private static async Task<AssistantAgentMessage> ExecuteWithRetryAsync(
        List<AgentMessage> messages,
        AgentLoopConfig config,
        Context providerContext,
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
            try
            {
                var stream = config.LlmClient.StreamSimple(config.Model, providerContext, streamOptions);
                return await StreamAccumulator.AccumulateAsync(stream, emit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ContextOverflowDetector.IsContextOverflow(ex) && !overflowRecovered)
            {
                overflowRecovered = true;
                var compacted = CompactForOverflow(messages);
                messages.Clear();
                messages.AddRange(compacted);
                var compactedProviderMessages = providerContext.Messages.Count <= 12
                    ? providerContext.Messages
                    : providerContext.Messages.Skip(providerContext.Messages.Count - Math.Max(8, providerContext.Messages.Count / 3)).ToList();
                providerContext = providerContext with { Messages = compactedProviderMessages };
                continue;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < maxAttempts - 1)
            {
                var delayMs = config.MaxRetryDelayMs is > 0
                    ? Math.Min(backoffMs, config.MaxRetryDelayMs.Value)
                    : backoffMs;
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                attempt++;
                backoffMs *= 2;
                continue;
            }
        }
    }

    private static IReadOnlyList<AgentMessage> CompactForOverflow(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count <= 12)
        {
            return messages;
        }

        var keep = Math.Max(8, messages.Count / 3);
        return messages.Skip(messages.Count - keep).ToList();
    }

    private static bool IsTransientError(Exception exception)
    {
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
