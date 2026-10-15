using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Gateway.Tests.Configuration;

// #2825: builds an IConfiguration root over a file it then rewrites, and asserts the reload
// pipeline observes it. That pipeline is process-global, so this must not run concurrently with
// any test that swaps BOTNEXUS_HOME / BotNexus__ConfigPath out from under it.
[Collection("IntegrationTests")]
public sealed class RuntimeConfigProviderIntegrationTests : IAsyncLifetime
{
    private readonly string _rootPath;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public RuntimeConfigProviderIntegrationTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-runtime-config-provider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
    }

    [Fact]
    public async Task LocationsResolver_WhenWriterUpdatesConfig_ReloadsViaProviderWithoutManualNotification()
    {
        var initialPath = Path.Combine(_rootPath, "repo-a");
        var updatedPath = Path.Combine(_rootPath, "repo-b");
        var initialConfig = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["locations"] = new JsonObject
                {
                    ["repo-root"] = new JsonObject
                    {
                        ["type"] = "filesystem",
                        ["path"] = initialPath
                    }
                }
            }
        };
        await File.WriteAllTextAsync(_configPath, initialConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var rejections = new ConcurrentQueue<string>();
        using var serviceProvider = BuildServiceProvider(_configPath, out var configuration,
            onLoadFailure: (message, error) => rejections.Enqueue($"{message} {error}"));
        var provider = configuration.Providers.ShouldHaveSingleItem().ShouldBeAssignableTo<FileConfigurationProvider>();
        provider.Source.FileProvider.ShouldBeOfType<PhysicalFileProvider>();
        provider.Source.ReloadOnChange.ShouldBeTrue();
        var providerNotifications = 0;
        using var providerSubscription = ChangeToken.OnChange(provider.GetReloadToken,
            () => Interlocked.Increment(ref providerNotifications));
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var resolver = new DefaultLocationResolver(monitor);
        resolver.ResolvePath("repo-root").ShouldBe(initialPath);

        var backupDirectory = Path.Combine(_rootPath, "backups");
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, new ConfigBackupService(backupDirectory, _fileSystem));
        var changed = new TaskCompletionSource<PlatformConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionsCallbacks = 0;
        using var subscription = monitor.OnChange((config, _) =>
        {
            Interlocked.Increment(ref optionsCallbacks);
            if (config.Gateway?.Locations?.TryGetValue("repo-root", out var location) == true
                && string.Equals(location.Path, updatedPath, StringComparison.Ordinal))
            {
                changed.TrySetResult(config);
            }
        });

        var gatewayUpdate = new JsonObject
        {
            ["locations"] = new JsonObject
            {
                ["repo-root"] = new JsonObject
                {
                    ["type"] = "filesystem",
                    ["path"] = updatedPath
                }
            }
        };

        await writer.UpdateSectionAsync("gateway", gatewayUpdate);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(_configPath));
        var persistedPath = persisted?["gateway"]?["locations"]?["repo-root"]?["path"]?.GetValue<string>();
        persistedPath.ShouldBe(updatedPath, "the writer must persist the new canonical location before waiting for reload propagation");
        try
        {
            await changed.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException error)
        {
            throw LocationReloadTimeout(error, persistedPath, configuration,
                Volatile.Read(ref providerNotifications), Volatile.Read(ref optionsCallbacks), rejections);
        }
        resolver.ResolvePath("repo-root").ShouldBe(updatedPath);
        Directory.GetFiles(backupDirectory, "config-*.json").Length.ShouldBe(1);
    }

    /// <summary>
    /// A config write reaches <c>PlatformConfigAgentSource</c> through the provider and re-materialises
    /// the agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #3515: this previously changed extension defaults and asserted that a merged extension bag
    /// reached the descriptor. Defaults are now carried separately, while this test still changes
    /// the named agent so the effective descriptor fingerprint must change and notify.
    /// </para>
    /// <para>
    /// The subject under test is reload PROPAGATION, so the edit moved to the agent block - a change
    /// that genuinely alters the descriptor. That is a stronger test of the same property: it proves
    /// the provider-to-source path works AND that the #2114 suppression is not swallowing real
    /// changes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PlatformConfigAgentSource_WhenAgentConfigChanges_ReceivesProviderReload()
    {
        await File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "defaults": {
                  "extensions": {
                    "ext": {
                      "a": 1
                    }
                  }
                },
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "enabled": true
                }
              }
            }
            """);

        using var serviceProvider = BuildServiceProvider(_configPath);
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var source = new PlatformConfigAgentSource(
            monitor,
            _rootPath,
            new NullLogger<PlatformConfigAgentSource>());

        var writer = new PlatformConfigWriter(_configPath, _fileSystem, null);
        var changed = new TaskCompletionSource<IReadOnlyList<BotNexus.Gateway.Abstractions.Models.AgentDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = source.Watch(descriptors =>
        {
            if (descriptors.Count != 1)
                return;

            // #3515: the gate is the agent's own value. This previously waited for the world-level
            // extension defaults to appear in ExtensionConfig, which no longer happens because
            // nothing merges them in.
            if (descriptors[0].DisplayName == "Assistant Renamed")
                changed.TrySetResult(descriptors);
        });

        // Edit the named agent. A defaults-only edit is not the subject of this reload-propagation
        // test; the display-name change must alter the descriptor fingerprint and trigger callback.
        var agentsUpdate = JsonNode.Parse("""
            {
              "assistant": {
                "provider": "copilot",
                "model": "gpt-4.1",
                "enabled": true,
                "displayName": "Assistant Renamed"
              }
            }
            """)!;
        await writer.UpdateSectionAsync("agents", agentsUpdate);

        var descriptor = (await changed.Task.WaitAsync(TimeSpan.FromMinutes(2))).ShouldHaveSingleItem();
        descriptor.DisplayName.ShouldBe("Assistant Renamed", "the reload must carry the new value");
    }

    /// <summary>
    /// No runtime or API code loads platform configuration directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This previously allowlisted two bootstrap sites - <c>GatewayServiceCollectionExtensions</c>
    /// and <c>Program.cs</c> - and asserted they were still present, because at the time the host
    /// genuinely needed a config value before the pipeline existed.
    /// </para>
    /// <para>
    /// #3504 removed that need: <c>Program.cs</c> binds from <c>builder.Configuration</c>, which is
    /// already built, and the registration path constructs the same provider pipeline when no
    /// <c>IConfiguration</c> was threaded in. The allowlist is therefore empty, and the assertion
    /// flips from "these two still load" to "nothing loads". Keeping the old positive assertion
    /// would have required re-introducing a hand-rolled load to satisfy it.
    /// </para>
    /// </remarks>
    [Fact]
    public void RuntimeApiPaths_DoNotLoadPlatformConfigDirectly()
    {
        var repoRoot = FindRepositoryRoot();
        var runtimeRoots = new[]
        {
            Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway"),
            Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api")
        };

        List<string> unexpected = [];

        foreach (var root in runtimeRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file);
                if (relativePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relativePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (!line.Contains("PlatformConfigLoader.Load", StringComparison.Ordinal))
                        continue;

                    unexpected.Add($"{relativePath}:{index + 1} => {line.Trim()}");
                }
            }
        }

        unexpected.ShouldBeEmpty(
            "Runtime/API config reads must go through IConfiguration + IOptionsMonitor (#3504). " +
            "Program.cs binds from builder.Configuration; the registration path builds the same " +
            "pipeline when no IConfiguration is supplied. There are no remaining bootstrap loads.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildServiceProvider_DisposeWithoutResolvingConfiguration_DisposesRoot(bool resolveOptions)
    {
        await File.WriteAllTextAsync(_configPath, "{}");
        var probe = new DisposalProbe();
        using var services = BuildServiceProvider(_configPath, out var configuration, probe);
        try
        {
            if (resolveOptions)
                _ = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;

            services.Dispose();
            probe.Disposed.ShouldBeTrue("the fixture must own its root even when neither configuration registration is requested");
        }
        finally
        {
            // Also release the physical watcher when the ownership assertion is RED.
            ((IDisposable)configuration).Dispose();
        }
    }

    [Fact]
    public void LocationReloadTimeout_IncludesBoundaryEvidenceAndOriginalException()
    {
        using var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gateway:locations:repo-root:path"] = "visible-old-path"
        }).Build() as ConfigurationRoot ?? throw new InvalidOperationException("Expected a configuration root.");
        var original = new TimeoutException("original deadline");
        var error = LocationReloadTimeout(original, "persisted-new-path", configuration, 3, 2,
            new ConcurrentQueue<string>(["candidate rejected: invalid listenUrl"]));

        error.InnerException.ShouldBeSameAs(original);
        error.Message.ShouldContain("persistedPath='persisted-new-path'");
        error.Message.ShouldContain("providerReloadNotifications=3");
        error.Message.ShouldContain("configurationPath='visible-old-path'");
        error.Message.ShouldContain("optionsCallbacks=2");
        error.Message.ShouldContain("candidate rejected: invalid listenUrl");
    }

    private static TimeoutException LocationReloadTimeout(
        TimeoutException error, string? persistedPath, IConfigurationRoot configuration,
        int providerNotifications, int optionsCallbacks, ConcurrentQueue<string> rejections)
        => new(
            $"Location reload did not reach the expected options callback within the original two-minute deadline. " +
            $"persistedPath='{persistedPath}'; providerReloadNotifications={providerNotifications}; " +
            $"configurationPath='{configuration["gateway:locations:repo-root:path"]}'; " +
            $"optionsCallbacks={optionsCallbacks}; rejections=[{string.Join(" | ", rejections.ToArray())}]",
            error);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestAwait.EventuallyAsync(
            () =>
            {
                try
                {
                    if (Directory.Exists(_rootPath))
                        Directory.Delete(_rootPath, recursive: true);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            },
            $"runtime configuration test directory '{_rootPath}' to be deletable",
            timeout: TimeSpan.FromSeconds(2));
    }

    private static ServiceProvider BuildServiceProvider(string configPath)
        => BuildServiceProvider(configPath, out _);

    private static ServiceProvider BuildServiceProvider(
        string configPath, out IConfigurationRoot configuration, IConfigurationSource? ownershipProbe = null,
        Action<string, Exception?>? onLoadFailure = null)
    {
        var builder = new ConfigurationBuilder()
            .AddResilientJsonFile(configPath, optional: false, reloadOnChange: true, onLoadFailure: onLoadFailure);
        if (ownershipProbe is not null)
            builder.Add(ownershipProbe);
        var root = builder.Build();
        configuration = root;

        var services = new ServiceCollection();
        services.AddSingleton(_ => root);
        services.AddSingleton<IConfiguration>(provider => provider.GetRequiredService<IConfigurationRoot>());
        services.AddOptions<PlatformConfig>().Bind(root);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            _ => new PlatformConfigPostConfigure(root, configPath));
        services.AddSingleton<IValidateOptions<PlatformConfig>, PlatformConfigOptionsValidator>();
        var provider = services.BuildServiceProvider();
        // Binding captures root directly, so resolving options alone never activates this factory.
        // Activate it here to transfer disposal ownership before returning the fixture's container.
        _ = provider.GetRequiredService<IConfigurationRoot>();
        return provider;
    }

    private sealed class DisposalProbe : ConfigurationProvider, IConfigurationSource, IDisposable
    {
        public bool Disposed { get; private set; }
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
        public void Dispose() => Disposed = true;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
