using System.IO.Abstractions;
using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for the SECOND instance of #2819, in
/// <see cref="MemoryDreamingCronAction"/>.
/// </summary>
/// <remarks>
/// The original #2819 fix corrected the cron STORE's home resolution but left an identical stale
/// assembly-qualified string in the dreaming action:
/// <c>Type.GetType("BotNexus.Gateway.Configuration.BotNexusHome, BotNexus.Gateway")</c>. Because
/// #2765/#2777 moved the type out of the BotNexus.Gateway assembly, that lookup returned null at
/// runtime and the action fell back to the user-profile root -- so a gateway launched with an
/// isolated <c>--target</c> home would consolidate memory against the DEVELOPER'S real agent
/// workspace, reading and rewriting live MEMORY.md content.
///
/// A single string is enough to redirect production state, and the compiler cannot see it. These
/// tests assert the resolved path so a future assembly move fails here as well as at build time.
/// </remarks>
public sealed class MemoryDreamingWorkspacePathTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> configureHome)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystem>(new FileSystem());
        configureHome(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DreamingWorkspace_UsesTheRegisteredHome_NotTheUserProfile()
    {
        var isolatedHome = Path.Combine(Path.GetTempPath(), "botnexus-2819-dream-" + Guid.NewGuid().ToString("N"));

        using var provider = BuildProvider(
            services => services.AddSingleton(new BotNexusHome(new FileSystem(), isolatedHome)));

        var resolved = MemoryDreamingCronAction.ResolveWorkspacePath(provider, AgentId.From("farnsworth"));

        // The defect in one assertion: pre-fix this resolves under ~/.botnexus regardless of the
        // home the host supplied, so an isolated gateway dreams over the live workspace.
        resolved.ShouldBe(Path.Combine(isolatedHome, "agents", "farnsworth", "workspace"));
    }

    [Fact]
    public void DreamingWorkspace_HonoursTheHomeOverrideEnvironmentVariable()
    {
        // The exact production shape: `gateway start --target <home>` sets BOTNEXUS_HOME and lets
        // DI construct BotNexusHome. The env var was always set correctly; the action ignored it.
        var isolatedHome = Path.Combine(Path.GetTempPath(), "botnexus-2819-dreamenv-" + Guid.NewGuid().ToString("N"));
        var before = Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, isolatedHome);

            using var provider = BuildProvider(
                services => services.AddSingleton(new BotNexusHome(new FileSystem())));

            var resolved = MemoryDreamingCronAction.ResolveWorkspacePath(provider, AgentId.From("farnsworth"));

            resolved.ShouldNotBeNull();
            var profileRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".botnexus"));

            resolved!.ShouldNotStartWith(
                profileRoot,
                Case.Sensitive,
                "dreaming must never target the shared live workspace when the host supplied an isolated home (#2819)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, before);
        }
    }
}
