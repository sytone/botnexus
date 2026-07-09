using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Contributes the session-scoped <see cref="SkillManagerTool"/> when
/// <see cref="SkillsConfig.AllowSkillCreation"/> is enabled.
/// The write tool is contributed alongside the existing <see cref="SkillTool"/>
/// (contributed by <see cref="SkillsToolContributor"/>).
/// </summary>
public sealed class SkillManagerToolContributor(ISkillUsageTelemetry? telemetry = null) : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveExtensionConfig<SkillsConfig>(context.Descriptor, "botnexus-skills")
                     ?? new SkillsConfig();

        // Only contribute the write tool when creation is explicitly enabled
        if (!config.AllowSkillCreation)
            return Task.FromResult(new AgentToolContribution(Array.Empty<IAgentTool>()));

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentSkillsDir = Path.Combine(homeDir, ".botnexus", "agents", context.Descriptor.AgentId.Value, "skills");
        var workspaceSkillsDir = Path.Combine(context.WorkspacePath, "skills");
        // Shared (all-agent) skills live at ~/.botnexus/skills. Writes there require AllowSharedSkillManagement.
        var globalSkillsDir = Path.Combine(homeDir, ".botnexus", "skills");

        IReadOnlyList<IAgentTool> tools =
        [
            new SkillManagerTool(agentSkillsDir, workspaceSkillsDir, globalSkillsDir, config, fileSystem: null, telemetry: telemetry, createdBy: context.Descriptor.AgentId.Value)
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
