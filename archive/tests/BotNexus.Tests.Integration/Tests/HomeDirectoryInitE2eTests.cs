using System.Net;
using BotNexus.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-AWM-009: BotNexus home directory initialization
/// Validates that Gateway startup creates the full ~/.botnexus/ directory
/// structure when BOTNEXUS_HOME points to a clean temp directory.
/// </summary>
[CollectionDefinition("home-directory-init-e2e", DisableParallelization = true)]
public sealed class HomeDirectoryInitE2eCollection;

[Collection("home-directory-init-e2e")]
public sealed class HomeDirectoryInitE2eTests : IDisposable
{
    private readonly string _testHomePath;
    private readonly string? _originalHomeOverride;

    public HomeDirectoryInitE2eTests()
    {
        _originalHomeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        _testHomePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "home-init-e2e",
            Guid.NewGuid().ToString("N"));
        // Do NOT pre-create the directory — the Gateway should create it
    }

    [Fact]
    public async Task GatewayStartup_WithCleanHome_CreatesFullDirectoryStructure()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _testHomePath);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:Gateway:ApiKey"] = string.Empty,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                });
            });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify directory structure was created
        Directory.Exists(_testHomePath).Should().BeTrue("BOTNEXUS_HOME should exist");
        Directory.Exists(Path.Combine(_testHomePath, "agents")).Should().BeTrue("agents/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "extensions")).Should().BeTrue("extensions/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "extensions", "providers")).Should().BeTrue("extensions/providers/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "extensions", "channels")).Should().BeTrue("extensions/channels/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "extensions", "tools")).Should().BeTrue("extensions/tools/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "tokens")).Should().BeTrue("tokens/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "sessions")).Should().BeTrue("sessions/ should exist");
        Directory.Exists(Path.Combine(_testHomePath, "logs")).Should().BeTrue("logs/ should exist");
    }

    [Fact]
    public async Task GatewayStartup_WithCleanHome_CreatesDefaultConfig()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _testHomePath);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:Gateway:ApiKey"] = string.Empty,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                });
            });

        using var client = factory.CreateClient();
        _ = await client.GetAsync("/health");

        var configPath = Path.Combine(_testHomePath, "config.json");
        File.Exists(configPath).Should().BeTrue("config.json should be created on first run");

        var configContent = await File.ReadAllTextAsync(configPath);
        configContent.Should().Contain("BotNexus", "config.json should contain BotNexus section");
    }

    [Fact]
    public void Initialize_WithBotnexusHome_CreatesPerAgentWorkspaces()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _testHomePath);
        BotNexusHome.Initialize();

        BotNexusHome.InitializeAgentWorkspace("nova");
        BotNexusHome.InitializeAgentWorkspace("quill");

        var novaPath = Path.Combine(_testHomePath, "agents", "nova");
        var quillPath = Path.Combine(_testHomePath, "agents", "quill");

        Directory.Exists(novaPath).Should().BeTrue();
        Directory.Exists(Path.Combine(novaPath, "memory")).Should().BeTrue();
        Directory.Exists(Path.Combine(novaPath, "memory", "daily")).Should().BeTrue();
        Directory.Exists(quillPath).Should().BeTrue();
        Directory.Exists(Path.Combine(quillPath, "memory", "daily")).Should().BeTrue();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _originalHomeOverride);
        if (Directory.Exists(_testHomePath))
        {
            try { Directory.Delete(_testHomePath, recursive: true); } catch { }
        }
    }
}
