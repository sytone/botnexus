using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// The escape hatch: an authenticated call to an arbitrary GitHub REST path (#2627 AC7).
/// </summary>
/// <remarks>
/// <para>Its purpose is to make the modelled tool surface safe to keep SMALL. Without it, the first
/// unmodelled endpoint sends an agent straight back to shelling out with a hand-minted token - which
/// would restore every cost this extension removes, for the sake of one missing operation.</para>
/// <para>It still takes no credential argument: the platform attaches the token, so even the escape
/// hatch cannot re-introduce agent-visible credentials. Writes are restricted to the configured
/// identity for the same reason.</para>
/// </remarks>
public sealed class GitHubApiTool : GitHubToolBase
{
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PATCH", "PUT", "DELETE" };

    /// <summary>Creates the tool.</summary>
    public GitHubApiTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_api";

    /// <inheritdoc />
    public override string Label => "GitHub API";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "Call any GitHub REST endpoint with the platform-managed credential. Use when no dedicated github_* tool covers the operation, so an unmodelled endpoint never requires shelling out to gh.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "method": { "type": "string", "enum": ["GET", "POST", "PATCH", "PUT", "DELETE"], "description": "HTTP method. Default: GET." },
                "path": { "type": "string", "description": "REST path relative to the API root, e.g. 'repos/owner/repo/labels'. Do not include a host." },
                "body": { "type": "object", "description": "Optional JSON request body for write methods." }
              },
              "required": ["path"]
            }
            """));

    /// <inheritdoc />
    public override Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(arguments);

        // This tool overrides the base rather than extending it: an arbitrary path carries its own
        // repository, so requiring the base's owner/repo resolution would reject perfectly valid
        // non-repository endpoints such as 'user' or 'rate_limit'.
        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal);
        Prepare(arguments, prepared);
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    /// <inheritdoc />
    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var method = ReadString(arguments, "method") ?? "GET";
        if (!AllowedMethods.Contains(method))
            throw new ArgumentException($"method must be one of: {string.Join(", ", AllowedMethods)}.");

        var path = RequireString(arguments, "path");
        if (path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("path must be relative to the GitHub API root, not an absolute URL.");

        prepared["method"] = method.ToUpperInvariant();
        prepared["path"] = path;
        prepared["body"] = arguments.TryGetValue("body", out var body) ? body : null;
    }

    /// <inheritdoc />
    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var method = new HttpMethod((string)arguments["method"]!);
        var path = (string)arguments["path"]!;
        var body = arguments["body"];

        var response = await Api.SendAsync(method, path, body, cancellationToken).ConfigureAwait(false);

        return StructuredResult(new
        {
            tool = Name,
            ok = response.IsSuccess,
            status = response.StatusCode,
            path,
            method = method.Method,
            error = response.ErrorMessage,
            // The parsed body is passed through as JSON, not as text: even the escape hatch honours
            // the structured-result contract, so a caller reads fields rather than re-parsing.
            data = response.Body is { ValueKind: not JsonValueKind.Undefined } element ? element : (JsonElement?)null,
        });
    }
}
