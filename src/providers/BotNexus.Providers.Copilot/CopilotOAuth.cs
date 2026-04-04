using System.Net.Http.Headers;
using System.Text.Json;

namespace BotNexus.Providers.Copilot;

/// <summary>
/// OAuth credentials for GitHub Copilot.
/// AccessToken holds the current usable token (Copilot session token after exchange).
/// RefreshToken holds the long-lived GitHub OAuth token for re-exchange.
/// ExpiresAt is Unix epoch seconds.
/// </summary>
public record OAuthCredentials(string AccessToken, string RefreshToken, long ExpiresAt);

/// <summary>
/// GitHub Copilot OAuth device code flow and token management.
/// </summary>
public static class CopilotOAuth
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Perform GitHub device code OAuth flow for Copilot access.
    /// Returns credentials with the GitHub OAuth token (requires exchange before API use).
    /// </summary>
    public static async Task<OAuthCredentials> LoginAsync(
        Func<string, string?, Task> onAuth,
        Func<string, Task<string>>? onPrompt = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // Step 1: Request device code
        using var codeResponse = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user"
        }, ct);

        var codeJson = await ReadJsonAsync(codeResponse, ct);
        var deviceCode = codeJson.GetProperty("device_code").GetString()!;
        var userCode = codeJson.GetProperty("user_code").GetString()!;
        var verificationUri = codeJson.GetProperty("verification_uri").GetString()!;
        var interval = codeJson.TryGetProperty("interval", out var intervalEl) ? intervalEl.GetInt32() : 5;
        var expiresIn = codeJson.TryGetProperty("expires_in", out var expiresEl) ? expiresEl.GetInt32() : 900;

        // Step 2: Present auth URL and code to user
        await onAuth(verificationUri, userCode);

        // Step 3: Poll for authorization
        var pollInterval = Math.Max(interval, 1);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct);
            onProgress?.Invoke("Waiting for authorization...");

            using var tokenResponse = await PostFormAsync(AccessTokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, ct);

            var tokenJson = await ReadJsonAsync(tokenResponse, ct);

            if (tokenJson.TryGetProperty("access_token", out var accessTokenEl))
            {
                var accessToken = accessTokenEl.GetString()!;
                // Return credentials with ExpiresAt = 0 to force Copilot token exchange on first use
                return new OAuthCredentials(accessToken, "", 0);
            }

            var error = tokenJson.TryGetProperty("error", out var errorEl) ? errorEl.GetString() : null;

            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    pollInterval += 5;
                    continue;
                case "access_denied":
                    throw new InvalidOperationException("GitHub device authorization was denied by the user.");
                case "expired_token":
                    throw new TimeoutException("GitHub device code expired before authorization was completed.");
            }

            var description = tokenJson.TryGetProperty("error_description", out var descEl)
                ? descEl.GetString() : "Unknown error";
            throw new InvalidOperationException($"GitHub OAuth error: {error} — {description}");
        }

        throw new TimeoutException("Timed out waiting for GitHub device code authorization.");
    }

    /// <summary>
    /// Refresh expired Copilot credentials by re-exchanging the GitHub token.
    /// </summary>
    public static async Task<OAuthCredentials> RefreshAsync(
        OAuthCredentials credentials, CancellationToken ct = default)
    {
        // Use RefreshToken (GitHub OAuth token) if available, otherwise AccessToken is the GitHub token
        var githubToken = !string.IsNullOrEmpty(credentials.RefreshToken)
            ? credentials.RefreshToken
            : credentials.AccessToken;

        var (copilotToken, expiresAt, _) = await ExchangeForCopilotTokenAsync(githubToken, ct);
        return new OAuthCredentials(copilotToken, githubToken, expiresAt);
    }

    /// <summary>
    /// Get a usable Copilot API key, auto-refreshing if expired.
    /// Returns null if the provider has no credentials.
    /// </summary>
    public static async Task<(OAuthCredentials NewCredentials, string ApiKey)?> GetApiKeyAsync(
        string provider, Dictionary<string, OAuthCredentials> credentialsMap, CancellationToken ct = default)
    {
        if (!credentialsMap.TryGetValue(provider, out var credentials))
            return null;

        // Refresh if expired or within 60s of expiry
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= credentials.ExpiresAt - 60)
        {
            credentials = await RefreshAsync(credentials, ct);
        }

        return (credentials, credentials.AccessToken);
    }

    private static async Task<(string Token, long ExpiresAt, string? ApiEndpoint)> ExchangeForCopilotTokenAsync(
        string githubToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"token {githubToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "BotNexus/0.1");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.0");

        var response = await SharedClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await ReadJsonAsync(response, ct);
        var token = json.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Copilot token exchange returned no token.");

        string? apiEndpoint = null;
        if (json.TryGetProperty("endpoints", out var endpoints) &&
            endpoints.TryGetProperty("api", out var apiEl))
        {
            apiEndpoint = apiEl.GetString();
        }

        long expiresAt;
        if (json.TryGetProperty("expires_at", out var expiresAtEl) && expiresAtEl.ValueKind == JsonValueKind.Number)
        {
            expiresAt = expiresAtEl.GetInt64();
        }
        else
        {
            var refreshIn = json.TryGetProperty("refresh_in", out var refreshInEl) ? refreshInEl.GetInt32() : 1500;
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshIn).ToUnixTimeSeconds();
        }

        return (token, expiresAt, apiEndpoint);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        string url, IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SharedClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
