using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Loop;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;

namespace BotNexus.AgentCore;

/// <summary>
/// Stateful wrapper around the low-level agent loop.
/// </summary>
/// <remarks>
/// Agent owns the current transcript, emits lifecycle events, executes tools,
/// and exposes queueing APIs for steering and follow-up messages. It enforces
/// single-run concurrency — only one PromptAsync/ContinueAsync may be active at a time.
/// Listeners are awaited in subscription order and are included in the current run's settlement.
/// </remarks>
public sealed class Agent
{
    private static readonly TransformContextDelegate IdentityTransformContext =
        (messages, _) => Task.FromResult(messages);

    private readonly AgentOptions _options;
    private readonly ConvertToLlmDelegate _convertToLlm;
    private readonly AgentState _state;
    private readonly TransformContextDelegate _transformContext;
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly object _stateLock = new();
    private readonly object _listenersLock = new();

    private List<Func<AgentEvent, CancellationToken, Task>> _listeners = [];
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _activeRun;
    private AgentStatus _status = AgentStatus.Idle;
    private bool _skipInitialSteeringPollForNextRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="Agent"/> class.
    /// </summary>
    /// <param name="options">The agent configuration options.</param>
    /// <remarks>
    /// Creates internal message queues, copies initial state, and prepares the agent for runs.
    /// The agent starts in Idle status.
    /// </remarks>
    public Agent(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _convertToLlm = options.ConvertToLlm ?? DefaultMessageConverter.ConvertToLlm;
        _transformContext = options.TransformContext ?? IdentityTransformContext;

        var initial = options.InitialState;
        _state = new AgentState
        {
            SystemPrompt = initial?.SystemPrompt,
            Model = initial?.Model ?? options.Model,
            ThinkingLevel = initial?.ThinkingLevel
        };
        _state.Tools = initial?.Tools ?? [];
        _state.Messages = initial?.Messages ?? [];

        _steeringQueue = new PendingMessageQueue(options.SteeringMode);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode);
    }

    /// <summary>
    /// Gets the current agent state.
    /// </summary>
    /// <remarks>
    /// Assigning State.Tools or State.Messages copies the provided top-level array.
    /// State mutations are visible immediately but do not affect in-flight runs.
    /// </remarks>
    public AgentState State => _state;

    /// <summary>
    /// Gets the current agent execution status.
    /// </summary>
    /// <remarks>
    /// Thread-safe. Returns Idle, Running, or Aborting.
    /// </remarks>
    public AgentStatus Status
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether steering or follow-up messages are queued.
    /// </summary>
    public bool HasQueuedMessages => _steeringQueue.HasItems || _followUpQueue.HasItems;

    /// <summary>
    /// Gets or sets the queue consumption mode for steering messages.
    /// </summary>
    public QueueMode SteeringMode
    {
        get => _steeringQueue.Mode;
        set => _steeringQueue.Mode = value;
    }

    /// <summary>
    /// Gets or sets the queue consumption mode for follow-up messages.
    /// </summary>
    public QueueMode FollowUpMode
    {
        get => _followUpQueue.Mode;
        set => _followUpQueue.Mode = value;
    }

    /// <summary>
    /// Subscribe to agent lifecycle events.
    /// </summary>
    /// <param name="listener">The event listener callback.</param>
    /// <returns>A disposable subscription handle.</returns>
    /// <remarks>
    /// <para>
    /// Listener promises are awaited in subscription order and are included in
    /// the current run's settlement. Listeners also receive the active abort
    /// signal for the current run.
    /// </para>
    /// <para>
    /// agent_end is the final emitted event for a run, but the agent does not
    /// become idle until all awaited listeners for that event have settled.
    /// </para>
    /// <para>
    /// Thread-safe. Can be called while a run is active — new listeners will receive
    /// events from the next turn boundary.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using var subscription = agent.Subscribe(async (evt, ct) =>
    /// {
    ///     if (evt is MessageUpdateEvent update)
    ///     {
    ///         Console.Write(update.ContentDelta);
    ///     }
    /// });
    /// </code>
    /// </example>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_listenersLock)
        {
            _listeners = [.. _listeners, listener];
        }

        return new Subscription(this, listener);
    }

    /// <summary>
    /// Start a new agent run with a user text prompt.
    /// </summary>
    /// <param name="text">The user message text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All messages produced during the run (prompts, assistant messages, tool results).</returns>
    /// <remarks>
    /// Convenience overload that wraps the text in a UserMessage.
    /// Blocks until the agent run completes or is aborted.
    /// </remarks>
    public Task<IReadOnlyList<AgentMessage>> PromptAsync(string text, CancellationToken cancellationToken = default)
    {
        return PromptAsync(new UserMessage(text), cancellationToken);
    }

    /// <summary>
    /// Start a new agent run with a single message.
    /// </summary>
    /// <param name="message">The message to append to the timeline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All messages produced during the run.</returns>
    /// <remarks>
    /// Convenience overload that wraps the message in a list.
    /// </remarks>
    public Task<IReadOnlyList<AgentMessage>> PromptAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PromptAsync([message], cancellationToken);
    }

    /// <summary>
    /// Start a new agent run with one or more messages.
    /// </summary>
    /// <param name="messages">The messages to append to the timeline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All messages produced during the run.</returns>
    /// <remarks>
    /// <para>
    /// Throws InvalidOperationException if the agent is already running.
    /// Drains queued steering messages before starting the loop.
    /// </para>
    /// <para>
    /// All lifecycle events (agent_start, turn_start, message_start/update/end, tool_execution_start/end,
    /// turn_end, agent_end) are emitted and awaited in order.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await agent.PromptAsync("What is the weather?");
    /// var lastMessage = result[^1] as AssistantAgentMessage;
    /// </code>
    /// </example>
    public Task<IReadOnlyList<AgentMessage>> PromptAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
        }

        return RunAsync(
            (context, config, emit, ct) => AgentLoopRunner.RunAsync(messages, context, config, emit, ct),
            cancellationToken);
    }

    /// <summary>
    /// Continue an agent loop from the current context without adding a new message.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All messages produced during the run.</returns>
    /// <remarks>
    /// <para>
    /// Used for retries when the context already has a user message or tool results.
    /// If queued messages exist, drains them and calls PromptAsync instead.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> The last message in context must convert to a user or tool result message
    /// via ConvertToLlm. If it doesn't, the LLM provider will reject the request.
    /// </para>
    /// <para>
    /// Throws InvalidOperationException if the last message is from the assistant.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<AgentMessage>> ContinueAsync(CancellationToken cancellationToken = default)
    {
        bool shouldDrainQueues;

        lock (_stateLock)
        {
            shouldDrainQueues = _state.Messages.Count > 0 && _state.Messages[^1] is AssistantAgentMessage;
        }

        if (shouldDrainQueues)
        {
            var queued = DrainQueuedMessages();
            if (queued.Count > 0)
            {
                _skipInitialSteeringPollForNextRun = true;
                return PromptAsync(queued, cancellationToken);
            }

            throw new InvalidOperationException("Cannot continue when the last message is from the assistant.");
        }

        return RunAsync(
            (context, config, emit, ct) => AgentLoopRunner.ContinueAsync(context, config, emit, ct),
            cancellationToken);
    }

    /// <summary>
    /// Enqueue a steering message to be injected at the next turn boundary.
    /// </summary>
    /// <param name="message">The message to enqueue.</param>
    /// <remarks>
    /// Steering messages are drained before each LLM call. Use for user interruptions,
    /// context updates, or mid-run corrections. Thread-safe.
    /// </remarks>
    public void Steer(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _steeringQueue.Enqueue(message);
    }

    /// <summary>
    /// Enqueue a follow-up message to be injected after the current run completes.
    /// </summary>
    /// <param name="message">The message to enqueue.</param>
    /// <remarks>
    /// Follow-up messages are drained after the agent finishes a run with no pending tool calls.
    /// Use for chained workflows or continuation prompts. Thread-safe.
    /// </remarks>
    public void FollowUp(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _followUpQueue.Enqueue(message);
    }

    /// <summary>
    /// Clears all enqueued steering messages.
    /// </summary>
    public void ClearSteeringQueue() => _steeringQueue.Clear();

    /// <summary>
    /// Clears all enqueued follow-up messages.
    /// </summary>
    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    /// <summary>
    /// Clears both steering and follow-up message queues.
    /// </summary>
    public void ClearAllQueues()
    {
        _steeringQueue.Clear();
        _followUpQueue.Clear();
    }

    /// <summary>
    /// Abort the currently running agent loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sets status to Aborting, cancels the internal CancellationTokenSource, and waits
    /// for the active run to settle. Swallows OperationCanceledException.
    /// </para>
    /// <para>
    /// Thread-safe. No-op if the agent is already idle.
    /// </para>
    /// </remarks>
    public async Task AbortAsync()
    {
        Task? activeRunTask;
        CancellationTokenSource? cts;

        lock (_lifecycleLock)
        {
            if (_status == AgentStatus.Idle)
            {
                return;
            }

            _status = AgentStatus.Aborting;
            activeRunTask = _activeRun?.Task;
            cts = _cts;
        }

        cts?.Cancel();
        if (activeRunTask is not null)
        {
            try
            {
                await activeRunTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Wait for the agent to become idle.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// Returns immediately if already idle. Otherwise, waits for the active run to settle.
    /// Thread-safe.
    /// </remarks>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task? activeRunTask;
        lock (_lifecycleLock)
        {
            activeRunTask = _activeRun?.Task;
            if (_status == AgentStatus.Idle || activeRunTask is null)
            {
                return;
            }
        }

        await activeRunTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reset the agent to a clean initial state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancels any active run, clears all message queues, clears the message timeline,
    /// and resets error/streaming/pending state. Sets status to Idle.
    /// </para>
    /// <para>
    /// Use with caution — this discards the conversation history.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        ClearAllQueues();

        lock (_stateLock)
        {
            _state.Messages = [];
            _state.SetErrorMessage(null);
            _state.SetStreamingMessage(null);
            _state.SetPendingToolCalls([]);
        }
    }

    private async Task<IReadOnlyList<AgentMessage>> RunAsync(
        Func<AgentContext, AgentLoopConfig, Func<AgentEvent, Task>, CancellationToken, Task<IReadOnlyList<AgentMessage>>> runner,
        CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        CancellationTokenSource linkedCts;
        TaskCompletionSource activeRun;
        try
        {
            lock (_lifecycleLock)
            {
                if (_status != AgentStatus.Idle)
                {
                    throw new InvalidOperationException("Agent is already running.");
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                activeRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _cts = linkedCts;
                _activeRun = activeRun;
                _status = AgentStatus.Running;
            }
        }
        finally
        {
            _runLock.Release();
        }

        lock (_stateLock)
        {
            _state.SetErrorMessage(null);
        }

        try
        {
            return await runner(
                    BuildContextSnapshot(),
                    BuildLoopConfig(),
                    @event => HandleEventAsync(@event, linkedCts.Token),
                    linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            var abortedMessage = new AssistantAgentMessage(
                Content: string.Empty,
                FinishReason: BotNexus.Providers.Core.Models.StopReason.Aborted,
                ErrorMessage: "Operation aborted",
                Timestamp: DateTimeOffset.UtcNow);

            lock (_stateLock)
            {
                _state.SetErrorMessage(abortedMessage.ErrorMessage);
                var messages = _state.Messages.ToList();
                messages.Add(abortedMessage);
                _state.Messages = messages;
            }

            try
            {
                await HandleEventAsync(
                        new AgentEndEvent([abortedMessage], DateTimeOffset.UtcNow),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception listenerEx)
            {
                _options.OnDiagnostic?.Invoke($"Listener error during agent_end: {listenerEx.Message}");
            }

            return [abortedMessage];
        }
        catch (Exception ex)
        {
            var failureMessage = new AssistantAgentMessage(
                Content: string.Empty,
                FinishReason: BotNexus.Providers.Core.Models.StopReason.Error,
                ErrorMessage: ex.Message,
                Timestamp: DateTimeOffset.UtcNow);

            lock (_stateLock)
            {
                _state.SetErrorMessage(ex.Message);
                var messages = _state.Messages.ToList();
                messages.Add(failureMessage);
                _state.Messages = messages;
            }

            try
            {
                await HandleEventAsync(
                        new AgentEndEvent([failureMessage], DateTimeOffset.UtcNow),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception listenerEx)
            {
                _options.OnDiagnostic?.Invoke($"Listener error during agent_end: {listenerEx.Message}");
            }

            return [failureMessage];
        }
        finally
        {
            linkedCts.Dispose();
            lock (_stateLock)
            {
                _state.SetIsRunning(false);
            }

            lock (_lifecycleLock)
            {
                _cts = null;
                _activeRun = null;
                _status = AgentStatus.Idle;
            }

            activeRun.TrySetResult();
        }
    }

    private AgentContext BuildContextSnapshot()
    {
        lock (_stateLock)
        {
            return new AgentContext(
                _state.SystemPrompt,
                _state.Messages.ToList(),
                _state.Tools.ToList());
        }
    }

    private AgentLoopConfig BuildLoopConfig()
    {
        BotNexus.Providers.Core.Models.LlmModel model;
        BotNexus.Providers.Core.Models.ThinkingLevel? thinkingLevel;
        var skipInitialSteeringPoll = _skipInitialSteeringPollForNextRun;
        _skipInitialSteeringPollForNextRun = false;
        lock (_stateLock)
        {
            model = _state.Model;
            thinkingLevel = _state.ThinkingLevel;
        }

        var generationSettings = CloneGenerationSettings(_options.GenerationSettings);
        generationSettings = generationSettings with { Reasoning = thinkingLevel };
        if (!string.IsNullOrWhiteSpace(_options.SessionId))
        {
            generationSettings = generationSettings with { SessionId = _options.SessionId };
        }

        return new AgentLoopConfig(
            model,
            _options.LlmClient,
            _convertToLlm,
            _transformContext,
            _options.GetApiKey,
            BuildQueueDelegate(_steeringQueue, _options.GetSteeringMessages),
            BuildQueueDelegate(_followUpQueue, _options.GetFollowUpMessages),
            _options.ToolExecutionMode,
            _options.BeforeToolCall,
            _options.AfterToolCall,
            generationSettings,
            skipInitialSteeringPoll);
    }

    private static SimpleStreamOptions CloneGenerationSettings(SimpleStreamOptions source)
    {
        return source with
        {
            Headers = source.Headers is null ? null : new Dictionary<string, string>(source.Headers),
            Metadata = source.Metadata is null ? null : new Dictionary<string, object>(source.Metadata),
        };
    }

    private static GetMessagesDelegate BuildQueueDelegate(
        PendingMessageQueue queue,
        GetMessagesDelegate? extra)
    {
        return async cancellationToken =>
        {
            var messages = queue.Drain().ToList();
            if (extra is not null)
            {
                var extraMessages = await extra(cancellationToken).ConfigureAwait(false);
                if (extraMessages.Count > 0)
                {
                    messages.AddRange(extraMessages);
                }
            }

            return messages;
        };
    }

    private IReadOnlyList<AgentMessage> DrainQueuedMessages()
    {
        var steering = _steeringQueue.Drain().ToList();
        if (steering.Count > 0)
        {
            return steering;
        }

        return _followUpQueue.Drain().ToList();
    }

    private async Task HandleEventAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        ProcessEvent(@event);

        List<Func<AgentEvent, CancellationToken, Task>> listenersSnapshot;
        lock (_listenersLock)
        {
            listenersSnapshot = [.. _listeners];
        }

        foreach (var listener in listenersSnapshot)
        {
            await listener(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ProcessEvent(AgentEvent @event)
    {
        lock (_stateLock)
        {
            switch (@event)
            {
                case MessageStartEvent messageStart:
                    if (messageStart.Message is AssistantAgentMessage startingAssistant)
                    {
                        _state.SetStreamingMessage(startingAssistant);
                    }
                    break;
                case MessageUpdateEvent messageUpdate:
                    _state.SetStreamingMessage(messageUpdate.Message);
                    if (_state.Messages.Count > 0 && _state.Messages[^1] is AssistantAgentMessage)
                    {
                        var updatedMessages = _state.Messages.ToList();
                        updatedMessages[^1] = messageUpdate.Message;
                        _state.Messages = updatedMessages;
                    }
                    break;
                case MessageEndEvent messageEnd:
                {
                    if (messageEnd.Message is AssistantAgentMessage)
                    {
                        _state.SetStreamingMessage(null);
                        var finalizedMessages = _state.Messages.ToList();
                        finalizedMessages.Add(messageEnd.Message);
                        _state.Messages = finalizedMessages;
                        break;
                    }
                    var messages = _state.Messages.ToList();
                    messages.Add(messageEnd.Message);
                    _state.Messages = messages;
                    break;
                }
                case ToolExecutionStartEvent toolStart:
                {
                    var pending = _state.PendingToolCalls.ToHashSet(StringComparer.Ordinal);
                    pending.Add(toolStart.ToolCallId);
                    _state.SetPendingToolCalls(pending);
                    break;
                }
                case ToolExecutionEndEvent toolEnd:
                {
                    var pending = _state.PendingToolCalls.ToHashSet(StringComparer.Ordinal);
                    pending.Remove(toolEnd.ToolCallId);
                    _state.SetPendingToolCalls(pending);
                    break;
                }
                case TurnEndEvent turnEnd when !string.IsNullOrWhiteSpace(turnEnd.Message.ErrorMessage):
                    _state.SetErrorMessage(turnEnd.Message.ErrorMessage);
                    break;
                case AgentEndEvent:
                    _state.SetStreamingMessage(null);
                    _state.SetIsRunning(false);
                    break;
                case AgentStartEvent:
                    _state.SetIsRunning(true);
                    break;
            }
        }
    }

    private void Unsubscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        lock (_listenersLock)
        {
            var copy = _listeners.ToList();
            copy.Remove(listener);
            _listeners = copy;
        }
    }

    private sealed class Subscription(Agent owner, Func<AgentEvent, CancellationToken, Task> listener) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            owner.Unsubscribe(listener);
        }
    }
}
