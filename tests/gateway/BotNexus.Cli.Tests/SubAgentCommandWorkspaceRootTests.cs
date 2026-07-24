using System.IO.Abstractions.TestingHelpers;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Pins that <c>subagent workspace list|prune</c> resolves the same configurable workspace root as
/// the gateway (#2040): the historical temp-root default when no override is configured, and the
/// configured <c>gateway.subAgents.workspaceRoot</c> when one is present in the platform config.
/// This guarantees the CLI reaper and the gateway can never target different directories.
/// </summary>
public sealed class SubAgentCommandWorkspaceRootTests
{
    [Fact]
    public void ResolveWorkspaceRoot_WhenNoConfig_UsesTempDefault()
    {
        var fs = new MockFileSystem();
        var command = new SubAgentCommand(fs);
        var home = fs.Path.Combine(fs.Path.GetTempPath(), "home-no-config");
        fs.Directory.CreateDirectory(home);

        var expected = fs.Path.Combine(
            fs.Path.GetTempPath(), SubAgentWorkspaceRootResolver.DefaultDirectoryName);

        command.ResolveWorkspaceRoot(home).ShouldBe(expected);
    }

    [Fact]
    public void ResolveWorkspaceRoot_WhenConfigOverride_UsesConfiguredRoot()
    {
        var fs = new MockFileSystem();
        var home = fs.Path.Combine(fs.Path.GetTempPath(), "home-with-config");
        fs.Directory.CreateDirectory(home);
        var configured = fs.Path.Combine(fs.Path.GetTempPath(), "custom-cli-root");
        var json = "{\"gateway\":{\"subAgents\":{\"workspaceRoot\":\"" +
            configured.Replace("\\", "\\\\") + "\"}}}";
        fs.File.WriteAllText(fs.Path.Combine(home, "config.json"), json);

        var command = new SubAgentCommand(fs);

        command.ResolveWorkspaceRoot(home).ShouldBe(fs.Path.GetFullPath(configured));
    }
}
