using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using BotNexus.Agent.Tools;
using Microsoft.Extensions.Logging;

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
    private readonly string? _systemPrompt;
    private readonly string? _model;
    private readonly string? _providerName;
    private readonly ProviderRegistry _providerRegistry;
    private readonly ISessionManager _sessionManager;
    private readonly ContextBuilder _contextBuilder;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<ITool> _additionalTools;
    private readonly IReadOnlyList<IAgentHook> _hooks;
    private readonly ILogger<AgentLoop> _logger;
    private readonly GenerationSettings _settings;
    private readonly int _maxToolIterations;

    public AgentLoop(
        string agentName,
        string? systemPrompt,
        ProviderRegistry providerRegistry,
        ISessionManager sessionManager,
        ContextBuilder contextBuilder,
        ToolRegistry toolRegistry,
        GenerationSettings settings,
        string? model = null,
        string? providerName = null,
        IEnumerable<ITool>? additionalTools = null,
        IReadOnlyList<IAgentHook>? hooks = null,
        ILogger<AgentLoop>? logger = null,
        int maxToolIterations = 40)
    {
        _agentName = agentName;
        _systemPrompt = systemPrompt;
        _providerRegistry = providerRegistry;
        _sessionManager = sessionManager;
        _contextBuilder = contextBuilder;
        _toolRegistry = toolRegistry;
        _settings = settings;
        _model = model;
        _providerName = providerName;
        _additionalTools = [.. (additionalTools ?? [])];
        _hooks = hooks ?? [];
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentLoop>.Instance;
        _maxToolIterations = maxToolIterations;
    }

    /// <summary>Processes an inbound message through the full agent pipeline.</summary>
    public async Task<string> ProcessAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var session = await _sessionManager.GetOrCreateAsync(message.SessionKey, _agentName, cancellationToken).ConfigureAwait(false);
        var hookContext = new AgentHookContext(_agentName, message.SessionKey, message);

        foreach (var hook in _hooks)
            await hook.OnBeforeAsync(hookContext, cancellationToken).ConfigureAwait(false);

        try
        {
            // Add user message to history
            session.AddEntry(new SessionEntry(MessageRole.User, message.Content, message.Timestamp));

            if (_additionalTools.Count > 0)
                _toolRegistry.RegisterRange(_additionalTools);

            var tools = _toolRegistry.GetDefinitions();
            var response = string.Empty;

            for (int iteration = 0; iteration < _maxToolIterations; iteration++)
            {
                var messages = _contextBuilder.Build(session, message, _settings);

                // Build fresh request from current session state
                var userMessages = session.History
                    .Where(e => e.Role != MessageRole.System)
                    .Select(e => new ChatMessage(
                        e.Role == MessageRole.Assistant ? "assistant" : "user",
                        e.Content))
                    .ToList();

                var request = new ChatRequest(userMessages, _settings, tools, _systemPrompt);
                var provider = ResolveProvider();
                var llmResponse = await provider.ChatAsync(request, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(llmResponse.Content))
                {
                    session.AddEntry(new SessionEntry(MessageRole.Assistant, llmResponse.Content, DateTimeOffset.UtcNow));
                    response = llmResponse.Content;
                }

                // Handle tool calls
                if (llmResponse.FinishReason != FinishReason.ToolCalls || llmResponse.ToolCalls is not { Count: > 0 })
                    break;

                _logger.LogDebug("Agent {AgentName}: executing {Count} tool calls (iteration {Iteration})",
                    _agentName, llmResponse.ToolCalls.Count, iteration + 1);

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
