using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Shared plumbing for every GitHub agent tool: repository resolution, argument coercion, and
/// structured-result emission.
/// </summary>
/// <remarks>
/// <para><b>The credential is absent by construction.</b> This base exposes no token parameter, no
/// identity parameter, and no way for a derived tool to declare one that would reach GitHub - the
/// acting identity is resolved from <see cref="GitHubToolsConfig"/> at contribution time, so a tool
/// call cannot select or change it (#2627 AC2, AC3). The corresponding schema assertion in the test
/// suite enumerates every registered tool and fails if such a parameter ever appears.</para>
/// <para><b>Results are JSON objects, not command text.</b> <see cref="StructuredResult"/> is the
/// only success path; there is no code path that returns a formatted table or a raw body, which is
/// what removes the <c>--jq</c>/<c>ConvertFrom-Json</c> layer (#2627 AC4).</para>
/// </remarks>
public abstract class GitHubToolBase : IAgentTool
{
    /// <summary>The REST seam. Derived tools issue calls through this and never build their own.</summary>
    protected IGitHubApiClient Api { get; }

    /// <summary>Resolved per-agent configuration, including the default repository.</summary>
    protected GitHubToolsConfig Config { get; }

    /// <summary>Creates the tool over the REST seam and the resolved configuration.</summary>
    protected GitHubToolBase(IGitHubApiClient api, GitHubToolsConfig config)
    {
        Api = api ?? throw new ArgumentNullException(nameof(api));
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Label { get; }

    /// <inheritdoc />
    public abstract Tool Definition { get; }

    /// <inheritdoc />
    public abstract Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null);

    /// <inheritdoc />
    public virtual Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(arguments);

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal);
        Prepare(arguments, prepared);

        // Repository is resolved here, once, for every tool: either from the call or from the
        // agent's configured default. Doing it in the base is what makes "repo is optional at the
        // call site" uniformly true instead of per-tool folklore.
        prepared["repository"] = ResolveRepository(ReadString(arguments, "repository"));

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    /// <summary>Validates and copies tool-specific arguments into <paramref name="prepared"/>.</summary>
    /// <remarks>
    /// Derived tools MUST copy every argument they later read. The prepared dictionary is an
    /// allow-list, and an argument declared in the schema but never copied here is silently dropped
    /// at execution time - a defect that produces a plausible answer for the wrong input rather than
    /// an error.
    /// </remarks>
    protected abstract void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared);

    /// <summary>
    /// Resolves <c>owner/repo</c> from an explicit argument or the agent's configured default.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When neither is available, or the value is not in <c>owner/repo</c> form.
    /// </exception>
    protected string ResolveRepository(string? requested)
    {
        var candidate = string.IsNullOrWhiteSpace(requested) ? Config.DefaultRepository : requested;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException(
                "repository is required: pass 'owner/repo', or configure a default repository for this agent.");
        }

        var parts = candidate.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"repository must be in 'owner/repo' form, got '{candidate}'.");
        }

        return parts[0] + "/" + parts[1];
    }

    /// <summary>Serialises <paramref name="payload"/> as the tool's structured result.</summary>
    protected static AgentToolResult StructuredResult(object payload) =>
        new([new AgentToolContent(
            AgentToolContentType.Text,
            JsonSerializer.Serialize(payload, GitHubJson.ResultOptions))]);

    /// <summary>
    /// Projects a failed <see cref="GitHubApiResponse"/> into a structured error result.
    /// </summary>
    /// <remarks>
    /// Errors are objects too, not prose: an agent deciding whether to retry needs the status code
    /// as a field, and the previous shell-based path forced it to pattern-match stderr text.
    /// </remarks>
    protected static AgentToolResult ErrorResult(string tool, string repository, GitHubApiResponse response) =>
        StructuredResult(new
        {
            tool,
            repository,
            ok = false,
            status = response.StatusCode,
            error = response.ErrorMessage ?? $"GitHub returned status {response.StatusCode}.",
        });

    /// <summary>Reads an optional string argument, tolerating raw <see cref="JsonElement"/> values.</summary>
    protected static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s => string.IsNullOrWhiteSpace(s) ? null : s,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>Reads a required string argument.</summary>
    protected static string RequireString(IReadOnlyDictionary<string, object?> args, string key) =>
        ReadString(args, key) ?? throw new ArgumentException($"{key} is required.");

    /// <summary>Reads an optional integer argument, tolerating raw <see cref="JsonElement"/> values.</summary>
    protected static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var n) => n,
            JsonElement { ValueKind: JsonValueKind.String } el when int.TryParse(el.GetString(), out var n) => n,
            string s when int.TryParse(s, out var n) => n,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer."),
        };
    }

    /// <summary>Reads an optional boolean argument, tolerating raw <see cref="JsonElement"/> values.</summary>
    /// <remarks>
    /// Returns <c>null</c> for an absent argument rather than <c>false</c>, so a tool can tell "the
    /// caller did not say" from "the caller said no" and apply its own default explicitly.
    /// </remarks>
    protected static bool? ReadBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } el when bool.TryParse(el.GetString(), out var b) => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
    }

    /// <summary>Reads a required integer argument.</summary>
    protected static int RequireInt(IReadOnlyDictionary<string, object?> args, string key) =>
        ReadInt(args, key) ?? throw new ArgumentException($"{key} is required.");

    /// <summary>
    /// Clamps a caller-supplied page size into the configured bound.
    /// </summary>
    /// <remarks>
    /// Silent truncation is the failure mode this exists to avoid (#2627 AC on pagination): list
    /// tools report the effective page size and a continuation signal in their result, so a caller
    /// can always tell a complete page from a bounded one.
    /// </remarks>
    protected int ClampPageSize(int? requested) =>
        Math.Clamp(requested ?? Config.DefaultPageSize, 1, Config.MaxPageSize);

    /// <summary>Builds the tool schema element from a JSON schema literal.</summary>
    protected static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
