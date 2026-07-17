using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot.Responses;

/// <summary>
/// GitHub Copilot Responses provider. Wire transport selection remains private to this provider;
/// callers always consume the same normalized <see cref="LlmStream"/> events.
/// </summary>
public sealed class CopilotResponsesProvider : IApiProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CopilotResponsesProvider> _logger;
    private readonly Func<ICopilotResponsesWebSocketTransport> _webSocketFactory;

    /// <summary>Creates the Copilot Responses provider with the production WebSocket adapter.</summary>
    public CopilotResponsesProvider(HttpClient httpClient, ILogger<CopilotResponsesProvider> logger)
        : this(httpClient, logger, static () => new CopilotResponsesWebSocketTransport())
    {
    }

    internal CopilotResponsesProvider(
        HttpClient httpClient,
        ILogger<CopilotResponsesProvider> logger,
        ICopilotResponsesWebSocketTransport webSocket)
        : this(httpClient, logger, () => webSocket)
    {
    }

    private CopilotResponsesProvider(
        HttpClient httpClient,
        ILogger<CopilotResponsesProvider> logger,
        Func<ICopilotResponsesWebSocketTransport> webSocketFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webSocketFactory = webSocketFactory;
    }

    /// <inheritdoc />
    public string Api => "github-copilot-responses";

    /// <inheritdoc />
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var preference = options is CopilotResponsesOptions copilotOptions
            ? copilotOptions.TransportPreference
            : CopilotResponsesTransportPreference.Auto;
        var selected = CopilotResponsesTransportPolicy.Select(model, preference);
        if (selected == CopilotResponsesWireTransport.Sse)
            return StreamSse(model, context, options);

        var output = new LlmStream();
        // Do not pass the request token to Task.Run: an already-cancelled token would prevent the
        // producer from starting and leave consumers waiting forever for a terminal event.
        _ = Task.Run(() => StreamWebSocketWithSafeFallbackAsync(output, model, context, options));
        return output;
    }

    /// <inheritdoc />
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var reasoning = ModelRegistry.SupportsExtraHigh(model) ? options?.Reasoning : SimpleOptionsHelper.ClampReasoning(options?.Reasoning);
        var responsesOptions = new CopilotResponsesOptions
        {
            ApiKey = apiKey,
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
            CancellationToken = options?.CancellationToken ?? CancellationToken.None,
            Transport = options?.Transport ?? Transport.Sse,
            CacheRetention = options?.CacheRetention ?? CacheRetention.Short,
            SessionId = options?.SessionId,
            OnPayload = options?.OnPayload,
            Headers = options?.Headers,
            MaxRetryDelayMs = options?.MaxRetryDelayMs ?? 60000,
            Metadata = options?.Metadata
        };
        if (reasoning is not null && model.Reasoning)
            responsesOptions.ReasoningEffort = MapThinkingLevel(reasoning.Value);
        return Stream(model, context, responsesOptions);
    }

    private async Task StreamWebSocketWithSafeFallbackAsync(
        LlmStream output,
        LlmModel model,
        Context context,
        StreamOptions? options)
    {
        using var activity = ProviderDiagnostics.Source.StartActivity("provider.copilot-responses.stream", ActivityKind.Client);
        var descriptor = CopilotResolvedModelDescriptors.Get(model);
        activity?.SetTag("botnexus.provider.transport.advertised", string.Join(',', descriptor.AdvertisedEndpoints));
        activity?.SetTag("botnexus.provider.transport.selected", "websocket");
        _logger.LogDebug(
            "Copilot Responses transport selected {Transport} for {Model}; advertised endpoints: {AdvertisedEndpoints}",
            "websocket", model.Id, string.Join(", ", descriptor.AdvertisedEndpoints));

        var semanticOutput = false;
        AssistantMessage? partial = null;
        try
        {
            await using var socket = _webSocketFactory();
            var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");

            var messages = MessageTransformer.TransformMessages(context.Messages, model);
            var payload = CopilotResponsesRequestBuilder.Build(
                model, context.SystemPrompt, messages, context.Tools, options,
                ResponsesMessageConverter.ConvertMessages, ResponsesMessageConverter.ConvertTools);
            if (options?.OnPayload is not null && await options.OnPayload(payload, model).ConfigureAwait(false) is JsonObject modified)
                payload = modified;
            payload.Remove("stream");
            payload["type"] = "response.create";

            var headers = BuildHeaders(model, context.Messages, options, apiKey);
            var uri = new UriBuilder(model.BaseUrl.TrimEnd('/') + "/responses")
            {
                Scheme = model.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
            }.Uri;
            await socket.ConnectAsync(uri, headers, options?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);
            await socket.SendAsync(payload.ToJsonString(), options?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);

            var normalized = new LlmStream();
            Exception? parseFailure = null;
            var receivedTerminalEvent = false;
            var parseTask = Task.Run(async () =>
            {
                try
                {
                    await ResponsesStreamParser.ParseEventsAsync(
                        normalized,
                        async ct =>
                        {
                            var json = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                            if (json is null)
                            {
                                if (!receivedTerminalEvent)
                                    throw new WebSocketException("Copilot Responses WebSocket closed before a terminal response event.");
                                return null;
                            }

                            using var document = JsonDocument.Parse(json);
                            var type = document.RootElement.TryGetProperty("type", out var typeElement)
                                ? typeElement.GetString() ?? "message"
                                : "message";
                            receivedTerminalEvent = type is "response.completed" or "response.done" or "response.failed" or "error";
                            return new ResponsesEvent(type, json);
                        },
                        model, options, Api, _logger,
                        static (stream, failedModel, message, content) => ResponsesStreamEngine.EmitError(stream, "github-copilot-responses", failedModel, message, content),
                        static root => Telemetry.CopilotUsageActivity.TryParseAndEmit(root, Activity.Current),
                        static value => value is CopilotResponsesOptions responseOptions ? responseOptions.ServiceTier : null,
                        NormalizeTextDelta,
                        options?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    parseFailure = ex;
                    normalized.End();
                }
            });

            await foreach (var evt in normalized.WithCancellation(options?.CancellationToken ?? CancellationToken.None))
            {
                semanticOutput |= IsSemantic(evt);
                partial = GetPartial(evt) ?? partial;
                output.Push(evt);
            }
            await parseTask.ConfigureAwait(false);
            if (parseFailure is not null)
                throw parseFailure;
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !semanticOutput)
        {
            activity?.SetTag("botnexus.provider.transport.fallback", "sse");
            activity?.SetTag("botnexus.provider.transport.fallback_reason", ex.GetType().Name);
            _logger.LogWarning(ex,
                "Copilot Responses WebSocket failed before semantic output for {Model}; falling back to SSE", model.Id);
            await ForwardAsync(StreamSse(model, context, options), output, options?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ResponsesStreamEngine.EmitAborted(output, Api, model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Copilot Responses WebSocket failed after semantic output for {Model}; SSE replay is suppressed", model.Id);
            ResponsesStreamEngine.EmitError(output, Api, model, ex.Message, partial?.Content);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
    }

    private static bool IsSemantic(AssistantMessageEvent evt) => evt is
        TextStartEvent or TextDeltaEvent or ThinkingStartEvent or ThinkingDeltaEvent or
        ToolCallStartEvent or ToolCallDeltaEvent;

    private static AssistantMessage? GetPartial(AssistantMessageEvent evt) => evt switch
    {
        StartEvent value => value.Partial,
        TextStartEvent value => value.Partial,
        TextDeltaEvent value => value.Partial,
        TextEndEvent value => value.Partial,
        ThinkingStartEvent value => value.Partial,
        ThinkingDeltaEvent value => value.Partial,
        ThinkingEndEvent value => value.Partial,
        ToolCallStartEvent value => value.Partial,
        ToolCallDeltaEvent value => value.Partial,
        ToolCallEndEvent value => value.Partial,
        _ => null
    };

    private static async Task ForwardAsync(LlmStream source, LlmStream destination, CancellationToken cancellationToken)
    {
        await foreach (var evt in source.WithCancellation(cancellationToken))
            destination.Push(evt);
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(
        LlmModel model,
        IReadOnlyList<Message> messages,
        StreamOptions? options,
        string apiKey)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {apiKey}",
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        };
        if (model.Headers is not null)
            foreach (var (key, value) in model.Headers) headers[key] = value;
        var hasImages = CopilotHeaders.HasVisionInput(messages);
        var headerOptions = CopilotInteractionId.WithResolvedInteractionId((options as CopilotResponsesOptions)?.HeaderOptions);
        foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages, headerOptions)) headers[key] = value;
        if (options?.Headers is not null)
            foreach (var (key, value) in options.Headers) headers[key] = value;
        return headers;
    }

    private LlmStream StreamSse(LlmModel model, Context context, StreamOptions? options)
        => ResponsesStreamEngine.StreamAsync(BuildProfile(_logger), _httpClient, _logger, model, context, options);

    private static ResponsesTransportProfile BuildProfile(ILogger logger) => new(
        Api: "github-copilot-responses",
        ActivityName: "provider.copilot-responses.stream",
        BuildPayload: static (model, systemPrompt, messages, tools, options) =>
            CopilotResponsesRequestBuilder.Build(model, systemPrompt, messages, tools, options,
                ResponsesMessageConverter.ConvertMessages, ResponsesMessageConverter.ConvertTools),
        Parse: (stream, reader, model, options, api, emitError, ct) =>
            ResponsesStreamParser.ParseAsync(stream, reader, model, options, api, logger, emitError,
                static root => Telemetry.CopilotUsageActivity.TryParseAndEmit(root, Activity.Current),
                static value => value is CopilotResponsesOptions responseOptions ? responseOptions.ServiceTier : null,
                NormalizeTextDelta, ct),
        DecorateHeaders: static (request, _, messages, options) =>
        {
            var hasImages = CopilotHeaders.HasVisionInput(messages);
            var headerOptions = CopilotInteractionId.WithResolvedInteractionId((options as CopilotResponsesOptions)?.HeaderOptions);
            foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages, headerOptions))
                request.Headers.TryAddWithoutValidation(key, value);
        },
        ThrowForError: static (response, errorBody) =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, "Copilot Responses"),
        OnResponseHeaders: static response => CopilotResponseHeaders.EmitToActivity(response, Activity.Current));

    private static string NormalizeTextDelta(LlmModel model, string delta)
    {
        if (model.Id.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase) &&
            delta.StartsWith("\r\n", StringComparison.Ordinal))
            return delta[2..];
        return delta;
    }

    private static string MapThinkingLevel(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        ThinkingLevel.Max => "xhigh",
        _ => "medium"
    };
}
