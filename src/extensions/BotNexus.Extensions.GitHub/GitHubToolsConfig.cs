using System.Text.Json.Serialization;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Per-agent configuration for the GitHub tool surface.
/// </summary>
/// <remarks>
/// <para><b>Identity lives here, not in a tool argument (#2627 AC3).</b> The acting identity is a
/// property of the agent's configuration, resolved once when tools are contributed. Because the
/// credential is minted from that configuration rather than read from ambient <c>gh auth</c> state,
/// there is nothing for a tool call to switch: <c>gh auth switch</c> - a documented red line
/// violated 287 times because the ergonomic trap made it look like the remedy - becomes both
/// unnecessary and unreachable from a tool call.</para>
/// <para>Note what is NOT here: no token, no PEM contents, no environment variable name holding a
/// secret. The private key path and installation identity are <b>platform</b> configuration bound by
/// <see cref="GitHubCredentialOptions"/> in the host container, deliberately out of reach of
/// agent-visible config.</para>
/// </remarks>
public sealed class GitHubToolsConfig
{
    /// <summary>Extension id used to look this configuration up on an agent descriptor.</summary>
    public const string ExtensionId = "botnexus-github";

    /// <summary>
    /// Default <c>owner/repo</c> used when a tool call omits <c>repository</c>. Optional: without it
    /// every call must name a repository explicitly.
    /// </summary>
    [JsonPropertyName("defaultRepository")]
    public string? DefaultRepository { get; set; }

    /// <summary>
    /// Human-readable label for the acting identity, surfaced in tool results so an agent can see
    /// WHICH identity acted without being able to change it.
    /// </summary>
    [JsonPropertyName("identity")]
    public string? Identity { get; set; }

    /// <summary>Page size used when a list call does not specify one.</summary>
    [JsonPropertyName("defaultPageSize")]
    public int DefaultPageSize { get; set; } = 30;

    /// <summary>
    /// Hard upper bound on a page. A caller asking for more is clamped and TOLD it was clamped;
    /// the alternative - quietly returning fewer rows than requested - is the silent truncation the
    /// pagination criterion forbids.
    /// </summary>
    [JsonPropertyName("maxPageSize")]
    public int MaxPageSize { get; set; } = 100;
}
