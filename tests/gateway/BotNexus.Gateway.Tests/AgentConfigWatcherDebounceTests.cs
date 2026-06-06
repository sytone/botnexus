using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the debounced agent config file watcher (#937).
/// Uses the internal static IsAgentDefinitionFile method directly to avoid
/// needing MockFileSystem to support FileSystemWatcher.
///
/// Paths are built with Path.Combine so tests pass on both Windows and Linux CI.
/// </summary>
public sealed class AgentConfigWatcherDebounceTests
{
    // Cross-platform root: e.g. /tmp/agents on Linux, C:\agents on Windows
    private static readonly string AgentsRoot = Path.Combine(Path.GetTempPath(), "agents");

    // Shorthand helper
    private static bool IsDefinitionFile(string fullPath)
        => FileAgentConfigurationSource.FileConfigurationWatcher.IsAgentDefinitionFile(AgentsRoot, fullPath);

    // ── test 1: workspace file write does NOT trigger reload ─────────────────

    [Fact]
    public void IsAgentDefinitionFile_WorkspaceSubdirectoryFile_ReturnsFalse()
    {
        var path = Path.Combine(AgentsRoot, "nova", "workspace", "memory", "2026-01-01.md");
        IsDefinitionFile(path).ShouldBeFalse();
    }

    // ── test 2: config.json at depth 1 triggers reload ───────────────────────

    [Fact]
    public void IsAgentDefinitionFile_AgentConfigJson_ReturnsTrue()
    {
        var path = Path.Combine(AgentsRoot, "nova", "config.json");
        IsDefinitionFile(path).ShouldBeTrue();
    }

    // ── test 3: SOUL.md at depth 1 triggers reload ───────────────────────────

    [Fact]
    public void IsAgentDefinitionFile_AgentRootMarkdown_ReturnsTrue()
    {
        var path = Path.Combine(AgentsRoot, "nova", "SOUL.md");
        IsDefinitionFile(path).ShouldBeTrue();
    }

    // ── test 4: .txt or .log file does NOT trigger reload ────────────────────

    [Fact]
    public void IsAgentDefinitionFile_TxtFile_ReturnsFalse()
    {
        var path = Path.Combine(AgentsRoot, "nova", "debug.log");
        IsDefinitionFile(path).ShouldBeFalse();
    }

    // ── test 5: tmp file in workspace subdirectory does NOT trigger reload ───

    [Fact]
    public void IsAgentDefinitionFile_TmpFileInWorkspace_ReturnsFalse()
    {
        var path = Path.Combine(AgentsRoot, "nova", "workspace", "tmp", "script.ps1");
        IsDefinitionFile(path).ShouldBeFalse();
    }

    // ── test 6: .md in workspace/playbook subdirectory does NOT trigger ──────

    [Fact]
    public void IsAgentDefinitionFile_MdInWorkspaceSubdir_ReturnsFalse()
    {
        var path = Path.Combine(AgentsRoot, "nova", "workspace", "playbook", "ci-pr-monitor.md");
        IsDefinitionFile(path).ShouldBeFalse();
    }

    // ── test 7: default debounce constant is 2000ms ──────────────────────────

    [Fact]
    public void FileConfigurationWatcher_DefaultDebounce_Is2000ms()
    {
        // Verify via the ctor's default parameter value
        var ctor = typeof(FileAgentConfigurationSource.FileConfigurationWatcher)
            .GetConstructors(
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance)
            .FirstOrDefault();
        ctor.ShouldNotBeNull("No constructor found on FileConfigurationWatcher");

        var param = ctor!.GetParameters()
            .FirstOrDefault(p => p.Name == "reloadDebounceMs");
        param.ShouldNotBeNull("reloadDebounceMs parameter not found");
        param!.DefaultValue.ShouldBe(2000);
    }
}
