using System.Diagnostics;
using System.Runtime.CompilerServices;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Diagnostics;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using Microsoft.Extensions.Logging;
using AgentCoreUserMessage = BotNexus.AgentCore.Types.UserMessage;

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
    private readonly GatewayAuthManager _authManager;
    private readonly IContextBuilder _contextBuilder;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<InProcessIsolationStrategy> _logger;

    public InProcessIsolationStrategy(
        LlmClient llmClient,
        GatewayAuthManager authManager,
        IContextBuilder contextBuilder,
        IToolRegistry toolRegistry,
        ILogger<InProcessIsolationStrategy> logger)
    {
        _llmClient = llmClient;
        _authManager = authManager;
        _contextBuilder = contextBuilder;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "in-process";

    /// <inheritdoc />
    public async Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        var model = _llmClient.Models.GetModel(descriptor.ApiProvider, descriptor.ModelId)
            ?? throw new InvalidOperationException($"Model '{descriptor.ModelId}' for provider '{descriptor.ApiProvider}' is not registered.");

        // Override model BaseUrl from auth endpoint or provider config (e.g., enterprise Copilot)
        var apiEndpoint = _authManager.GetApiEndpoint(descriptor.ApiProvider);
        if (!string.IsNullOrWhiteSpace(apiEndpoint))
            model = model with { BaseUrl = apiEndpoint };

        var enrichedSystemPrompt = await _contextBuilder.BuildSystemPromptAsync(descriptor, cancellationToken);

        // Resolve tools for this agent
        var tools = descriptor.ToolIds.Count > 0
            ? _toolRegistry.ResolveTools(descriptor.ToolIds)
            : _toolRegistry.GetAll();

        var options = new AgentOptions(
            InitialState: new AgentInitialState(
                SystemPrompt: enrichedSystemPrompt,
                Model: model,
                Tools: tools),
            Model: model,
            LlmClient: _llmClient,
            ConvertToLlm: null,
            TransformContext: null,
            GetApiKey: (provider, cancellationToken) => _authManager.GetApiKeyAsync(provider, cancellationToken),
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

        return handle;
    }
}

/// <summary>
/// Agent handle that wraps an in-process <see cref="BotNexus.AgentCore.Agent"/> instance.
/// </summary>
internal sealed class InProcessAgentHandle : IAgentHandle, IHealthCheckable
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
        using var activity = AgentDiagnostics.Source.StartActivity("agent.prompt", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", AgentId);
        activity?.SetTag("botnexus.session.id", SessionId);
        try
        {
            var messages = await _agent.PromptAsync(message, cancellationToken);
            var lastAssistant = messages.OfType<AssistantAgentMessage>().LastOrDefault();

            var response = new AgentResponse
            {
                Content = lastAssistant?.Content ?? string.Empty,
                Usage = lastAssistant?.Usage is { } u ? new AgentResponseUsage(u.InputTokens, u.OutputTokens) : null,
                ToolCalls = messages.OfType<ToolResultAgentMessage>()
                    .Select(t => new AgentToolCallInfo(t.ToolCallId, t.ToolName, t.IsError))
                    .ToList()
            };

            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = AgentDiagnostics.Source.StartActivity("agent.stream", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", AgentId);
        activity?.SetTag("botnexus.session.id", SessionId);

        var messageId = Guid.NewGuid().ToString("N");
        var events = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var subscription = _agent.Subscribe(async (agentEvent, cancellationToken) =>
        {
            try
            {
                var streamEvent = agentEvent switch
                {
                    MessageStartEvent => new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = messageId },
                    MessageUpdateEvent update when update.ContentDelta is not null => update.IsThinking
                        ? new AgentStreamEvent
                        {
                            Type = AgentStreamEventType.ThinkingDelta,
                            ThinkingContent = update.ContentDelta,
                            MessageId = messageId
                        }
                        : new AgentStreamEvent
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
                    await events.Writer.WriteAsync(streamEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing agent event in stream for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
                try
                {
                    await events.Writer.WriteAsync(new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.Error,
                        ErrorMessage = $"Internal streaming error: {ex.Message}",
                        MessageId = messageId
                    }, cancellationToken);
                }
                catch
                {
                    // Best-effort error notification.
                }

                events.Writer.TryComplete(ex);
            }
        });

        async Task RunPromptAsync()
        {
            try
            {
                await _agent.PromptAsync(message, promptCancellation.Token);
            }
            catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Agent prompt cancelled for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Agent prompt failed for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
                try
                {
                    await events.Writer.WriteAsync(new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.Error,
                        ErrorMessage = $"Agent prompt failed: {ex.Message}",
                        MessageId = messageId
                    }, CancellationToken.None);
                }
                catch
                {
                    // Best-effort error notification.
                }

                events.Writer.TryComplete(ex);
                return;
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
            events.Writer.TryComplete();
        }

        var promptTask = RunPromptAsync();

        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync(cancellationToken))
                yield return evt;
        }
        finally
        {
            promptCancellation.Cancel();

            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _agent.AbortAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error aborting agent after stream cancellation for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
                }
            }

            try
            {
                await promptTask;
            }
            catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                // Expected when caller cancels stream.
            }
        }
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await _agent.AbortAsync();
    }

    /// <inheritdoc />
    public Task SteerAsync(string message, CancellationToken cancellationToken = default)
    {
        _agent.Steer(new AgentCoreUserMessage(message));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
    {
        _agent.FollowUp(new AgentCoreUserMessage(message));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_agent.Status != AgentStatus.Aborting);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _agent.AbortAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error aborting agent during dispose"); }
    }
}
