using System.Text.Json;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pins the gateway composition half of the home-root world sentinel (#3411). The sentinel and
/// file-store containment guards are independently tested; these tests prove the production DI path
/// actually supplies both with the one world identity resolved from platform configuration.
/// </summary>
public sealed class GatewayHomeWorldCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-home-world-composition",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddPlatformConfiguration_RegistersHomeWithResolvedWorldIdentity()
    {
        var expectedWorldId = Guid.NewGuid();
        var configPath = WriteConfig(expectedWorldId);
        var services = CreateServices(configPath);

        using var provider = services.BuildServiceProvider();
        var home = provider.GetRequiredService<BotNexusHome>();
        var worldId = provider.GetRequiredService<WorldId>();

        home.WorldId.ShouldBe(worldId.Value);
        home.WorldId.ShouldBe(expectedWorldId.ToString("D"));
        home.RootPath.ShouldBe(Path.GetFullPath(_root));
    }

    [Fact]
    public void AddPlatformConfiguration_ForeignHomeSentinelFailsBeforeGatewayUsesTheHome()
    {
        var expectedWorldId = Guid.NewGuid();
        var foreignWorldId = Guid.NewGuid();
        var configPath = WriteConfig(expectedWorldId);
        File.WriteAllText(
            Path.Combine(_root, HomeWorldSentinel.FileName),
            WorldSentinel.Serialize(foreignWorldId.ToString("D"), "test"));

        var services = CreateServices(configPath);
        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<HomeWorldIdentityMismatchException>(
            () => provider.GetRequiredService<BotNexusHome>());

        exception.ExpectedWorldId.ShouldBe(expectedWorldId.ToString("D"));
        exception.ActualWorldId.ShouldBe(foreignWorldId.ToString("D"));
        exception.HomePath.ShouldBe(Path.GetFullPath(_root));
        exception.Message.ShouldContain(expectedWorldId.ToString("D"));
        exception.Message.ShouldContain(foreignWorldId.ToString("D"));
        exception.Message.ShouldContain(Path.GetFullPath(_root));
    }

    [Fact]
    public void AddPlatformConfiguration_FileSessionStoreOutsideVerifiedHomeIsRefused()
    {
        var foreignSessionsPath = Path.Combine(
            Path.GetTempPath(),
            "botnexus-home-world-foreign-sessions",
            Guid.NewGuid().ToString("N"));
        var configPath = WriteConfig(Guid.NewGuid(), foreignSessionsPath);
        var services = CreateServices(configPath);

        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<HomeScopeViolationException>(
            () => provider.GetRequiredService<ISessionStore>());
        exception.HomePath.ShouldBe(Path.GetFullPath(_root));
    }

    private static ServiceCollection CreateServices(string configPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddPlatformConfiguration(configPath);
        return services;
    }

    private string WriteConfig(Guid worldId, string? fileSessionPath = null)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "config.json");
        var document = new Dictionary<string, object?>
        {
            ["worldId"] = worldId.ToString("D")
        };
        if (fileSessionPath is not null)
        {
            document["gateway"] = new
            {
                sessionStore = new
                {
                    type = "File",
                    filePath = fileSessionPath
                }
            };
        }

        File.WriteAllText(configPath, JsonSerializer.Serialize(document));
        return configPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
