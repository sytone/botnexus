using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// HTTPS implementation of <see cref="IGitHubApiClient"/> that attaches the platform-owned
/// credential to every outbound request.
/// </summary>
/// <remarks>
/// <para><b>Why REST and not GraphQL.</b> Comment writes in particular must go through the REST
/// endpoint: the GraphQL <c>addComment</c> mutation fails under an Enterprise Managed User account,
/// and rediscovering that per agent is exactly the cost this extension exists to remove (#2627 AC5).
/// Every call in this client is REST, so the workaround is encoded once in the platform.</para>
/// <para><b>Why failures carry a status and GitHub's own message, never a raw body dump.</b> A
/// verbatim body echo is an uncontrolled channel that can re-emit request material; the projection
/// keeps errors actionable without becoming a leak surface (#2627 AC9).</para>
/// </remarks>
public sealed class HttpGitHubApiClient : IGitHubApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IGitHubCredentialProvider _credentials;
    private readonly Uri _baseAddress;

    /// <summary>Creates a client over an <see cref="HttpClient"/>, the credential provider, and options.</summary>
    public HttpGitHubApiClient(
        HttpClient httpClient,
        IGitHubCredentialProvider credentials,
        GitHubCredentialOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        ArgumentNullException.ThrowIfNull(options);
        _baseAddress = new Uri(options.ApiBaseAddress, UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<GitHubApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var request = new HttpRequestMessage(method, new Uri(_baseAddress, path.TrimStart('/')));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("BotNexus");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: GitHubJson.RequestOptions);
        }

        // The single point at which the credential is applied. No tool, and therefore no agent
        // argument, participates in this step (#2627 AC2).
        await _credentials.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonElement? parsed = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                parsed = JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException)
            {
                // A non-JSON body (an HTML error page, for instance) is reported as "no body"
                // rather than being echoed: the tool contract is structured data, and echoing an
                // unparsed payload is precisely the text-to-re-parse problem being retired.
                parsed = null;
            }
        }

        string? errorMessage = null;
        if (!response.IsSuccessStatusCode)
        {
            errorMessage = parsed is { ValueKind: JsonValueKind.Object } obj
                && obj.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : $"GitHub returned status {(int)response.StatusCode}.";
        }

        return new GitHubApiResponse((int)response.StatusCode, response.IsSuccessStatusCode, parsed, errorMessage);
    }
}
