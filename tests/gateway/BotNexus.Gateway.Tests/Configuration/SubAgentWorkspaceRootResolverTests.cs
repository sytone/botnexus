using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Contract for the single shared sub-agent workspace-root resolver (#2040). The gateway and the
/// CLI reaper both go through this helper, so these tests pin the three behaviours the issue
/// requires: the preserved temp-root default when unset, an honoured configured override, and
/// consistent <c>~</c>/environment-variable expansion normalized to an absolute path.
/// </summary>
public sealed class SubAgentWorkspaceRootResolverTests
{
    [Fact]
    public void Resolve_WhenUnset_PreservesHistoricalTempDefault()
    {
        var fileSystem = new MockFileSystem();
        var expected = fileSystem.Path.Combine(
            fileSystem.Path.GetTempPath(),
            SubAgentWorkspaceRootResolver.DefaultDirectoryName);

        SubAgentWorkspaceRootResolver.Resolve(null, fileSystem).ShouldBe(expected);
        SubAgentWorkspaceRootResolver.Resolve("   ", fileSystem).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_WhenOverrideSet_UsesConfiguredAbsolutePath()
    {
        var fileSystem = new MockFileSystem();
        var configured = fileSystem.Path.Combine(
            fileSystem.Path.GetTempPath(), "custom-subagent-root");

        var resolved = SubAgentWorkspaceRootResolver.Resolve(configured, fileSystem);

        resolved.ShouldBe(fileSystem.Path.GetFullPath(configured));
        fileSystem.Path.IsPathRooted(resolved).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_ExpandsEnvironmentVariables()
    {
        var fileSystem = new MockFileSystem();
        var marker = "BOTNEXUS_TEST_SAWR_" + Guid.NewGuid().ToString("N");
        var target = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "env-expanded-root");
        Environment.SetEnvironmentVariable(marker, target);
        try
        {
            var resolved = SubAgentWorkspaceRootResolver.Resolve($"%{marker}%", fileSystem);
            resolved.ShouldBe(fileSystem.Path.GetFullPath(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
        }
    }

    [Fact]
    public void Resolve_ExpandsLeadingHomeTilde()
    {
        var fileSystem = new MockFileSystem();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        home.ShouldNotBeNullOrWhiteSpace();

        var resolved = SubAgentWorkspaceRootResolver.Resolve("~/subagent-workspaces", fileSystem);

        resolved.ShouldBe(fileSystem.Path.GetFullPath(Path.Combine(home, "subagent-workspaces")));
    }
}
