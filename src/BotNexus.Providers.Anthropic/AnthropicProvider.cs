using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Anthropic;

/// <summary>
/// Anthropic Claude provider implementation using the Anthropic Messages API directly via HttpClient.
/// </summary>
public sealed class AnthropicProvider : LlmProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly string _defaultModel;
    private const string ApiVersion = "2023-06-01";
    private const string DefaultApiBase = "https://api.anthropic.com";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnthropicProvider(
        string apiKey,
        string model = "claude-3-5-sonnet-20241022",
        string? apiBase = null,
        ILogger<AnthropicProvider>? logger = null,
        int maxRetries = 3)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicProvider>.Instance, maxRetries)
    {
        _defaultModel = model;
        Generation = new GenerationSettings { Model = model };

        _httpClient = new HttpClient { BaseAddress = new Uri(apiBase ?? DefaultApiBase) };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc/>
    public override string DefaultModel => _defaultModel;

    /// <inheritdoc/>
    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(request, stream: false);
        var json = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("/v1/messages", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var text = root.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        var finishReason = stopReason == "end_turn" ? FinishReason.Stop
            : stopReason == "max_tokens" ? FinishReason.Length
            : FinishReason.Other;

        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }

        return new LlmResponse(text, finishReason, null, inputTokens, outputTokens);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<string> ChatStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(request, stream: true);
        var json = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = content };
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return text;
                }
            }
        }
    }

    private Dictionary<string, object?> BuildRequestBody(ChatRequest request, bool stream)
    {
        var settings = request.Settings;
        var messages = request.Messages
            .Where(m => m.Role != "system")
            .Select(m => new { role = m.Role, content = m.Content })
            .ToList<object>();

        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["max_tokens"] = settings.MaxTokens,
            ["temperature"] = settings.Temperature,
            ["messages"] = messages,
            ["stream"] = stream
        };

        var systemPrompt = request.SystemPrompt
            ?? request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        if (systemPrompt is not null)
            body["system"] = systemPrompt;

        return body;
    }
}
