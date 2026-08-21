using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

        using var serviceProvider = BuildServiceProvider(_configPath);
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var resolver = new DefaultLocationResolver(monitor);
        resolver.ResolvePath("repo-root").ShouldBe(initialPath);

        var backupDirectory = Path.Combine(_rootPath, "backups");
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, new ConfigBackupService(backupDirectory, _fileSystem));
        var changed = new TaskCompletionSource<PlatformConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange((config, _) =>
        {
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

        await changed.Task.WaitAsync(TimeSpan.FromMinutes(2));
        resolver.ResolvePath("repo-root").ShouldBe(updatedPath);
        Directory.GetFiles(backupDirectory, "config-*.json").Length.ShouldBe(1);
    }

    [Fact]
    public async Task PlatformConfigAgentSource_WhenGatewayExtensionDefaultsChange_ReceivesProviderReload()
    {
        await File.WriteAllTextAsync(_configPath, """
            {
              "gateway": {
                "extensions": {
                  "defaults": {
                    "ext": {
                      "a": 1
                    }
                  }
                }
              },
              "agents": {
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

            if (!descriptors[0].ExtensionConfig.TryGetValue("ext", out var extensionJson))
                return;

            using var jsonDocument = JsonDocument.Parse(extensionJson.GetRawText());
            if (jsonDocument.RootElement.TryGetProperty("b", out var value) && value.GetInt32() == 2)
                changed.TrySetResult(descriptors);
        });

        var gatewayUpdate = JsonNode.Parse("""
            {
              "extensions": {
                "defaults": {
                  "ext": {
                    "a": 1,
                    "b": 2
                  }
                }
              }
            }
            """)!;
        await writer.UpdateSectionAsync("gateway", gatewayUpdate);

        var descriptor = (await changed.Task.WaitAsync(TimeSpan.FromMinutes(2))).ShouldHaveSingleItem();
        descriptor.ExtensionConfig.ShouldContainKey("ext");
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
    {
        var configuration = (IConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_ => configuration);
        services.AddSingleton<IConfiguration>(provider => provider.GetRequiredService<IConfigurationRoot>());
        services.AddOptions<PlatformConfig>().Bind(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            _ => new PlatformConfigPostConfigure(configuration, configPath));
        services.AddSingleton<IValidateOptions<PlatformConfig>, PlatformConfigOptionsValidator>();
        return services.BuildServiceProvider();
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
