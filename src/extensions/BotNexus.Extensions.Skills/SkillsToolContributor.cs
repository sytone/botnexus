using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Contributes the session-scoped <see cref="SkillTool"/> without requiring Gateway compile-time references.
/// </summary>
public sealed class SkillsToolContributor : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSkillsDir = Path.Combine(homeDir, ".botnexus", "skills");
        var agentSkillsDir = Path.Combine(homeDir, ".botnexus", "agents", context.Descriptor.AgentId.Value, "skills");
        var workspaceSkillsDir = Path.Combine(context.WorkspacePath, "skills");
        var config = ResolveExtensionConfig<SkillsConfig>(context.Descriptor, "botnexus-skills");

        IReadOnlyList<IAgentTool> tools =
        [
            new SkillTool(globalSkillsDir, agentSkillsDir, workspaceSkillsDir, config)
        ];

        return Task.FromResult(new AgentToolContribution(tools));
    }

    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
    {
        if (descriptor.ExtensionConfig.TryGetValue(extensionId, out var element))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
