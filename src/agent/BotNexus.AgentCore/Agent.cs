using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Loop;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;

namespace BotNexus.AgentCore;

public sealed class Agent
{
    private readonly AgentOptions _options;
    private readonly AgentState _state;
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

    public Agent(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

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

    public AgentState State => _state;

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

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_listenersLock)
        {
            _listeners = [.. _listeners, listener];
        }

        return new Subscription(this, listener);
    }

    public Task<IReadOnlyList<AgentMessage>> PromptAsync(string text, CancellationToken cancellationToken = default)
    {
        return PromptAsync(new UserMessage(text), cancellationToken);
    }

    public Task<IReadOnlyList<AgentMessage>> PromptAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PromptAsync([message], cancellationToken);
    }

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

    public Task<IReadOnlyList<AgentMessage>> ContinueAsync(CancellationToken cancellationToken = default)
    {
        var queued = DrainQueuedMessages();
        if (queued.Count > 0)
        {
            return PromptAsync(queued, cancellationToken);
        }

        lock (_stateLock)
        {
            if (_state.Messages.Count > 0 && _state.Messages[^1] is AssistantAgentMessage)
            {
                throw new InvalidOperationException("Cannot continue when the last message is from the assistant.");
            }
        }

        return RunAsync(
            (context, config, emit, ct) => AgentLoopRunner.ContinueAsync(context, config, emit, ct),
            cancellationToken);
    }

    public void Steer(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _steeringQueue.Enqueue(message);
    }

    public void FollowUp(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _followUpQueue.Enqueue(message);
    }

    public void ClearSteeringQueue() => _steeringQueue.Clear();

    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    public void ClearAllQueues()
    {
        _steeringQueue.Clear();
        _followUpQueue.Clear();
    }

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

    public void Reset()
    {
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            cts = _cts;
            _status = AgentStatus.Idle;
        }

        cts?.Cancel();
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
            throw;
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _state.SetErrorMessage(ex.Message);
            }

            throw;
        }
        finally
        {
            linkedCts.Dispose();

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
        lock (_stateLock)
        {
            model = _state.Model;
        }

        var generationSettings = CloneGenerationSettings(_options.GenerationSettings);
        if (!string.IsNullOrWhiteSpace(_options.SessionId))
        {
            generationSettings.SessionId = _options.SessionId;
        }

        return new AgentLoopConfig(
            model,
            _options.ConvertToLlm,
            _options.TransformContext,
            _options.GetApiKey,
            BuildQueueDelegate(_steeringQueue, _options.GetSteeringMessages),
            BuildQueueDelegate(_followUpQueue, _options.GetFollowUpMessages),
            _options.ToolExecutionMode,
            _options.BeforeToolCall,
            _options.AfterToolCall,
            generationSettings);
    }

    private static SimpleStreamOptions CloneGenerationSettings(SimpleStreamOptions source)
    {
        return new SimpleStreamOptions
        {
            Temperature = source.Temperature,
            MaxTokens = source.MaxTokens,
            CancellationToken = source.CancellationToken,
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
        var messages = _steeringQueue.Drain().ToList();
        messages.AddRange(_followUpQueue.Drain());
        return messages;
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
                    _state.SetStreamingMessage(messageStart.Message);
                    break;
                case MessageUpdateEvent messageUpdate:
                    _state.SetStreamingMessage(messageUpdate.Message);
                    break;
                case MessageEndEvent messageEnd:
                {
                    _state.SetStreamingMessage(null);
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
