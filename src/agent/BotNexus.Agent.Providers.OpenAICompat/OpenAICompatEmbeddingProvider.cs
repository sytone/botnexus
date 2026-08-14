using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Embeddings;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.OpenAICompat;

/// <summary>
/// <see cref="IEmbeddingProvider"/> over the OpenAI-compatible <c>POST {baseUrl}/embeddings</c>
/// endpoint (#2855).
/// </summary>
/// <remarks>
/// <para>
/// One implementation covers OpenAI, Azure OpenAI, Ollama and every other endpoint that speaks the
/// same shape, because the only thing that differs between them is <c>baseUrl</c> and the bearer
/// token. That is the same reasoning that made <see cref="OpenAICompatProvider"/> a single type
/// rather than one per vendor.
/// </para>
/// <para>
/// This deliberately does NOT implement <see cref="Core.Registry.IApiProvider"/>. Chat and
/// embeddings are separate endpoints with separate model catalogues; keeping the capabilities in
/// separate types is what lets a provider expose one without the other.
/// </para>
/// </remarks>
public sealed class OpenAICompatEmbeddingProvider : IEmbeddingProvider
{
    // An embeddings response is a fixed-width float array plus a little envelope, orders of
    // magnitude smaller than a chat stream. Cap it tight so a hostile or misconfigured endpoint
    // cannot stream an unbounded body into memory on the memory-write path.
    private const long MaxResponseBytes = 8L * 1024 * 1024;
    private const long ErrorBodyLimitBytes = 64L * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly ISecretRedactor? _secretRedactor;

    /// <param name="httpClient">Transport. Supplied by composition so tests can inject a handler.</param>
    /// <param name="providerKey">Provider key this capability is registered under, e.g. <c>ollama</c>.</param>
    /// <param name="baseUrl">Endpoint base URL, e.g. <c>http://localhost:11434/v1</c>.</param>
    /// <param name="models">Embedding models this endpoint serves, with their declared widths.</param>
    /// <param name="apiKey">Optional bearer token. Omitted for a local endpoint that wants none.</param>
    /// <param name="secretRedactor">
    /// Optional redactor applied to the endpoint's error body before it reaches an exception message
    /// (#2881). An endpoint that echoes the offending <c>Authorization</c> header back on a 401 would
    /// otherwise leak the credential into a session-visible error.
    /// </param>
    public OpenAICompatEmbeddingProvider(
        HttpClient httpClient,
        string providerKey,
        string baseUrl,
        IReadOnlyList<EmbeddingModelDescriptor> models,
        string? apiKey = null,
        ISecretRedactor? secretRedactor = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(models);

        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _secretRedactor = secretRedactor;
        ProviderKey = providerKey;
        Models = models;
    }

    /// <inheritdoc />
    public string ProviderKey { get; }

    /// <inheritdoc />
    public IReadOnlyList<EmbeddingModelDescriptor> Models { get; }

    /// <summary>The endpoint this provider posts to. Exposed so fingerprint derivation can include it.</summary>
    public string BaseUrl => _baseUrl;

    /// <inheritdoc />
    public async Task<float[]?> EmbedAsync(string modelId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["input"] = text,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/embeddings")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (_apiKey is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedAsync(response, ErrorBodyLimitBytes, ct).ConfigureAwait(false);
            // Routed through the shared choke point rather than interpolated here (#2881): the body
            // is untrusted credential-bearing text, and this helper redacts it BEFORE any string
            // interpolation as well as mapping 401/403/429 to their diagnosable exception types.
            // It always throws; the return below is unreachable and only satisfies the compiler.
            ProviderHttpErrorHelper.ThrowForFailedResponse(
                response, errorBody, $"{ProviderKey} embeddings ({_baseUrl}/embeddings, model '{modelId}')", _secretRedactor);
            return null;
        }

        var payload = await ReadBoundedAsync(response, MaxResponseBytes, ct).ConfigureAwait(false);
        return ParseVector(payload);
    }

    /// <summary>
    /// Extracts <c>data[0].embedding</c>. Returns <see langword="null"/> for a well-formed response
    /// that carried no vector, so the caller can tell "nothing to say" from "endpoint is broken".
    /// </summary>
    internal static float[]? ParseVector(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return null;
            }

            var first = data[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var vector = new float[embedding.GetArrayLength()];
            var index = 0;
            foreach (var component in embedding.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Number)
                    return null;

                vector[index++] = component.GetSingle();
            }

            return vector;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        try
        {
            return await BoundedHttpContent.ReadStringWithLimitAsync(response.Content, limit, ct).ConfigureAwait(false);
        }
        catch (ResponseContentTooLargeException)
        {
            return $"<body exceeded {limit} bytes and was discarded>";
        }
    }
}
