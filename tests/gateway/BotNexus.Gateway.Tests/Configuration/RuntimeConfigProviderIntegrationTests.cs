using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class RuntimeConfigProviderIntegrationTests : IDisposable
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

        var completed = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(changed.Task, "Expected IConfiguration reload pipeline to notify IOptionsMonitor.");
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

        var completed = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(changed.Task, "Expected provider-driven reload to trigger PlatformConfigAgentSource.Watch.");
        var descriptor = (await changed.Task).ShouldHaveSingleItem();
        descriptor.ExtensionConfig.ShouldContainKey("ext");
    }

    [Fact]
    public void RuntimeApiPaths_DoNotAddNewPlatformConfigLoaderLoadUsage_OutsideAllowlist()
    {
        var repoRoot = FindRepositoryRoot();
        var runtimeRoots = new[]
        {
            Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway"),
            Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api")
        };

        var allowedRuntimeLoadSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("src", "gateway", "BotNexus.Gateway", "Extensions", "GatewayServiceCollectionExtensions.cs"),
            Path.Combine("src", "gateway", "BotNexus.Gateway.Api", "Program.cs")
        };

        List<string> unexpected = [];
        HashSet<string> observedAllowed = new(StringComparer.OrdinalIgnoreCase);

        foreach (var root in runtimeRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file);
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (!line.Contains("PlatformConfigLoader.Load", StringComparison.Ordinal))
                        continue;

                    if (allowedRuntimeLoadSites.Contains(relativePath))
                    {
                        observedAllowed.Add(relativePath);
                        continue;
                    }

                    unexpected.Add($"{relativePath}:{index + 1} => {line.Trim()}");
                }
            }
        }

        unexpected.ShouldBeEmpty(
            "Runtime/API config loads must use IConfiguration + IOptionsMonitor provider reload. " +
            "Only explicitly documented bootstrap files are allowlisted.");
        observedAllowed.ShouldBe(allowedRuntimeLoadSites);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_rootPath))
                    Directory.Delete(_rootPath, recursive: true);

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
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
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
