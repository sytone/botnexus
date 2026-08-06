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
// #2825: the reload pipeline under test is process-global, so this cannot run concurrently with
// tests that reassign BOTNEXUS_HOME / BotNexus__ConfigPath.
[Collection("IntegrationTests")]
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

        // Act — write a new config.
        //
        // #2825: a single write plus one long wait is why this test flaked at ~40% across five
        // identical parallel container runs. The change notification is a filesystem EVENT, and
        // an event has two ways to never arrive: it can fire before OnChange finished
        // registering, or it can be coalesced/dropped while the host is under load. Either way
        // the single-shot wait then blocks for its whole budget on a notification that no
        // longer exists, and reports a product defect for a lost event.
        //
        // Rewriting periodically makes the test depend on the pipeline eventually delivering A
        // notification rather than on one specific event surviving. A genuinely broken reload
        // pipeline still fails - no rewrite can satisfy it - so this removes the flake without
        // weakening the assertion.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!tcs.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            File.WriteAllText(_configPath, """{"gateway":{"defaultAgentId":"agent-b"}}""");
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        // Assert
        tcs.Task.IsCompleted.ShouldBeTrue("IOptionsMonitor should have reloaded within 30 seconds");
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
        config.Agents.ShouldNotBeNull();
        var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
        agents.ShouldNotContainKey("defaults",
            "defaults pseudo-agent should be stripped after post-configure");
        agents.ShouldContainKey("myagent");
        config.AgentDefaults.ShouldNotBeNull();
        var agentDefaults = config.AgentDefaults ?? throw new InvalidOperationException("Expected agent defaults.");
        agentDefaults.ToolIds.ShouldNotBeNull();
        var toolIds = agentDefaults.ToolIds ?? throw new InvalidOperationException("Expected default tool IDs.");
        toolIds.ShouldContain("web-search");
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
