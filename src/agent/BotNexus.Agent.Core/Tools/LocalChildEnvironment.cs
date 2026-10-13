using System.Diagnostics;

namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Clear-then-populate environment boundary for local shell/exec children, not MCP or remote execution.
/// Ambient variables survive only by exact OS-essential name or trusted operator pass-through name.
/// This does not sandbox filesystem access or credentials available to the same OS user.
/// </summary>
public static class LocalChildEnvironment
{
    // Exact names only. Keep this reviewed set in sync with the shell-execution documentation.
    private static readonly string[] WindowsEssentials =
        ["PATH", "PATHEXT", "SystemRoot", "WINDIR", "ComSpec", "TEMP", "TMP",
         "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA"];
    private static readonly string[] PosixEssentials =
        ["PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL", "LC_CTYPE", "TZ"];

    /// <summary>
    /// Replaces the inherited environment before process start. A null policy uses secure defaults;
    /// explicit caller values are merged last using <see cref="ProcessEnvironment.Merge"/>.
    /// No values are logged or retained in policy/configuration.
    /// </summary>
    public static void Apply(ProcessStartInfo startInfo, LocalChildEnvironmentPolicy? policy = null,
        IEnumerable<KeyValuePair<string, string>>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var ambient = startInfo.Environment.ToArray();
        Populate(startInfo.Environment, ambient, policy, overrides);
    }

    /// <summary>
    /// Clears target and copies only approved non-null ambient entries, then explicit overrides.
    /// Missing names are omitted. Windows compares names without case; POSIX compares ordinally.
    /// The optional platform argument allows deterministic testing of both rules on either host.
    /// Inputs are snapshotted before mutation so ambient and target may be the same dictionary.
    /// </summary>
    public static void Populate(IDictionary<string, string?> target,
        IEnumerable<KeyValuePair<string, string?>> ambient,
        LocalChildEnvironmentPolicy? policy = null,
        IEnumerable<KeyValuePair<string, string>>? overrides = null,
        bool? windows = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ambient);
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var comparer = isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var approved = new HashSet<string>(isWindows ? WindowsEssentials : PosixEssentials, comparer);
        approved.UnionWith((policy ?? LocalChildEnvironmentPolicy.Default).PassThroughNames);
        var inherited = ambient.Where(entry => entry.Value is not null && approved.Contains(entry.Key))
            .Select(entry => new KeyValuePair<string, string>(entry.Key,
                entry.Value ?? throw new InvalidOperationException("Filtered null environment value"))).ToArray();
        var explicitValues = overrides?.ToArray();
        target.Clear();
        ProcessEnvironment.Merge(target, inherited, comparer);
        if (explicitValues is not null)
            ProcessEnvironment.Merge(target, explicitValues, comparer);
    }
}
