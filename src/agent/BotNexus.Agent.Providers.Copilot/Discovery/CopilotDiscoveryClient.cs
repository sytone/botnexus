using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot.Discovery;

/// <summary>
/// Read-only client for GitHub Copilot's discovery endpoints. Used by CLI
/// diagnostics (<c>provider copilot whoami|models|quota</c>) and any caller
/// that needs to inspect the authenticated user's entitlement before invoking
/// an LLM. The client is intentionally stateless — callers supply the
/// credentials and (where relevant) the resolved API endpoint per request so
/// the same instance can serve enterprise and individual users side by side.
/// </summary>
public sealed class CopilotDiscoveryClient
{
    /// <summary>
    /// GitHub.com endpoint that returns the authenticated user's Copilot plan,
    /// entitlement, organization list, regional API endpoints, and current
    /// per-feature quota snapshots.
    /// </summary>
    public const string UserInfoUrl = "https://api.github.com/copilot_internal/user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public CopilotDiscoveryClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Fetches the authenticated user's Copilot entitlement and endpoint
    /// configuration from <see cref="UserInfoUrl"/>. The <paramref name="githubToken"/>
    /// must be a GitHub OAuth token (gho_* / ghu_*) — Copilot-exchanged session
    /// tokens are rejected by this endpoint.
    /// </summary>
    public async Task<CopilotUserInfo> GetUserAsync(string githubToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            throw new ArgumentException("GitHub OAuth token is required.", nameof(githubToken));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {githubToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "BotNexus-CLI/1.0");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<CopilotUserInfo>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return info ?? throw new InvalidOperationException("Copilot user-info response was empty.");
    }

    /// <summary>
    /// Lists every model the authenticated user can invoke through GitHub Copilot.
    /// <paramref name="endpointBase"/> must be the <c>endpoints.api</c> value
    /// returned by <see cref="GetUserAsync"/> (enterprise vs individual);
    /// <paramref name="copilotSessionToken"/> must be the short-lived Copilot
    /// session token returned by <see cref="CopilotOAuth.RefreshAsync"/>.
    /// </summary>
    public async Task<CopilotModelsResponse> GetModelsAsync(
        string endpointBase,
        string copilotSessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointBase))
        {
            throw new ArgumentException("Copilot API endpoint base URL is required.", nameof(endpointBase));
        }

        if (string.IsNullOrWhiteSpace(copilotSessionToken))
        {
            throw new ArgumentException("Copilot session token is required.", nameof(copilotSessionToken));
        }

        var url = endpointBase.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {copilotSessionToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "copilot-developer-cli");
        request.Headers.TryAddWithoutValidation("Editor-Version", "copilot/1.0.59");
        request.Headers.TryAddWithoutValidation("User-Agent", "BotNexus-CLI/1.0");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-06-01");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var models = await response.Content.ReadFromJsonAsync<CopilotModelsResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return models ?? throw new InvalidOperationException("Copilot models response was empty.");
    }
}
