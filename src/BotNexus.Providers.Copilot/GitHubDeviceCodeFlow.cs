using System.Net.Http.Headers;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Bus;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Copilot;

public class GitHubDeviceCodeFlow
{
    private const string DeviceCodeEndpoint = "https://github.com/login/device/code";
    private const string AccessTokenEndpoint = "https://github.com/login/oauth/access_token";
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubDeviceCodeFlow> _logger;
    private readonly IActivityStream? _activityStream;
    private readonly SystemMessageStore? _messageStore;

    public GitHubDeviceCodeFlow(ILogger<GitHubDeviceCodeFlow>? logger = null)
        : this(new HttpClient(), logger, null, null)
    {
    }

    public GitHubDeviceCodeFlow(
        HttpClient httpClient, 
        ILogger<GitHubDeviceCodeFlow>? logger = null, 
        IActivityStream? activityStream = null,
        SystemMessageStore? messageStore = null)
    {
        _httpClient = httpClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubDeviceCodeFlow>.Instance;
        _activityStream = activityStream;
        _messageStore = messageStore;
    }

    public async Task<OAuthToken> AcquireAccessTokenAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var deviceCode = await RequestDeviceCodeAsync(clientId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Go to {VerificationUri} and enter code: {UserCode}", deviceCode.VerificationUri, deviceCode.UserCode);

        // Broadcast device auth message to connected clients
        if (_activityStream is not null)
        {
            await _activityStream.PublishSystemMessageAsync(new SystemMessage(
                Type: "device_auth",
                Title: "GitHub Copilot Authentication Required",
                Content: $"Visit {deviceCode.VerificationUri} and enter code: {deviceCode.UserCode}",
                Data: new Dictionary<string, string>
                {
                    ["provider"] = "copilot",
                    ["verification_uri"] = deviceCode.VerificationUri,
                    ["user_code"] = deviceCode.UserCode,
                    ["expires_in"] = deviceCode.ExpiresIn.ToString()
                }), cancellationToken).ConfigureAwait(false);
        }

        return await PollForAccessTokenAsync(clientId, deviceCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken)
    {
        using var response = await PostFormAsync(DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "read:user"
        }, cancellationToken).ConfigureAwait(false);

        var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new DeviceCodeResponse(
            payload.GetProperty("device_code").GetString() ?? string.Empty,
            payload.GetProperty("user_code").GetString() ?? string.Empty,
            payload.GetProperty("verification_uri").GetString() ?? string.Empty,
            payload.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            payload.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 900);
    }

    private async Task<OAuthToken> PollForAccessTokenAsync(
        string clientId,
        DeviceCodeResponse deviceCode,
        CancellationToken cancellationToken)
    {
        var intervalSeconds = Math.Max(deviceCode.Interval, 1);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);

            using var response = await PostFormAsync(AccessTokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, cancellationToken).ConfigureAwait(false);

            var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (payload.TryGetProperty("access_token", out var accessTokenElement))
            {
                var accessToken = accessTokenElement.GetString() ?? string.Empty;
                var refreshToken = payload.TryGetProperty("refresh_token", out var refreshTokenElement)
                    ? refreshTokenElement.GetString()
                    : null;
                var expiresInSeconds = payload.TryGetProperty("expires_in", out var expiresInElement)
                    ? expiresInElement.GetInt32()
                    : 8 * 60 * 60;

                // Broadcast auth success message to connected clients
                if (_activityStream is not null)
                {
                    await _activityStream.PublishSystemMessageAsync(new SystemMessage(
                        Type: "auth_success",
                        Title: "GitHub Copilot Authentication Successful",
                        Content: "GitHub Copilot authentication completed successfully.",
                        Data: new Dictionary<string, string>
                        {
                            ["provider"] = "copilot"
                        }), cancellationToken).ConfigureAwait(false);
                }

                return new OAuthToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds), refreshToken);
            }

            var error = payload.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;

            if (error is "authorization_pending")
                continue;

            if (error is "slow_down")
            {
                intervalSeconds += 5;
                continue;
            }

            if (error is "access_denied")
                throw new InvalidOperationException("GitHub device authorization was denied.");

            if (error is "expired_token")
                throw new TimeoutException("GitHub device code expired before authorization completed.");

            var description = payload.TryGetProperty("error_description", out var descElement)
                ? descElement.GetString()
                : "Unknown OAuth error.";
            throw new InvalidOperationException($"GitHub OAuth failed: {error ?? "unknown"} ({description})");
        }

        throw new TimeoutException("Timed out while waiting for GitHub device code authorization.");
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record DeviceCodeResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int Interval,
        int ExpiresIn);
}
