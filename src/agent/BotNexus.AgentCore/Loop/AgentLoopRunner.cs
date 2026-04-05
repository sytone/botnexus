using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

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

        if (context.Messages.Count > 0 && context.Messages[^1] is AssistantAgentMessage)
        {
            throw new InvalidOperationException("Cannot continue when the last message is from the assistant.");
        }

        var newMessages = new List<AgentMessage>();
        await emit(new AgentStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await emit(new TurnStartEvent(DateTimeOffset.UtcNow)).ConfigureAwait(false);

        await RunLoopAsync(context, newMessages, config, emit, cancellationToken, firstTurn: true)
            .ConfigureAwait(false);

        return newMessages;
    }

    private static async Task RunLoopAsync(
        AgentContext currentContext,
        List<AgentMessage> newMessages,
        AgentLoopConfig config,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken,
        bool firstTurn)
    {
        var messages = currentContext.Messages.ToList();
        IReadOnlyList<AgentMessage> followUpSeed = [];

        while (true)
        {
            var pendingMessages = followUpSeed.Count > 0
                ? followUpSeed.ToList()
                : (await GetMessagesAsync(config.GetSteeringMessages, cancellationToken).ConfigureAwait(false)).ToList();
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

                var transformedMessages = await config.TransformContext(messages, cancellationToken).ConfigureAwait(false);
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
                var stream = LlmClient.StreamSimple(config.Model, providerContext, streamOptions);
                var assistantMessage = await StreamAccumulator.AccumulateAsync(stream, emit, cancellationToken)
                    .ConfigureAwait(false);

                messages.Add(assistantMessage);
                newMessages.Add(assistantMessage);

                if (assistantMessage.FinishReason is StopReason.Error or StopReason.Aborted)
                {
                    await emit(new TurnEndEvent(assistantMessage, [], DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    await emit(new AgentEndEvent(messages, DateTimeOffset.UtcNow)).ConfigureAwait(false);
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

        await emit(new AgentEndEvent(messages, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    private static async Task<SimpleStreamOptions> BuildStreamOptionsAsync(
        AgentLoopConfig config,
        CancellationToken cancellationToken)
    {
        var options = CloneOptions(config.GenerationSettings, cancellationToken);
        var apiKey = await config.GetApiKey(config.Model.Provider, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options.ApiKey = apiKey;
        }

        return options;
    }

    private static SimpleStreamOptions CloneOptions(SimpleStreamOptions source, CancellationToken cancellationToken)
    {
        return new SimpleStreamOptions
        {
            Temperature = source.Temperature,
            MaxTokens = source.MaxTokens,
            CancellationToken = cancellationToken,
            ApiKey = source.ApiKey,
            Transport = source.Transport,
            CacheRetention = source.CacheRetention,
            SessionId = source.SessionId,
            OnPayload = source.OnPayload,
            Headers = source.Headers is null ? null : new Dictionary<string, string>(source.Headers),
            MaxRetryDelayMs = source.MaxRetryDelayMs,
            Metadata = source.Metadata is null ? null : new Dictionary<string, object>(source.Metadata),
            Reasoning = source.Reasoning,
            ThinkingBudgets = source.ThinkingBudgets
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
}
