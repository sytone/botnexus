using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Hook handler that discovers and resolves skills, then injects
/// the formatted skills prompt into the system context via the
/// <see cref="BeforePromptBuildEvent"/> pipeline.
/// </summary>
public sealed class SkillPromptHookHandler
    : IHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>
{
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IAgentRegistry _agentRegistry;

    /// <summary>Skills load after other hooks.</summary>
    public int Priority => 100;

    public SkillPromptHookHandler(
        IAgentWorkspaceManager workspaceManager,
        IAgentRegistry agentRegistry)
    {
        _workspaceManager = workspaceManager;
        _agentRegistry = agentRegistry;
    }

    public Task<BeforePromptBuildResult?> HandleAsync(
        BeforePromptBuildEvent hookEvent,
        CancellationToken ct = default)
    {
        var descriptor = _agentRegistry.Get(hookEvent.AgentId);
        if (descriptor is null)
            return Task.FromResult<BeforePromptBuildResult?>(null);

        var workspacePath = _workspaceManager.GetWorkspacePath(hookEvent.AgentId);

        var botnexusHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus");

        var globalSkillsDir = Path.Combine(botnexusHome, "skills");
        var agentSkillsDir = Path.Combine(botnexusHome, "agents", hookEvent.AgentId, "skills");
        var workspaceSkillsDir = Path.Combine(workspacePath, "skills");

        var allSkills = SkillDiscovery.Discover(globalSkillsDir, agentSkillsDir, workspaceSkillsDir);
        if (allSkills.Count == 0)
            return Task.FromResult<BeforePromptBuildResult?>(null);

        var resolution = SkillResolver.Resolve(allSkills, descriptor.Skills);
        if (resolution.Loaded.Count == 0 && resolution.Available.Count == 0)
            return Task.FromResult<BeforePromptBuildResult?>(null);

        var prompt = SkillPromptBuilder.Build(resolution.Loaded, resolution.Available);
        if (string.IsNullOrWhiteSpace(prompt))
            return Task.FromResult<BeforePromptBuildResult?>(null);

        return Task.FromResult<BeforePromptBuildResult?>(new BeforePromptBuildResult
        {
            AppendSystemContext = prompt
        });
    }
}
