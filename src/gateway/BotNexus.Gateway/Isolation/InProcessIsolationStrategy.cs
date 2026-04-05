using System.Runtime.CompilerServices;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// In-process isolation strategy — runs agents directly in the Gateway process
/// by wrapping <see cref="BotNexus.AgentCore.Agent"/>.
/// </summary>
/// <remarks>
/// This is the default and fastest strategy. No process or container boundaries.
/// Suitable for development, testing, and trusted agent deployments.
/// </remarks>
public sealed class InProcessIsolationStrategy : IIsolationStrategy
{
    private readonly LlmClient _llmClient;
    private readonly ILogger<InProcessIsolationStrategy> _logger;

    public InProcessIsolationStrategy(LlmClient llmClient, ILogger<InProcessIsolationStrategy> logger)
    {
        _llmClient = llmClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "in-process";

    /// <inheritdoc />
    public Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        var model = _llmClient.Models.GetModel(descriptor.ApiProvider, descriptor.ModelId)
            ?? throw new InvalidOperationException($"Model '{descriptor.ModelId}' for provider '{descriptor.ApiProvider}' is not registered.");

        var options = new AgentOptions(
            InitialState: new AgentInitialState(
                SystemPrompt: descriptor.SystemPrompt,
                Model: model),
            Model: model,
            LlmClient: _llmClient,
            ConvertToLlm: null,
            TransformContext: null,
            GetApiKey: (provider, ct) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Parallel,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            SteeringMode: QueueMode.All,
            FollowUpMode: QueueMode.All,
            SessionId: context.SessionId);

        var agent = new Agent(options);
        IAgentHandle handle = new InProcessAgentHandle(agent, descriptor.AgentId, context.SessionId, _logger);

        _logger.LogDebug("Created in-process agent handle for '{AgentId}' session '{SessionId}'", descriptor.AgentId, context.SessionId);

        return Task.FromResult(handle);
    }
}

/// <summary>
/// Agent handle that wraps an in-process <see cref="BotNexus.AgentCore.Agent"/> instance.
/// </summary>
internal sealed class InProcessAgentHandle : IAgentHandle
{
    private readonly Agent _agent;
    private readonly ILogger _logger;

    public InProcessAgentHandle(Agent agent, string agentId, string sessionId, ILogger logger)
    {
        _agent = agent;
        AgentId = agentId;
        SessionId = sessionId;
        _logger = logger;
    }

    /// <inheritdoc />
    public string AgentId { get; }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public bool IsRunning => _agent.Status == AgentStatus.Running;

    /// <inheritdoc />
    public async Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
    {
        var messages = await _agent.PromptAsync(message, cancellationToken);
        var lastAssistant = messages.OfType<AssistantAgentMessage>().LastOrDefault();

        return new AgentResponse
        {
            Content = lastAssistant?.Content ?? string.Empty,
            Usage = lastAssistant?.Usage is { } u ? new AgentResponseUsage(u.InputTokens, u.OutputTokens) : null,
            ToolCalls = messages.OfType<ToolResultAgentMessage>()
                .Select(t => new AgentToolCallInfo(t.ToolCallId, t.ToolName, t.IsError))
                .ToList()
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IReadOnlyList<AgentMessage>>();
        var events = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();

        using var subscription = _agent.Subscribe(async (agentEvent, ct) =>
        {
            var streamEvent = agentEvent switch
            {
                MessageStartEvent => new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = messageId },
                MessageUpdateEvent update when update.ContentDelta is not null => new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ContentDelta,
                    ContentDelta = update.ContentDelta,
                    MessageId = messageId
                },
                ToolExecutionStartEvent toolStart => new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = toolStart.ToolCallId,
                    ToolName = toolStart.ToolName,
                    MessageId = messageId
                },
                ToolExecutionEndEvent toolEnd => new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = toolEnd.ToolCallId,
                    ToolName = toolEnd.ToolName,
                    ToolResult = toolEnd.Result.Content.FirstOrDefault()?.ToString(),
                    ToolIsError = toolEnd.IsError,
                    MessageId = messageId
                },
                MessageEndEvent => new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = messageId },
                _ => null
            };

            if (streamEvent is not null)
                await events.Writer.WriteAsync(streamEvent, ct);
        });

        // Fire prompt on background task
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _agent.PromptAsync(message, cancellationToken);
                tcs.TrySetResult(result);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { events.Writer.TryComplete(); }
        }, cancellationToken);

        await foreach (var evt in events.Reader.ReadAllAsync(cancellationToken))
            yield return evt;
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await _agent.AbortAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _agent.AbortAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error aborting agent during dispose"); }
    }
}
