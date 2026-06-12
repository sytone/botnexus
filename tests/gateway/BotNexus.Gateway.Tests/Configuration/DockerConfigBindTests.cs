using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class DockerConfigBindTests
{
    [Fact]
    public void LoadConfigForRegistration_DoesNotCrash_WhenRuntimeVersionKeyPresent()
    {
        // Arrange: simulate Docker environment where IConfiguration has runtimeOptions keys
        var configJson = """
        {
            "version": 1,
            "gateway": {
                "listenUrl": "http://0.0.0.0:5000"
            }
        }
        """;

        var configPath = "/app/config/config.json";
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(configPath, new MockFileData(configJson));

        // Build IConfiguration that includes a conflicting "Version" key
        // (simulates what .NET runtime injects in Docker containers)
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["runtimeOptions:framework:version"] = "10.0.9",
                ["Version"] = "10.0.9" // This is what causes the crash with Bind()
            })
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson)))
            .Build();

        // Act: should not throw (previously threw InvalidOperationException)
        var config = PlatformConfigLoader.Load(configPath, fileSystem: fileSystem);

        // Assert
        Assert.Equal(1, config.Version);
        Assert.Equal("http://0.0.0.0:5000", config.Gateway?.ListenUrl);
    }

    [Fact]
    public void LoadConfigForRegistration_WorksWithMissingFile()
    {
        var fileSystem = new MockFileSystem();
        var configPath = "/app/config/config.json";

        // File doesn't exist — should return default config without crash
        var config = PlatformConfigLoader.Load(configPath, fileSystem: fileSystem);

        Assert.NotNull(config);
        Assert.Equal(1, config.Version);
    }
}
