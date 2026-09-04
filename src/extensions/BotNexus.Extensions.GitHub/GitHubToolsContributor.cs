using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Contributes the GitHub agent tools for an agent that has the extension configured.
/// </summary>
/// <remarks>
/// <para><b>The identity decision happens here, once, and is not an argument (#2627 AC3).</b> The
/// acting identity is read from the agent descriptor's extension configuration at contribution time.
/// Nothing downstream can change it: the tools receive an already-resolved config object and the
/// credential provider is a host singleton bound to platform configuration. There is consequently no
/// tool call that can mutate ambient CLI account state, which is what turns the <c>gh auth switch</c>
/// prohibition from a convention into a mechanism.</para>
/// <para>An agent without <c>botnexus-github</c> configuration gets NO tools rather than tools that
/// fail at call time - a tool the model can see but never use is a turn tax, not a capability.</para>
/// </remarks>
public sealed class GitHubToolsContributor : IAgentToolContributor
{
    private readonly Func<GitHubToolsConfig, AgentId, IGitHubApiClient> _clientFactory;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the contributor over a factory that builds the REST client per agent, receiving the
    /// agent's resolved tool config and its agent id.
    /// </summary>
    /// <remarks>
    /// The agent id is a factory parameter rather than ambient state because the acting identity is
    /// derived from it (#2733). Passing it explicitly means the client for agent A is constructed
    /// against A's identity and cannot later be repointed at B's.
    /// </remarks>
    public GitHubToolsContributor(
        Func<GitHubToolsConfig, AgentId, IGitHubApiClient> clientFactory,
        ILogger? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
    }

    /// <summary>
    /// Creates the contributor over a factory that ignores the agent id. Retained for hosts and
    /// tests that bind a single client irrespective of the acting agent.
    /// </summary>
    public GitHubToolsContributor(
        Func<GitHubToolsConfig, IGitHubApiClient> clientFactory,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _clientFactory = (config, _) => clientFactory(config);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var config = ResolveConfig(context.Descriptor, _logger);
        if (config is null)
            return Task.FromResult(new AgentToolContribution([]));

        var api = _clientFactory(config, context.Descriptor.AgentId);
        IReadOnlyList<IAgentTool> tools =
        [
            new GitHubIssueGetTool(api, config),
            new GitHubIssueListTool(api, config),
            new GitHubIssueCommentTool(api, config),
            new GitHubPullRequestGetTool(api, config),
            new GitHubPullRequestListTool(api, config),
            new GitHubPullRequestChecksTool(api, config),
            new GitHubPullRequestDiffTool(api, config),
            new GitHubWorkflowRunsTool(api, config),
            new GitHubApiTool(api, config),
        ];

        return Task.FromResult(new AgentToolContribution(tools));
    }

    /// <summary>
    /// Reads and normalises the agent's GitHub extension configuration, or returns <c>null</c> when
    /// the extension is not configured for this agent.
    /// </summary>
    /// <param name="descriptor">The agent whose extension configuration is being read.</param>
    /// <param name="logger">
    /// Optional logger used to distinguish the two <c>null</c> outcomes (#3750 AC4). Both are
    /// fail-closed and both contribute zero tools, but only one of them is a mistake.
    /// </param>
    /// <remarks>
    /// <para><b>Two different silences used to look identical (#3750).</b> An agent with no
    /// <c>botnexus-github</c> key and an agent whose key holds a typo both landed on
    /// <c>return null</c> with no trace, so an operator who configured the extension and saw no
    /// tools had no way to tell a malformed value from an absent one. The merged extension went
    /// unused for two weeks for exactly the first reason while looking, from the outside, like the
    /// second.</para>
    /// <para>The unconfigured case logs at Debug because it is the normal state of most agents; the
    /// configured-but-malformed case logs at <b>Warning</b> because someone intended it to work.
    /// Only the extension id and agent id are logged, never the configured value - extension config
    /// bags carry API keys elsewhere in this file and a diagnostic must not become a leak.</para>
    /// </remarks>
    internal static GitHubToolsConfig? ResolveConfig(AgentDescriptor descriptor, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!descriptor.ExtensionConfig.TryGetValue(GitHubToolsConfig.ExtensionId, out var element))
        {
            logger?.LogDebug(
                "GitHub tools not contributed for agent '{AgentId}': no '{ExtensionId}' entry in extensionConfig. " +
                "Add one to enable the github_* tools; see docs/extensions/github.md.",
                descriptor.AgentId.Value,
                GitHubToolsConfig.ExtensionId);
            return null;
        }

        GitHubToolsConfig? config = ExtensionConfigBinder.Bind<GitHubToolsConfig>(element);

        // Malformed config yields no tools rather than tools with silently wrong bounds. A
        // page-size of 0 smuggled in through bad JSON would look like an empty repository.
        // Bind returns null for malformed input, so that case and an explicit JSON null converge
        // on the same fail-closed path.

        if (config is null)
        {
            logger?.LogWarning(
                "GitHub tools not contributed for agent '{AgentId}': the '{ExtensionId}' extensionConfig entry is " +
                "present but could not be bound (found JSON {ValueKind}; a JSON object is required). No github_* " +
                "tools were contributed. Correct the entry; see docs/extensions/github.md.",
                descriptor.AgentId.Value,
                GitHubToolsConfig.ExtensionId,
                element.ValueKind);
            return null;
        }

        // Defensive normalisation: a configured 0 or negative bound would make ClampPageSize throw
        // deep inside a tool call, where the error reads as a GitHub failure rather than a config one.
        if (config.MaxPageSize < 1)
            config.MaxPageSize = 100;
        if (config.DefaultPageSize < 1)
            config.DefaultPageSize = Math.Min(30, config.MaxPageSize);
        if (config.DefaultPageSize > config.MaxPageSize)
            config.DefaultPageSize = config.MaxPageSize;

        return config;
    }
}
