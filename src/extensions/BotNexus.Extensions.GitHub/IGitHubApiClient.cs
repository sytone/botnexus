using System.Text.Json;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// The outcome of one GitHub REST call: a status code plus an already-parsed JSON body.
/// </summary>
/// <remarks>
/// This type is the reason the tools in this extension do not return command text (#2627 AC4).
/// Shelling out to <c>gh</c> produced a string that every caller re-parsed with <c>--jq</c> or
/// <c>ConvertFrom-Json</c> (2,951 measured occurrences); here the parse happens once, in the
/// platform, and the tool projects fields directly.
/// <para>Deliberately carries NO credential material: the token is attached to the outbound request
/// by <see cref="IGitHubCredentialProvider"/> and is never copied into a response object.</para>
/// </remarks>
/// <param name="StatusCode">HTTP status returned by GitHub.</param>
/// <param name="IsSuccess">True when GitHub returned a 2xx status.</param>
/// <param name="Body">Parsed JSON body, or <c>null</c> when the response carried no JSON.</param>
/// <param name="ErrorMessage">GitHub's <c>message</c> field on a failure, when present.</param>
public sealed record GitHubApiResponse(
    int StatusCode,
    bool IsSuccess,
    JsonElement? Body,
    string? ErrorMessage = null);

/// <summary>
/// Minimal REST seam every GitHub tool calls through.
/// </summary>
/// <remarks>
/// Kept deliberately narrow (one method) so a test double is a few lines, and so there is exactly
/// one place where the platform credential is attached. Tools never construct requests themselves,
/// which is what makes "no tool call requires an agent to mint, set, or pass a token" (#2627 AC2) a
/// structural property rather than a convention.
/// </remarks>
public interface IGitHubApiClient
{
    /// <summary>Issues an authenticated REST call against the configured GitHub API host.</summary>
    /// <param name="method">HTTP verb.</param>
    /// <param name="path">API path relative to the API base address, e.g. <c>repos/o/r/issues/1</c>.</param>
    /// <param name="body">Optional request body, serialised as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GitHubApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken cancellationToken = default);
}
