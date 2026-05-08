using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for InitCommand default configuration values.
/// </summary>
public sealed class InitCommandTests
{
    [Fact]
    public async Task Init_DefaultConfig_ListenUrl_BindsToAllInterfaces()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var cmd = new InitCommand();

            // Act
            var result = await cmd.ExecuteAsync(tempHome, force: false, verbose: false, CancellationToken.None);

            // Assert — listenUrl must bind to all interfaces so NetBird/remote access works
            var configPath = Path.Combine(tempHome, "config.json");
            var json = await File.ReadAllTextAsync(configPath);
            json.ShouldContain("0.0.0.0");
            json.ShouldNotContain("localhost:5005");
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }
}
