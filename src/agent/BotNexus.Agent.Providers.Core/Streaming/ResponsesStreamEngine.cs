using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Shared streaming engine for the OpenAI and Copilot Responses providers.
/// <para>
/// The two Responses providers were ~97% character-identical god-classes. Their only genuine
/// behavioral deltas are captured by <see cref="ResponsesTransportProfile"/>:
/// (a) always-on vs conditional dynamic-header decoration,
/// (b) Copilot response-header telemetry,
/// (c) Copilot's stricter HTTP error projection,
/// plus the project-internal request builder / stream parser supplied as delegates. Everything else —
/// the request loop, message/tool conversion (via <see cref="ResponsesMessageConverter"/>), and the
/// error/abort emit shapes — lives here once (step 6/6 of #1377). Each provider collapses to a thin
/// shell that constructs a profile and calls <see cref="StreamAsync"/>.
/// </para>
/// <para>
/// The reasoning <c>{ effort: "none" }</c> fallback delta is encapsulated inside each provider's own
/// request builder (a <c>model.Provider == "github-copilot"</c> check), not here.
/// </para>
/// </summary>
public static class ResponsesStreamEngine
{
    /// <summary>
    /// Builds the canonical Responses <see cref="LlmStream"/> for a provider: starts the activity
    /// span, runs the request loop on a background task, and routes cancellation/errors to the shared
    /// abort/error emit shapes. Mirrors the (now-removed) per-provider <c>Stream</c> body.
    /// </summary>
    public static LlmStream StreamAsync(
        ResponsesTransportProfile profile,
        HttpClient httpClient,
        ILogger logger,
        LlmModel model,
        Context context,
        StreamOptions? options)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            using var activity = ProviderDiagnostics.Source.StartActivity(profile.ActivityName, ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(profile, httpClient, stream, model, context, options, ct);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (OperationCanceledException)
            {
                EmitAborted(stream, profile.Api, model);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Api} stream failed for model {Model}", profile.Api, model.Id);
                EmitError(stream, profile.Api, model, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }, ct);

        return stream;
    }

    private static async Task StreamCoreAsync(
        ResponsesTransportProfile profile,
        HttpClient httpClient,
        LlmStream stream,
        LlmModel model,
        Context context,
        StreamOptions? options,
        CancellationToken ct)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var messages = MessageTransformer.TransformMessages(context.Messages, model);
        var payload = profile.BuildPayload(model, context.SystemPrompt, messages, context.Tools, options);

        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(payload, model);
            if (modified is JsonObject obj)
                payload = obj;
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/responses";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        // Header decoration differs per transport (OpenAI conditional vs Copilot always-on). The
        // original providers pass the untransformed context.Messages for vision detection here.
        profile.DecorateHeaders(request, model, context.Messages, options);

        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        profile.OnResponseHeaders?.Invoke(response);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            profile.ThrowForError(response, errorBody);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        await profile.Parse(
            stream, reader, model, options, profile.Api,
            (s, m, msg, partial) => EmitError(s, profile.Api, m, msg, partial),
            ct);
    }

    /// <summary>
    /// Pushes the canonical error event/end for a failed Responses turn, optionally carrying any
    /// partial content already streamed.
    /// </summary>
    public static void EmitError(
        LlmStream stream,
        string api,
        LlmModel model,
        string errorMessage,
        IReadOnlyList<ContentBlock>? partialContent = null)
    {
        var message = new AssistantMessage(
            Content: partialContent?.ToList() ?? [],
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: errorMessage,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new ErrorEvent(StopReason.Error, message));
        stream.End(message);
    }

    /// <summary>
    /// Pushes the canonical done event/end for a cancelled Responses turn.
    /// </summary>
    public static void EmitAborted(LlmStream stream, string api, LlmModel model)
    {
        var message = new AssistantMessage(
            Content: [],
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Aborted,
            ErrorMessage: "Request was cancelled",
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Aborted, message));
        stream.End(message);
    }
}
