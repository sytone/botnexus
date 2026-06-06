using System.Reflection;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function: no code may reference <c>FileAgentConfigurationSource</c>
/// or <c>FileAgentConfigurationWriter</c>. Both classes were removed in #945.
/// Agent definitions live in <c>config.json</c> only, loaded via <c>PlatformConfigAgentSource</c>.
/// </summary>
/// <remarks>
/// The <c>~/.botnexus/agents/</c> directory is workspace-only (SOUL.md, MEMORY.md, workspace
/// files, etc.) — no agent definition JSON files are written there.
/// </remarks>
public sealed class FileAgentConfigurationRemovedTests
{
    private static readonly Assembly[] SolutionAssemblies = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.FullName?.StartsWith("BotNexus.", StringComparison.OrdinalIgnoreCase) == true)
        .ToArray();

    [Fact]
    public void FileAgentConfigurationSource_DoesNotExistInAnySolutionAssembly()
    {
        var violations = SolutionAssemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.Name == "FileAgentConfigurationSource" ||
                        t.FullName?.Contains("FileAgentConfigurationSource") == true)
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        violations.ShouldBeEmpty(
            "FileAgentConfigurationSource was deleted in #945 and must not be re-introduced. " +
            "Agent definitions belong in config.json only. Found: " + string.Join(", ", violations));
    }

    [Fact]
    public void FileAgentConfigurationWriter_DoesNotExistInAnySolutionAssembly()
    {
        var violations = SolutionAssemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.Name == "FileAgentConfigurationWriter" ||
                        t.FullName?.Contains("FileAgentConfigurationWriter") == true)
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        violations.ShouldBeEmpty(
            "FileAgentConfigurationWriter was deleted in #945 and must not be re-introduced. " +
            "Agent writes go through PlatformConfigAgentWriter to config.json only. Found: " + string.Join(", ", violations));
    }
}
