using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Core.Observability;
using BotNexus.Providers.Base;
using BotNexus.Agent.Tools;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

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
    private readonly IActivityStream? _activityStream;

    // Track tool call signatures per session to detect loops
    private readonly Dictionary<string, int> _toolCallSignatures = new(StringComparer.Ordinal);

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
        int maxToolIterations = 40,
        IActivityStream? activityStream = null)
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
        _activityStream = activityStream;
    }

    /// <summary>Processes an inbound message through the full agent pipeline.</summary>
    /// <param name="message">The inbound message to process.</param>
    /// <param name="onDelta">Optional callback for streaming deltas during LLM response generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> ProcessAsync(
        InboundMessage message, 
        Func<string, Task>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        var correlationId = message.GetCorrelationId() ?? "n/a";
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["SessionKey"] = message.SessionKey,
            ["AgentName"] = _agentName
        });

        var session = await _sessionManager.GetOrCreateAsync(message.SessionKey, _agentName, cancellationToken).ConfigureAwait(false);
        
        // Check for model override in message metadata
        var effectiveModel = _model;
        if (message.Metadata.TryGetValue("model", out var modelOverride) && modelOverride is string modelStr && !string.IsNullOrWhiteSpace(modelStr))
        {
            effectiveModel = modelStr;
            _logger.LogInformation("Model override from message metadata: {Model}", effectiveModel);
        }
        
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

                // Clone settings to apply effective model override without mutating original
                var effectiveSettings = new GenerationSettings
                {
                    Model = effectiveModel ?? _settings.Model,
                    MaxTokens = _settings.MaxTokens,
                    Temperature = _settings.Temperature,
                    ContextWindowTokens = _settings.ContextWindowTokens,
                    MaxToolIterations = _settings.MaxToolIterations,
                    MaxRepeatedToolCalls = _settings.MaxRepeatedToolCalls
                };

                var request = new ChatRequest(requestMessages, effectiveSettings, tools, systemPrompt);
                var provider = ResolveProvider(effectiveModel);
                var actualModel = string.IsNullOrWhiteSpace(request.Settings.Model) ? provider.DefaultModel : request.Settings.Model;
                
                // Set the session model if not already set (use the resolved actual model)
                if (string.IsNullOrWhiteSpace(session.Model))
                {
                    session.Model = actualModel;
                }
                
                _logger.LogInformation("Agent {AgentName} configured with model={ConfiguredModel}, resolved to provider={ProviderName}, sending model={ActualModel}, contextWindowTokens={ContextWindowTokens}", 
                    _agentName, _model ?? _settings.Model, provider.GetType().Name, actualModel, request.Settings.ContextWindowTokens);
                
                var providerTimer = Stopwatch.StartNew();
                
                // Use streaming if callback is provided - streaming now supports tool calls!
                LlmResponse llmResponse;
                var useStreaming = onDelta is not null;
                
                if (useStreaming)
                {
                    // Streaming mode: aggregate chunks into full response while streaming deltas to client
                    var contentBuilder = new System.Text.StringBuilder();
                    var toolCallBuffers = new Dictionary<string, ToolCallStreamBuffer>();
                    FinishReason finishReason = FinishReason.Stop;
                    int? inputTokens = null;
                    int? outputTokens = null;
                    
                    await foreach (var chunk in provider.ChatStreamAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        // Content delta - stream to client
                        if (!string.IsNullOrWhiteSpace(chunk.ContentDelta))
                        {
                            contentBuilder.Append(chunk.ContentDelta);
                            await onDelta!(chunk.ContentDelta).ConfigureAwait(false);
                        }
                        
                        // Tool call start
                        if (!string.IsNullOrEmpty(chunk.ToolCallId) && !string.IsNullOrEmpty(chunk.ToolName))
                        {
                            toolCallBuffers[chunk.ToolCallId] = new ToolCallStreamBuffer(chunk.ToolCallId, chunk.ToolName);
                        }
                        
                        // Tool call arguments delta
                        if (!string.IsNullOrEmpty(chunk.ToolCallId) && !string.IsNullOrEmpty(chunk.ArgumentsDelta))
                        {
                            if (toolCallBuffers.TryGetValue(chunk.ToolCallId, out var buffer))
                            {
                                buffer.ArgumentsBuilder.Append(chunk.ArgumentsDelta);
                            }
                        }
                        
                        // Finish reason
                        if (chunk.FinishReason.HasValue)
                        {
                            finishReason = chunk.FinishReason.Value;
                        }
                        
                        // Usage
                        if (chunk.InputTokens.HasValue)
                            inputTokens = chunk.InputTokens.Value;
                        if (chunk.OutputTokens.HasValue)
                            outputTokens = chunk.OutputTokens.Value;
                    }
                    
                    // Parse accumulated tool calls
                    IReadOnlyList<ToolCallRequest>? toolCalls = null;
                    if (toolCallBuffers.Count > 0)
                    {
                        var parsedToolCalls = new List<ToolCallRequest>();
                        foreach (var buffer in toolCallBuffers.Values)
                        {
                            var argumentsJson = buffer.ArgumentsBuilder.ToString();
                            Dictionary<string, object?> arguments;
                            
                            if (!string.IsNullOrWhiteSpace(argumentsJson))
                            {
                                try
                                {
                                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
                                }
                                catch (JsonException ex)
                                {
                                    _logger.LogWarning(ex, "Failed to parse tool arguments for {ToolName}: {Json}", buffer.Name, argumentsJson);
                                    arguments = new Dictionary<string, object?>();
                                }
                            }
                            else
                            {
                                arguments = new Dictionary<string, object?>();
                            }
                            
                            parsedToolCalls.Add(new ToolCallRequest(buffer.Id, buffer.Name, arguments));
                        }
                        toolCalls = parsedToolCalls;
                    }
                    
                    llmResponse = new LlmResponse(contentBuilder.ToString(), finishReason, toolCalls, inputTokens, outputTokens);
                }
                else
                {
                    // Non-streaming mode (backward compatible)
                    llmResponse = await provider.ChatAsync(request, cancellationToken).ConfigureAwait(false);
                }
                
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

                // Check if we have tool calls to execute
                var hasToolCalls = llmResponse.FinishReason == FinishReason.ToolCalls && llmResponse.ToolCalls is { Count: > 0 };
                
                if (!hasToolCalls)
                {
                    // No tool calls - standard pattern: break unless blank content (finalization retry)
                    var hasContent = !string.IsNullOrWhiteSpace(llmResponse.Content);
                    
                    if (!hasContent && iteration < _maxToolIterations - 1)
                    {
                        // Blank content - nanobot-style finalization retry
                        _logger.LogInformation("Agent {AgentName}: blank response, requesting finalization (iteration {Iteration})",
                            _agentName, iteration);
                        
                        // Retry with tools disabled to force a final answer
                        var finalizationHistory = session.History
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
                        
                        var finalizationMessages = await _contextBuilder.BuildMessagesAsync(
                            _agentName,
                            finalizationHistory,
                            "You have finished the tool work. Provide your final answer now.",
                            message.Channel,
                            message.ChatId,
                            cancellationToken).ConfigureAwait(false);
                        
                        var finalizationRequest = new ChatRequest(
                            finalizationMessages.Where(static m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).ToList(),
                            effectiveSettings,
                            Tools: null, // Disable tools for finalization
                            systemPrompt);
                        
                        var finalizationProvider = ResolveProvider(effectiveModel);
                        var finalizationTimer = Stopwatch.StartNew();
                        var finalizationResponse = await finalizationProvider.ChatAsync(finalizationRequest, cancellationToken).ConfigureAwait(false);
                        finalizationTimer.Stop();
                        _logger.LogInformation("Finalization request completed in {ElapsedMs}ms", finalizationTimer.Elapsed.TotalMilliseconds);
                        
                        if (!string.IsNullOrWhiteSpace(finalizationResponse.Content))
                        {
                            response = finalizationResponse.Content;
                            session.AddEntry(new SessionEntry(
                                MessageRole.Assistant,
                                finalizationResponse.Content,
                                DateTimeOffset.UtcNow));
                        }
                    }
                    
                    _logger.LogInformation("Agent {AgentName}: breaking loop at iteration {Iteration}, FinishReason={FinishReason}, ToolCalls={ToolCallCount}, HasContent={HasContent}",
                        _agentName, iteration, llmResponse.FinishReason, llmResponse.ToolCalls?.Count ?? 0, hasContent);
                    break;
                }

                _logger.LogInformation("Agent {AgentName}: executing {Count} tool calls (iteration {Iteration})",
                    _agentName, llmResponse.ToolCalls!.Count, iteration);

                foreach (var toolCall in llmResponse.ToolCalls!)
                {
                    // Check for repeated tool call (loop detection)
                    var signature = ComputeToolCallSignature(toolCall);
                    var currentCount = _toolCallSignatures.GetValueOrDefault(signature, 0);
                    var maxRepeatedCalls = _settings.MaxRepeatedToolCalls;

                    if (currentCount >= maxRepeatedCalls)
                    {
                        // Block the repeated call and return error to LLM
                        var errorMessage = $"Error: Loop detected. Tool '{toolCall.ToolName}' called {currentCount + 1} times with identical arguments. Try a different approach.";
                        _logger.LogWarning("Agent {AgentName}: Blocked repeated tool call '{ToolName}' (attempt {Count}/{Max}), signature: {Signature}",
                            _agentName, toolCall.ToolName, currentCount + 1, maxRepeatedCalls, signature);

                        session.AddEntry(new SessionEntry(
                            MessageRole.Tool,
                            errorMessage,
                            DateTimeOffset.UtcNow,
                            ToolName: toolCall.ToolName,
                            ToolCallId: toolCall.Id));
                        continue;
                    }

                    // Increment the count for this signature
                    _toolCallSignatures[signature] = currentCount + 1;

                    var toolProgressMessage = $"🔧 Using tool: {toolCall.ToolName}";
                    
                    // Stream tool progress via callback if available
                    if (onDelta is not null)
                    {
                        await onDelta($"\n\n{toolProgressMessage}\n").ConfigureAwait(false);
                    }
                    
                    // Also publish to activity stream for subscribers
                    if (_activityStream is not null)
                    {
                        await _activityStream.PublishAsync(new Core.Models.ActivityEvent(
                            ActivityEventType.AgentProcessing,
                            message.Channel,
                            $"{message.Channel}:{message.ChatId}",
                            message.ChatId,
                            _agentName,
                            toolProgressMessage,
                            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                    }
                    
                    var toolResult = await _toolRegistry.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
                    session.AddEntry(new SessionEntry(
                        MessageRole.Tool,
                        toolResult,
                        DateTimeOffset.UtcNow,
                        ToolName: toolCall.ToolName,
                        ToolCallId: toolCall.Id));
                }
                
                // After tool execution, let the client know we're processing the results
                if (onDelta is not null && llmResponse.ToolCalls!.Count > 0)
                {
                    await onDelta("\n\n💭 Processing tool results...\n").ConfigureAwait(false);
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

    private ILlmProvider ResolveProvider(string? modelOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(_providerName))
        {
            var configured = _providerRegistry.Get(_providerName);
            if (configured is not null)
            {
                _logger.LogDebug("Resolved provider by name: {ProviderName}", _providerName);
                return configured;
            }
        }

        var configuredModel = string.IsNullOrWhiteSpace(modelOverride) 
            ? (string.IsNullOrWhiteSpace(_model) ? _settings.Model : _model)
            : modelOverride;
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            var fromModelPrefix = ResolveProviderFromModelPrefix(configuredModel);
            if (fromModelPrefix is not null)
            {
                _logger.LogDebug("Resolved provider from model prefix: {Model} -> {ProviderName}", configuredModel, fromModelPrefix.GetType().Name);
                return fromModelPrefix;
            }

            foreach (var name in _providerRegistry.GetProviderNames())
            {
                var provider = _providerRegistry.Get(name);
                if (provider is not null &&
                    string.Equals(provider.DefaultModel, configuredModel, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Resolved provider by default model match: {Model} -> {ProviderName}", configuredModel, provider.GetType().Name);
                    return provider;
                }
            }
        }

        var byDefault = _providerRegistry.GetDefault();
        if (byDefault is not null)
        {
            _logger.LogDebug("Using default provider: {ProviderName}, defaultModel={DefaultModel}", byDefault.GetType().Name, byDefault.DefaultModel);
            return byDefault;
        }

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

    /// <summary>
    /// Computes a signature for a tool call based on tool name and normalized arguments.
    /// </summary>
    private string ComputeToolCallSignature(ToolCallRequest toolCall)
    {
        // Normalize: tool name + sorted arguments JSON
        var argsJson = JsonSerializer.Serialize(toolCall.Arguments, new JsonSerializerOptions { WriteIndented = false });
        return $"{toolCall.ToolName}::{argsJson}";
    }
}

/// <summary>
/// Buffer for accumulating streaming tool call arguments.
/// </summary>
internal sealed class ToolCallStreamBuffer
{
    public string Id { get; }
    public string Name { get; }
    public System.Text.StringBuilder ArgumentsBuilder { get; } = new();

    public ToolCallStreamBuffer(string id, string name)
    {
        Id = id;
        Name = name;
    }
}
