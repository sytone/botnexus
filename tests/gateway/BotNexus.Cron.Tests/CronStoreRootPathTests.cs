using System.IO.Abstractions;
using BotNexus.Cron.Extensions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for #2819: the cron store silently ignored an explicitly supplied home and
/// opened the shared live <c>~/.botnexus/cron.sqlite</c> instead.
/// </summary>
/// <remarks>
/// The defect was a single stale string. <c>ResolveRootPath</c> located <see cref="BotNexusHome"/>
/// via <c>Type.GetType("BotNexus.Gateway.Configuration.BotNexusHome, BotNexus.Gateway")</c>. When
/// #2765/#2777 extracted the type into the BotNexus.Gateway.Configuration assembly the
/// assembly-qualified name stopped resolving, <c>Type.GetType</c> returned null, and the resolver
/// fell through to its user-profile default -- with nothing failing at compile time and nothing
/// logged at runtime.
///
/// The consequence was not cosmetic. Test gateways started with an isolated <c>--target</c> home
/// opened the developer's REAL cron store, claimed scheduled jobs they had no agent registry to
/// run, and failed them with "Agent 'farnsworth' is not registered" -- for days, invisibly, because
/// the interactive gateway kept working.
///
/// These tests assert the OBSERVABLE OUTCOME: which <c>cron.sqlite</c> file actually appears on
/// disk after the store initialises. That is deliberately stronger than inspecting the resolver:
/// it would still catch the defect if the resolution strategy were replaced entirely, and it
/// cannot pass by agreeing with the implementation about where the file "should" be.
/// </remarks>
public sealed class CronStoreRootPathTests
{
    private static readonly SemaphoreSlim EnvLock = new(1, 1);

    /// <summary>
    /// Best-effort recursive delete. SQLite may still hold the file briefly after the provider is
    /// disposed, and a cleanup IOException must never be reported as an assertion failure -- that
    /// would make a passing fix look broken.
    /// </summary>
    private static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Builds the provider the way a host does, initialises the store, and reports whether the
    /// expected isolated <c>cron.sqlite</c> was created.
    /// </summary>
    private static async Task<bool> StoreCreatesDatabaseInAsync(
        string expectedDirectory,
        Action<IServiceCollection> configureHome)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystem>(new FileSystem());
        configureHome(services);
        services.AddBotNexusCron();

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ICronStore>();

        // Initialising is what actually opens (and creates) the database file.
        await store.InitializeAsync(CancellationToken.None);

        return File.Exists(Path.Combine(expectedDirectory, "cron.sqlite"));
    }

    [Fact]
    public async Task CronStore_UsesTheRegisteredHome_NotTheUserProfile()
    {
        var isolatedHome = Path.Combine(Path.GetTempPath(), "botnexus-2819-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedHome);

        try
        {
            var created = await StoreCreatesDatabaseInAsync(
                isolatedHome,
                services => services.AddSingleton(new BotNexusHome(new FileSystem(), isolatedHome)));

            // The whole defect in one assertion: an explicitly supplied home must win. Pre-fix this
            // is false, because the store opened ~/.botnexus/cron.sqlite instead.
            created.ShouldBeTrue(
                "a host that supplied its own home must not have its cron jobs read from the shared live store (#2819)");
        }
        finally
        {
            TryDelete(isolatedHome);
        }
    }

    [Fact]
    public async Task CronStore_PrefersTheWritableDataDirectory_OverAReadOnlyConfigRoot()
    {
        var configRoot = Path.Combine(Path.GetTempPath(), "botnexus-2819-cfg-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(Path.GetTempPath(), "botnexus-2819-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(dataRoot);

        try
        {
            // DataPath exists so cron.sqlite still works when the config root is mounted read-only.
            var createdInData = await StoreCreatesDatabaseInAsync(
                dataRoot,
                services => services.AddSingleton(new BotNexusHome(new FileSystem(), configRoot, dataRoot)));

            createdInData.ShouldBeTrue("the writable data directory must win over a possibly read-only config root");
            File.Exists(Path.Combine(configRoot, "cron.sqlite")).ShouldBeFalse(
                "the store must not also land in the config root");
        }
        finally
        {
            TryDelete(configRoot);
            TryDelete(dataRoot);
        }
    }

    [Fact]
    public async Task CronStore_HonoursTheHomeOverrideEnvironmentVariable()
    {
        // A host that sets BOTNEXUS_HOME (as `gateway start --target` does) and lets DI construct
        // BotNexusHome must still get an isolated store. This is the EXACT shape that failed in
        // production: the env var was set correctly, the home was registered correctly, and the
        // cron store ignored both.
        var isolatedHome = Path.Combine(Path.GetTempPath(), "botnexus-2819-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedHome);

        await EnvLock.WaitAsync();
        var before = Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, isolatedHome);

            var created = await StoreCreatesDatabaseInAsync(
                isolatedHome,
                services => services.AddSingleton(new BotNexusHome(new FileSystem())));

            created.ShouldBeTrue("BOTNEXUS_HOME must isolate the cron store, not merely the config file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, before);
            EnvLock.Release();
            TryDelete(isolatedHome);
        }
    }
}
