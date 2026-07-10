using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// A single model choice surfaced to a config <c>select</c> whose <c>x-ui-options-source</c> is
/// <c>"models"</c> (#1893). Carries the model id plus the capability lists the renderer uses to
/// derive dependent <c>thinking</c> / <c>contextSizes</c> option sets when a model is selected.
/// Mirrors the <c>ModelInfo</c> DTO returned by <c>GET /api/models</c>.
/// </summary>
public sealed record ModelOption(
    string ModelId,
    string Name,
    IReadOnlyList<string> SupportedThinkingLevels,
    IReadOnlyList<int> SupportedContextSizes);

/// <summary>
/// Resolves the dynamic option lists a schema-driven config <c>select</c> needs when it declares an
/// <c>x-ui-options-source</c> (#1893, config parity #1579). The generic <c>SchemaForm</c> renderer
/// depends on this abstraction (not <c>HttpClient</c> directly) so it stays host-agnostic and
/// unit-testable: tests supply a stub, desktop/mobile register <see cref="HttpModelOptionsProvider"/>.
/// </summary>
public interface IModelOptionsProvider
{
    /// <summary>
    /// Returns the registered models for <paramref name="provider"/> (the enclosing providers-dictionary
    /// key), or an empty list when the provider is unknown or the lookup fails. Implementations should
    /// cache so repeated render passes do not re-fetch.
    /// </summary>
    Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider);
}

/// <summary>
/// Default <see cref="IModelOptionsProvider"/> backed by the gateway <c>GET /api/models?provider=</c>
/// endpoint, with a per-provider in-memory cache. Failures degrade to an empty list so a select falls
/// back to any static <c>x-ui-options</c> rather than throwing during render.
/// </summary>
public sealed class HttpModelOptionsProvider : IModelOptionsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Dictionary<string, IReadOnlyList<ModelOption>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="HttpModelOptionsProvider"/>
    public HttpModelOptionsProvider(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return [];
        if (_cache.TryGetValue(provider, out var cached))
            return cached;

        try
        {
            var url = $"/api/models?provider={Uri.EscapeDataString(provider)}";
            var items = await _http.GetFromJsonAsync<List<ModelInfoDto>>(url, JsonOptions);
            var result = (items ?? [])
                .Select(m => new ModelOption(
                    m.ModelId ?? m.Id ?? string.Empty,
                    string.IsNullOrWhiteSpace(m.Name) ? (m.ModelId ?? m.Id ?? string.Empty) : m.Name,
                    m.SupportedThinkingLevels ?? [],
                    m.SupportedContextSizes ?? []))
                .Where(m => !string.IsNullOrEmpty(m.ModelId))
                .ToList();
            _cache[provider] = result;
            return result;
        }
        catch
        {
            _cache[provider] = [];
            return [];
        }
    }

    // Wire shape of the /api/models ModelInfo response (subset the renderer needs).
    private sealed class ModelInfoDto
    {
        public string? Name { get; set; }
        public string? ModelId { get; set; }
        public string? Id { get; set; }
        public List<string>? SupportedThinkingLevels { get; set; }
        public List<int>? SupportedContextSizes { get; set; }
    }
}
