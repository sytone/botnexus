using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Aggregate-suite check that reconciles the persistent agent folders under the BotNexus home
/// (<c>&lt;home&gt;/agents/&lt;id&gt;</c>) against the agents declared in <c>config.json</c>
/// (issue #2041 - persistent agent-folder reconciliation). It surfaces two drifts an operator should
/// know about: a configured agent that has no workspace folder yet (created lazily on first
/// activation - informational warning), and an on-disk agent folder with no matching config entry
/// (an orphan left behind by a removed agent - warning). It is read-only: it never creates or deletes
/// folders, mirroring the safe-seam philosophy of the sub-agent reaper.
/// </summary>
internal sealed class PersistentAgentFolderCheck : IDoctorCheck
{
    public string Id => "agent-folders";
    public string Title => "Persistent agent folders";

    public async Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
    {
        if (!File.Exists(context.ConfigPath))
        {
            return DoctorCheckResult.Error(
                "config.json not found",
                $"Expected at {context.ConfigPath}. Run 'botnexus init' first.");
        }

        PlatformConfig config;
        try
        {
            config = await PlatformConfigLoader.LoadAsync(context.ConfigPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error("config.json could not be loaded", ex.Message);
        }

        var configuredIds = new HashSet<string>(
            config.Agents?.Keys ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Resolve the agents root the same way the gateway does, honoring the active --target home.
        var home = new BotNexusHome(context.HomePath);
        var agentsRoot = home.AgentsPath;

        var onDiskFolders = Directory.Exists(agentsRoot)
            ? Directory.EnumerateDirectories(agentsRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList()
            : [];

        var onDiskSet = new HashSet<string>(onDiskFolders, StringComparer.OrdinalIgnoreCase);

        // Configured agents whose workspace folder has not been created yet (lazy first-activation).
        var missingFolders = configuredIds
            .Where(id => !onDiskSet.Contains(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // On-disk folders with no matching config entry - orphans from a removed agent.
        var orphanFolders = onDiskFolders
            .Where(name => !configuredIds.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingFolders.Count == 0 && orphanFolders.Count == 0)
        {
            return DoctorCheckResult.Healthy(
                $"{configuredIds.Count} configured agent(s) reconciled with on-disk folders");
        }

        var details = new List<string>();
        foreach (var id in missingFolders)
            details.Add($"  [warn] configured agent '{id}' has no workspace folder yet (created on first activation)");
        foreach (var name in orphanFolders)
            details.Add($"  [warn] orphan folder '{name}' has no matching agent in config.json");

        var summary =
            $"{missingFolders.Count} agent(s) without a folder, {orphanFolders.Count} orphan folder(s)";
        return new DoctorCheckResult(DoctorOutcome.Warning, summary, details);
    }
}
