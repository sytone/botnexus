using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Contributes the session-scoped <see cref="SkillTool"/> without requiring Gateway compile-time references.
/// </summary>
public sealed class SkillsToolContributor(ISkillUsageTelemetry? telemetry = null) : IAgentToolContributor
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
        // Plugin-shipped skills join at the global/shared tier (#2684).
        var pluginRootDir = Path.Combine(homeDir, ".botnexus", PluginSkillRootResolver.PluginRootDirectoryName);
        var config = ResolveExtensionConfig<SkillsConfig>(context.Descriptor, "botnexus-skills");

        // Seed the global skills directory with an example skill on first use.
        SkillsSeeder.EnsureGlobalSkillsSeed(globalSkillsDir);

        // Single implementation; the explicit aliases delegate to it and share its per-session
        // loaded-skill state so all three tool names stay perfectly consistent (#1831).
        var skillTool = new SkillTool(globalSkillsDir, agentSkillsDir, workspaceSkillsDir, config, telemetry, pluginRootDir);

        IReadOnlyList<IAgentTool> tools =
        [
            skillTool,
            SkillAliasTool.CreateListAlias(skillTool),
            SkillAliasTool.CreateViewAlias(skillTool)
        ];

        return Task.FromResult(new AgentToolContribution(tools));
    }

    /// <summary>
    /// Binds through the extension's single JSON seam so camelCase operator config binds (#3495).
    /// </summary>
    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
        => ExtensionConfigBinder.Bind<T>(descriptor, extensionId);
}
