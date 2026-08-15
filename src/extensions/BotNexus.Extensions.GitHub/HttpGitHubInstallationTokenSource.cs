using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Mints GitHub App installation tokens over HTTPS: signs a short-lived App JWT with the configured
/// PEM private key, then exchanges it at <c>POST /app/installations/{id}/access_tokens</c>.
/// </summary>
/// <remarks>
/// The JWT is assembled by hand with <see cref="RSA"/> rather than pulling in a JWT package, so this
/// extension ships no additional managed dependency closure into its isolated load context. Failures
/// are surfaced as <see cref="GitHubCredentialException"/> carrying the status code only — response
/// bodies from the token endpoint can echo credential material and are deliberately not included.
/// </remarks>
public sealed class HttpGitHubInstallationTokenSource : IGitHubInstallationTokenSource
{
    private readonly HttpClient _httpClient;
    private readonly GitHubCredentialOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a source over an <see cref="HttpClient"/>, options, and a clock.</summary>
    public HttpGitHubInstallationTokenSource(
        HttpClient httpClient,
        GitHubCredentialOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GitHubInstallationToken> MintAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AppId))
            throw new GitHubCredentialException("GitHub App id is not configured.");
        if (string.IsNullOrWhiteSpace(_options.InstallationId))
            throw new GitHubCredentialException("GitHub App installation id is not configured.");
        if (string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
            throw new GitHubCredentialException("GitHub App private key path is not configured.");

        var pem = await File.ReadAllTextAsync(_options.PrivateKeyPath!, cancellationToken).ConfigureAwait(false);
        var jwt = CreateAppJwt(_options.AppId!, pem, _timeProvider.GetUtcNow());

        var requestUri = new Uri(
            new Uri(_options.ApiBaseAddress, UriKind.Absolute),
            $"app/installations/{_options.InstallationId}/access_tokens");

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("BotNexus");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: the body of a token response can contain credential material.
            throw new GitHubCredentialException(
                $"GitHub rejected the installation token request with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content
            .ReadFromJsonAsync<AccessTokenResponse>(cancellationToken)
            .ConfigureAwait(false);

        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
            throw new GitHubCredentialException("GitHub returned no installation token.");

        return new GitHubInstallationToken(payload.Token!, payload.ExpiresAt);
    }

    /// <summary>
    /// Builds the RS256-signed App JWT GitHub requires to exchange for an installation token.
    /// </summary>
    internal static string CreateAppJwt(string appId, string pem, DateTimeOffset now)
    {
        var issuedAt = now.AddSeconds(-60).ToUnixTimeSeconds();
        var expiresAt = now.AddMinutes(9).ToUnixTimeSeconds();

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = Base64UrlEncode(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iat"] = issuedAt,
                ["exp"] = expiresAt,
                ["iss"] = appId,
            })));

        var signingInput = header + "." + claims;

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return signingInput + "." + Base64UrlEncode(signature);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record AccessTokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; init; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; init; }
    }
}
