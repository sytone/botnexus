using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Pins the shared default/custom root contract using initialized canonical configuration authority.
/// Workspace paths remain mock-backed so these tests cannot touch physical default workspaces.
/// </summary>
public sealed class SubAgentCommandWorkspaceRootTests
{
    [Fact]
    public async Task ResolveWorkspaceRoot_WhenNoConfig_UsesTempDefault()
    {
        using var fixture = new CleanupCommandFixture();
        await fixture.SeedAuthorityAsync(new JsonObject());

        fixture.Command().ResolveWorkspaceRoot(fixture.Home).ShouldBe(fixture.DefaultRoot);
        fixture.DefaultRoot.ShouldBe(fixture.Workspaces.Path.Combine(
            fixture.Workspaces.Path.GetTempPath(), SubAgentWorkspaceRootResolver.DefaultDirectoryName));
    }

    [Fact]
    public async Task ResolveWorkspaceRoot_WhenConfigOverride_UsesConfiguredRoot()
    {
        using var fixture = new CleanupCommandFixture();
        await fixture.SeedAuthorityAsync(fixture.CustomDocument());

        fixture.Command().ResolveWorkspaceRoot(fixture.Home).ShouldBe(
            fixture.Workspaces.Path.GetFullPath(fixture.CustomRoot));
    }
}
