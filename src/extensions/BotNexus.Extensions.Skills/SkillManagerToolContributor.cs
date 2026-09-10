using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Skills.Recording;
using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Contributes the skills WRITE path for a session: <see cref="SkillManagerTool"/> when
/// <see cref="SkillsConfig.AllowSkillCreation"/> is enabled, and <see cref="SkillRecordTool"/>
/// alongside it when <see cref="SkillsConfig.AllowSkillRecording"/> is too. The read tool
/// (<see cref="SkillTool"/>) is contributed separately by <see cref="SkillsToolContributor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the recorder is contributed HERE rather than by a contributor of its own.</strong>
/// It ends in a skill being written, through this very <see cref="SkillManagerTool"/> instance, so
/// the two tools must agree exactly on which directories a skill may land in. Resolving those paths
/// twice is how <c>shell</c> and the <c>exec</c> extension came to disagree about the working
/// directory for months (#2416), and the same divergence here would mean a draft reviewed as
/// installing to one place installing to another.
/// </para>
/// <para>
/// It also keeps the home-root derivation on this one file. The fence at
/// <c>HomeRootSingleResolutionArchitectureTests</c> treats its allowlist as a debt ledger that must
/// not grow, and a separate recorder contributor would have had to compute the same
/// <c>~/.botnexus</c> paths in a new file — a second unguarded derivation, for no benefit.
/// </para>
/// </remarks>
public sealed class SkillManagerToolContributor(
    ISkillUsageTelemetry? telemetry = null,
    ISessionStore? sessions = null) : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveExtensionConfig<SkillsConfig>(context.Descriptor, "botnexus-skills")
                     ?? new SkillsConfig();

        // Only contribute the write tools when creation is explicitly enabled
        if (!config.AllowSkillCreation)
            return Task.FromResult(new AgentToolContribution(Array.Empty<IAgentTool>()));

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentDir = Path.Combine(homeDir, ".botnexus", "agents", context.Descriptor.AgentId.Value);
        var agentSkillsDir = Path.Combine(agentDir, "skills");
        var workspaceSkillsDir = Path.Combine(context.WorkspacePath, "skills");
        // Shared (all-agent) skills live at ~/.botnexus/skills. Writes there require AllowSharedSkillManagement.
        var globalSkillsDir = Path.Combine(homeDir, ".botnexus", "skills");

        var writer = new SkillManagerTool(agentSkillsDir, workspaceSkillsDir, globalSkillsDir, config, fileSystem: null, telemetry: telemetry, createdBy: context.Descriptor.AgentId.Value);

        var tools = new List<IAgentTool> { writer };

        if (config.AllowSkillRecording)
            tools.Add(BuildRecorder(context, config, writer, agentDir, agentSkillsDir, workspaceSkillsDir, globalSkillsDir));

        return Task.FromResult(new AgentToolContribution(tools));
    }

    /// <summary>
    /// Builds the recorder around the same writer, the same directories, and this session's trace.
    /// </summary>
    /// <remarks>
    /// <see cref="ISessionStore"/> is optional for the same reason the telemetry sink is: a
    /// contributor the container cannot activate takes the extension's ENTIRE tool contribution down
    /// with it (see <c>AssemblyLoadContextExtensionLoader</c>). When it is absent the recorder is
    /// still contributed and every action refuses, naming the missing wiring. That is deliberate —
    /// a recorder that silently proposed skills with nothing to check them against would be the
    /// fifth "green tests, dead wiring" defect on this codebase, not the first.
    /// </remarks>
    private SkillRecordTool BuildRecorder(
        AgentToolContributionContext context,
        SkillsConfig config,
        SkillManagerTool writer,
        string agentDir,
        string agentSkillsDir,
        string workspaceSkillsDir,
        string globalSkillsDir)
    {
        // Beside the agent's skills directory, never inside it. The store's constructor rejects a
        // root within any discovery root, so the separation is checked rather than assumed.
        var drafts = new SkillDraftStore(
            SkillDraftStore.ResolveRoot(agentDir),
            [agentSkillsDir, workspaceSkillsDir, globalSkillsDir]);

        ISessionTraceSource? trace = sessions is null
            ? null
            : new SessionStoreTraceSource(sessions, context.ExecutionContext.SessionId);

        return new SkillRecordTool(writer, drafts, trace, config, context.Descriptor.AgentId.Value);
    }

    /// <summary>
    /// Binds through the extension's single JSON seam so camelCase operator config binds (#3495).
    /// </summary>
    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
        => ExtensionConfigBinder.Bind<T>(descriptor, extensionId);
}
