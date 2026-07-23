using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the sub-agent workspace sweep options defaults and the hosted-service orchestration
/// (single-pass behaviour, enable/disable gating) for issue #2237.
/// </summary>
public sealed class SubAgentWorkspaceSweepHostedServiceTests
{
    [Fact]
    public void Options_HaveSafeAutomaticDefaults()
    {
        var options = new SubAgentWorkspaceSweepOptions();

        options.Enabled.ShouldBeTrue();
        options.RetentionHours.ShouldBe(24);
        options.GraceMinutes.ShouldBe(60);
        options.Retention.ShouldBe(TimeSpan.FromHours(24));
        options.Grace.ShouldBe(TimeSpan.FromMinutes(60));
        options.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Options_NonPositiveRetentionAndGrace_MapToZero()
    {
        var options = new SubAgentWorkspaceSweepOptions { RetentionHours = 0, GraceMinutes = -5 };

        options.Retention.ShouldBe(TimeSpan.Zero);
        options.Grace.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void RunSweepOnce_WhenDisabled_IsNoOp()
    {
        var (service, fileSystem, agentsRoot) = CreateService(new SubAgentWorkspaceSweepOptions { Enabled = false });
        var dir = CreateOldHusk(fileSystem, agentsRoot);

        var result = service.RunSweepOnce();

        result.Removed.ShouldBe(0);
        fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    [Fact]
    public void RunSweepOnce_WhenEnabled_RemovesExpiredHuskUnderResolvedAgentsRoot()
    {
        var (service, fileSystem, agentsRoot) = CreateService(new SubAgentWorkspaceSweepOptions());
        var dir = CreateOldHusk(fileSystem, agentsRoot);

        var result = service.RunSweepOnce();

        result.Removed.ShouldBe(1);
        fileSystem.Directory.Exists(dir).ShouldBeFalse();
    }

    private static string CreateOldHusk(MockFileSystem fileSystem, string agentsRoot)
    {
        var dir = fileSystem.Path.Combine(agentsRoot, "farnsworth--subagent--coder--old123");
        fileSystem.Directory.CreateDirectory(dir);
        fileSystem.Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromDays(3));
        return dir;
    }

    private static (SubAgentWorkspaceSweepHostedService Service, MockFileSystem FileSystem, string AgentsRoot) CreateService(
        SubAgentWorkspaceSweepOptions options)
    {
        var fileSystem = new MockFileSystem();
        var homePath = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "botnexus-sweep-hosted", Guid.NewGuid().ToString("N"));
        var home = new BotNexusHome(fileSystem, homePath);
        home.Initialize();

        var service = new SubAgentWorkspaceSweepHostedService(
            home,
            fileSystem,
            Options.Create(options),
            NullLogger<SubAgentWorkspaceSweepHostedService>.Instance);

        return (service, fileSystem, home.AgentsPath);
    }
}
