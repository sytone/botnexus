using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

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

    /// <summary>
    /// Creates the contributor over a factory that builds the REST client per agent, receiving the
    /// agent's resolved tool config and its agent id.
    /// </summary>
    /// <remarks>
    /// The agent id is a factory parameter rather than ambient state because the acting identity is
    /// derived from it (#2733). Passing it explicitly means the client for agent A is constructed
    /// against A's identity and cannot later be repointed at B's.
    /// </remarks>
    public GitHubToolsContributor(Func<GitHubToolsConfig, AgentId, IGitHubApiClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <summary>
    /// Creates the contributor over a factory that ignores the agent id. Retained for hosts and
    /// tests that bind a single client irrespective of the acting agent.
    /// </summary>
    public GitHubToolsContributor(Func<GitHubToolsConfig, IGitHubApiClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _clientFactory = (config, _) => clientFactory(config);
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var config = ResolveConfig(context.Descriptor);
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
            new GitHubApiTool(api, config),
        ];

        return Task.FromResult(new AgentToolContribution(tools));
    }

    /// <summary>
    /// Reads and normalises the agent's GitHub extension configuration, or returns <c>null</c> when
    /// the extension is not configured for this agent.
    /// </summary>
    internal static GitHubToolsConfig? ResolveConfig(AgentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!descriptor.ExtensionConfig.TryGetValue(GitHubToolsConfig.ExtensionId, out var element))
            return null;

        GitHubToolsConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<GitHubToolsConfig>(element.GetRawText(), GitHubJson.RequestOptions);
        }
        catch (JsonException)
        {
            // Malformed config yields no tools rather than tools with silently wrong bounds. A
            // page-size of 0 smuggled in through bad JSON would look like an empty repository.
            return null;
        }

        if (config is null)
            return null;

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
