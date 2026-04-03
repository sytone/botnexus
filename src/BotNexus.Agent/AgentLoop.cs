using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Core.Observability;
using BotNexus.Providers.Base;
using BotNexus.Agent.Tools;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace BotNexus.Agent;

/// <summary>
/// The agent processing loop. Handles the full cycle of:
/// 1. Building context from session history
/// 2. Calling the LLM
/// 3. Executing tool calls
/// 4. Persisting results to session
/// 5. Sending responses back to the channel
/// </summary>
public sealed class AgentLoop
{
    private readonly string _agentName;
    private readonly string? _model;
    private readonly string? _providerName;
    private readonly ProviderRegistry _providerRegistry;
    private readonly ISessionManager _sessionManager;
    private readonly IContextBuilder _contextBuilder;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<ITool> _additionalTools;
    private readonly bool _enableMemory;
    private readonly IMemoryStore? _memoryStore;
    private readonly IReadOnlySet<string> _disallowedTools;
    private readonly IReadOnlyList<IAgentHook> _hooks;
    private readonly ILogger<AgentLoop> _logger;
    private readonly GenerationSettings _settings;
    private readonly int _maxToolIterations;
    private readonly IBotNexusMetrics? _metrics;

    public AgentLoop(
        string agentName,
        ProviderRegistry providerRegistry,
        ISessionManager sessionManager,
        IContextBuilder contextBuilder,
        ToolRegistry toolRegistry,
        GenerationSettings settings,
        string? model = null,
        string? providerName = null,
        IEnumerable<ITool>? additionalTools = null,
        bool enableMemory = false,
        IMemoryStore? memoryStore = null,
        IReadOnlySet<string>? disallowedTools = null,
        IReadOnlyList<IAgentHook>? hooks = null,
        ILogger<AgentLoop>? logger = null,
        IBotNexusMetrics? metrics = null,
        int maxToolIterations = 40)
    {
        _agentName = agentName;
        _providerRegistry = providerRegistry;
        _sessionManager = sessionManager;
        _contextBuilder = contextBuilder;
        _toolRegistry = toolRegistry;
        _settings = settings;
        _model = model;
        _providerName = providerName;
        _additionalTools = [.. (additionalTools ?? [])];
        _enableMemory = enableMemory;
        _memoryStore = memoryStore;
        _disallowedTools = disallowedTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _hooks = hooks ?? [];
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentLoop>.Instance;
        _metrics = metrics;
        _maxToolIterations = maxToolIterations;
    }

    /// <summary>Processes an inbound message through the full agent pipeline.</summary>
    public async Task<string> ProcessAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var correlationId = message.GetCorrelationId() ?? "n/a";
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["SessionKey"] = message.SessionKey,
            ["AgentName"] = _agentName
        });

        var session = await _sessionManager.GetOrCreateAsync(message.SessionKey, _agentName, cancellationToken).ConfigureAwait(false);
        var hookContext = new AgentHookContext(_agentName, message.SessionKey, message);

        foreach (var hook in _hooks)
            await hook.OnBeforeAsync(hookContext, cancellationToken).ConfigureAwait(false);

        try
        {
            // Add user message to history
            session.AddEntry(new SessionEntry(MessageRole.User, message.Content, message.Timestamp));
            var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(_agentName, cancellationToken).ConfigureAwait(false);

            RegisterTools();

            var tools = _toolRegistry.GetDefinitions();
            var response = string.Empty;

            for (int iteration = 0; iteration < _maxToolIterations; iteration++)
            {
                var history = session.History
                    .Where(entry =>
                        entry.Role != MessageRole.System &&
                        !(entry.Role == MessageRole.User &&
                          entry.Timestamp == message.Timestamp &&
                          string.Equals(entry.Content, message.Content, StringComparison.Ordinal)))
                    .Select(static entry => new ChatMessage(
                        entry.Role switch
                        {
                            MessageRole.User => "user",
                            MessageRole.Assistant => "assistant",
                            MessageRole.Tool => "tool",
                            _ => "user"
                        },
                        entry.Content,
                        ToolCallId: entry.ToolCallId,
                        ToolName: entry.ToolName,
                        ToolCalls: entry.ToolCalls))
                    .ToList();

                var messages = await _contextBuilder.BuildMessagesAsync(
                    _agentName,
                    history,
                    message.Content,
                    message.Channel,
                    message.ChatId,
                    cancellationToken).ConfigureAwait(false);

                var requestMessages = messages
                    .Where(static m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var request = new ChatRequest(requestMessages, _settings, tools, systemPrompt);
                var provider = ResolveProvider();
                _logger.LogInformation("Calling provider {ProviderName} for agent {AgentName}", provider.GetType().Name, _agentName);
                var providerTimer = Stopwatch.StartNew();
                var llmResponse = await provider.ChatAsync(request, cancellationToken).ConfigureAwait(false);
                providerTimer.Stop();
                _metrics?.RecordProviderLatency(provider.GetType().Name, providerTimer.Elapsed.TotalMilliseconds);
                _logger.LogInformation("Provider {ProviderName} responded in {ElapsedMs}ms", provider.GetType().Name, providerTimer.Elapsed.TotalMilliseconds);

                if (!string.IsNullOrEmpty(llmResponse.Content) || llmResponse.ToolCalls is { Count: > 0 })
                {
                    session.AddEntry(new SessionEntry(
                        MessageRole.Assistant,
                        llmResponse.Content ?? string.Empty,
                        DateTimeOffset.UtcNow,
                        ToolCalls: llmResponse.ToolCalls));
                    if (!string.IsNullOrEmpty(llmResponse.Content))
                        response = llmResponse.Content;
                }

                // Handle tool calls
                if (llmResponse.FinishReason != FinishReason.ToolCalls || llmResponse.ToolCalls is not { Count: > 0 })
                {
                    _logger.LogInformation("Agent {AgentName}: breaking loop at iteration {Iteration}, FinishReason={FinishReason}, ToolCalls={ToolCallCount}",
                        _agentName, iteration, llmResponse.FinishReason, llmResponse.ToolCalls?.Count ?? 0);
                    break;
                }

                _logger.LogInformation("Agent {AgentName}: executing {Count} tool calls (iteration {Iteration})",
                    _agentName, llmResponse.ToolCalls.Count, iteration);

                foreach (var toolCall in llmResponse.ToolCalls)
                {
                    var toolResult = await _toolRegistry.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
                    session.AddEntry(new SessionEntry(
                        MessageRole.Tool,
                        toolResult,
                        DateTimeOffset.UtcNow,
                        ToolName: toolCall.ToolName,
                        ToolCallId: toolCall.Id));
                }
            }

            await _sessionManager.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            var finalContext = new AgentHookContext(_agentName, message.SessionKey, message,
                new LlmResponse(response, FinishReason.Stop));
            foreach (var hook in _hooks)
                await hook.OnAfterAsync(finalContext, cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch (Exception ex)
        {
            var errorContext = new AgentHookContext(_agentName, message.SessionKey, message, Error: ex);
            foreach (var hook in _hooks)
                await hook.OnErrorAsync(errorContext, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private void RegisterTools()
    {
        if (_enableMemory)
        {
            if (_memoryStore is null)
            {
                _logger.LogWarning("Memory is enabled for agent {AgentName} but no IMemoryStore is configured", _agentName);
            }
            else
            {
                RegisterIfAllowed(new MemorySearchTool(_memoryStore, _agentName));
                RegisterIfAllowed(new MemorySaveTool(_memoryStore, _agentName));
                RegisterIfAllowed(new MemoryGetTool(_memoryStore, _agentName));
            }
        }

        if (_additionalTools.Count > 0)
            _toolRegistry.RegisterRange(_additionalTools);
    }

    private void RegisterIfAllowed(ITool tool)
    {
        if (!_disallowedTools.Contains(tool.Definition.Name))
            _toolRegistry.Register(tool);
    }

    private ILlmProvider ResolveProvider()
    {
        if (!string.IsNullOrWhiteSpace(_providerName))
        {
            var configured = _providerRegistry.Get(_providerName);
            if (configured is not null) return configured;
        }

        var configuredModel = string.IsNullOrWhiteSpace(_model) ? _settings.Model : _model;
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            var fromModelPrefix = ResolveProviderFromModelPrefix(configuredModel);
            if (fromModelPrefix is not null) return fromModelPrefix;

            foreach (var name in _providerRegistry.GetProviderNames())
            {
                var provider = _providerRegistry.Get(name);
                if (provider is not null &&
                    string.Equals(provider.DefaultModel, configuredModel, StringComparison.OrdinalIgnoreCase))
                {
                    return provider;
                }
            }
        }

        var byDefault = _providerRegistry.GetDefault();
        if (byDefault is not null) return byDefault;

        throw new InvalidOperationException(
            $"No LLM providers are registered for agent '{_agentName}'.");
    }

    private ILlmProvider? ResolveProviderFromModelPrefix(string model)
    {
        var separatorIndex = model.IndexOfAny([':', '/']);
        if (separatorIndex <= 0) return null;

        var providerName = model[..separatorIndex];
        return _providerRegistry.Get(providerName);
    }
}
