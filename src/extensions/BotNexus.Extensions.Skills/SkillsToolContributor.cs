using System.IO.Abstractions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Contributes the session-scoped <see cref="SkillTool"/> without requiring Gateway compile-time references.
/// </summary>
public sealed class SkillsToolContributor : IAgentToolContributor
{
    private readonly ISkillUsageTelemetry? _telemetry;
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly string? _homeDirectory;

    /// <summary>Creates session tools using the existing user-profile skill and prompt roots.</summary>
    public SkillsToolContributor(ISkillUsageTelemetry? telemetry = null)
    {
        _telemetry = telemetry;
    }

    // Tests must exercise startup without seeding the actual user profile. This is not a
    // caller-selectable production home policy; the public constructor retains UserProfile.
    internal SkillsToolContributor(string homeDirectory, IFileSystem fileSystem, ISkillUsageTelemetry? telemetry = null)
        : this(telemetry)
    {
        _homeDirectory = homeDirectory;
        _fileSystem = fileSystem;
    }
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var homeDir = _homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSkillsDir = _fileSystem.Path.Combine(homeDir, ".botnexus", "skills");
        var agentSkillsDir = _fileSystem.Path.Combine(homeDir, ".botnexus", "agents", context.Descriptor.AgentId.Value, "skills");
        var workspaceSkillsDir = _fileSystem.Path.Combine(context.WorkspacePath, "skills");
        // Plugin-shipped skills join at the global/shared tier (#2684).
        var pluginRootDir = _fileSystem.Path.Combine(homeDir, ".botnexus", PluginSkillRootResolver.PluginRootDirectoryName);
        var config = SkillsConfigResolver.Resolve(context.Descriptor);

        // Seed extension-owned onboarding artifacts without overwriting operator content.
        SkillsSeeder.EnsureGlobalSkillsSeed(globalSkillsDir, _fileSystem);
        SkillsPromptSeeder.EnsureSharedPromptSeed(_fileSystem.Path.Combine(homeDir, ".botnexus", "prompts"), _fileSystem);

        // Single implementation; the explicit aliases delegate to it and share its per-session
        // loaded-skill state so all three tool names stay perfectly consistent (#1831).
        var skillTool = new SkillTool(globalSkillsDir, agentSkillsDir, workspaceSkillsDir, config, _telemetry, pluginRootDir);

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
