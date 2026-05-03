using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests that PlatformConfig hot-reloads via IOptionsMonitor when config.json is updated.
/// Replaces the deleted PlatformConfigWatcherTests which tested the removed custom watcher.
/// </summary>
public sealed class PlatformConfigReloadTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _configPath;

    public PlatformConfigReloadTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-reload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
        File.WriteAllText(_configPath, """{"gateway":{"defaultAgentId":"agent-a"}}""");
    }

    [Fact]
    public async Task IOptionsMonitor_WhenConfigFileChanges_ReloadsViaIConfiguration()
    {
        // Arrange — build a minimal DI container with config.json in the IConfiguration pipeline
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddJsonFile(_configPath, optional: false, reloadOnChange: true);
        var configuration = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<PlatformConfig>().Bind(configuration);

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<PlatformConfig>>();

        monitor.CurrentValue.Gateway?.DefaultAgentId.ShouldBe("agent-a");

        var tcs = new TaskCompletionSource<PlatformConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = monitor.OnChange((cfg, _) => tcs.TrySetResult(cfg));

        // Act — write a new config
        File.WriteAllText(_configPath, """{"gateway":{"defaultAgentId":"agent-b"}}""");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert
        completed.ShouldBe(tcs.Task, "IOptionsMonitor should have reloaded within 10 seconds");
        (await tcs.Task).Gateway?.DefaultAgentId.ShouldBe("agent-b");
    }

    [Fact]
    public void PlatformConfigPostConfigure_ExtractsAgentDefaults_FromIConfiguration()
    {
        // Arrange — config.json with agents.defaults
        File.WriteAllText(_configPath, """
            {
              "agents": {
                "defaults": { "toolIds": ["web-search"] },
                "myagent": { "provider": "openai", "model": "gpt-3.5", "enabled": true }
              }
            }
            """);

        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddJsonFile(_configPath, optional: false, reloadOnChange: false);
        var configuration = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<PlatformConfig>().Bind(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            _ => new PlatformConfigPostConfigure(configuration, _configPath));

        using var sp = services.BuildServiceProvider();
        var config = sp.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;

        // Assert
        config.Agents.ShouldNotContainKey("defaults",
            "defaults pseudo-agent should be stripped after post-configure");
        config.Agents.ShouldContainKey("myagent");
        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.ToolIds.ShouldNotBeNull();
        config.AgentDefaults!.ToolIds!.ShouldContain("web-search");
    }

    [Fact]
    public void PlatformConfigPostConfigure_MigratesLegacyGatewayFields()
    {
        // Arrange — config.json with root-level legacy fields (pre-gateway-section format)
        File.WriteAllText(_configPath, """
            {
              "defaultAgentId": "legacy-agent",
              "listenUrl": "http://localhost:9999"
            }
            """);

        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddJsonFile(_configPath, optional: false, reloadOnChange: false);
        var configuration = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<PlatformConfig>().Bind(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            _ => new PlatformConfigPostConfigure(configuration, _configPath));

        using var sp = services.BuildServiceProvider();
        var config = sp.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;

        // Assert — legacy fields migrated into gateway section
        config.Gateway.ShouldNotBeNull();
        config.Gateway!.DefaultAgentId.ShouldBe("legacy-agent");
        config.Gateway.ListenUrl.ShouldBe("http://localhost:9999");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }
}
