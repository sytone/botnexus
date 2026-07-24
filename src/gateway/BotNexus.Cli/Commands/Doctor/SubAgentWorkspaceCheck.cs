using System.IO.Abstractions;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Aggregate-suite check that reconciles the temp sub-agent workspace directories against the
/// persisted <c>sub_agent_sessions</c> status rows using the existing #1942 reaper semantics
/// (<see cref="SubAgentWorkspaceReaper"/>). It reports how many stale (terminal or orphaned)
/// workspaces are reclaimable without ever touching a still-running sub-agent's live files
/// (issue #2041). It is read-only - pruning stays the job of <c>botnexus subagent workspace prune</c>;
/// here we only surface the reclaimable count so an operator knows when to run it.
/// </summary>
internal sealed class SubAgentWorkspaceCheck : IDoctorCheck
{
    private readonly IFileSystem _fileSystem;

    public SubAgentWorkspaceCheck()
        : this(new FileSystem())
    {
    }

    // Test seam: inject a MockFileSystem so the check can be exercised without touching the real
    // temp directory.
    internal SubAgentWorkspaceCheck(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public string Id => "subagent-workspaces";
    public string Title => "Sub-agent workspace reconciliation";

    public Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
    {
        var sessionsDbPath = Path.Combine(context.HomePath, "sessions.db");
        var workspaceRoot = _fileSystem.Path.Combine(
            _fileSystem.Path.GetTempPath(),
            SubAgentCommand.SubAgentWorkspaceDirectoryName);

        var reaper = new SubAgentWorkspaceReaper(_fileSystem);
        var statuses = SubAgentCommand.LoadStatusesByAgentDirectory(sessionsDbPath);
        var plan = reaper.BuildPlan(workspaceRoot, statuses);

        if (plan.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Healthy(
                "no sub-agent workspaces on disk - nothing to reconcile"));
        }

        var prunable = plan.Count(entry => entry.IsPrunable);
        var running = plan.Count - prunable;

        if (prunable == 0)
        {
            return Task.FromResult(DoctorCheckResult.Healthy(
                $"{plan.Count} workspace(s), all in-use ({running} running) - nothing to reclaim"));
        }

        var details = new List<string>
        {
            "Run 'botnexus subagent workspace prune' to reclaim stale workspaces."
        };
        if (context.Verbose)
        {
            details.AddRange(plan
                .Where(entry => entry.IsPrunable)
                .Select(entry => $"  - {entry.AgentDirectoryName} ({entry.Disposition})"));
        }

        return Task.FromResult(new DoctorCheckResult(
            DoctorOutcome.Warning,
            $"{prunable} stale workspace(s) reclaimable, {running} running (retained)",
            details));
    }
}
