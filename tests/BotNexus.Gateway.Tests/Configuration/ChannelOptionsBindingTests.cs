using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Verifies that channels.telegram in config.json is bound to TelegramGatewayOptions
/// when the Telegram extension assembly is loaded.
/// </summary>
public sealed class ChannelOptionsBindingTests
{
    [Fact]
    public void BindChannelOptions_TelegramSection_BindsTelegramGatewayOptions()
    {
        // Arrange: config with channels.telegram containing botToken + agentId
        const string configJson = """
            {
              "version": 1,
              "channels": {
                "telegram": {
                  "botToken": "123:ABC",
                  "agentId": "larry",
                  "allowedChatIds": [5067802539],
                  "pollingTimeoutSeconds": 60
                }
              }
            }
            """;

        var configPath = "/home/test/.botnexus/config.json";
        var fs = new MockFileSystem();
        fs.AddFile(configPath, new MockFileData(configJson));

        // Force the Telegram assembly to be loaded so Type.GetType can resolve it
        var telegramType = typeof(BotNexus.Extensions.Channels.Telegram.TelegramGatewayOptions);
        Assert.NotNull(telegramType); // keeps reference alive

        var services = new ServiceCollection();

        // Act: call BindChannelOptions via AddPlatformConfiguration which invokes it
        // We test it indirectly by calling the private method via the public surface
        services.AddBotNexusGateway();
        // Use reflection to call BindChannelOptions directly
        var method = typeof(GatewayServiceCollectionExtensions)
            .GetMethod("BindChannelOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [services, configPath, fs]);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetService<IOptions<BotNexus.Extensions.Channels.Telegram.TelegramGatewayOptions>>();

        // Assert
        Assert.NotNull(opts);
        Assert.Equal("123:ABC", opts.Value.BotToken);
        Assert.Equal("larry", opts.Value.AgentId);
        Assert.Equal(60, opts.Value.PollingTimeoutSeconds);
    }

    [Fact]
    public void BindChannelOptions_NoChannelsSection_IsNoOp()
    {
        const string configJson = """{ "version": 1 }""";
        var configPath = "/home/test/.botnexus/config.json";
        var fs = new MockFileSystem();
        fs.AddFile(configPath, new MockFileData(configJson));

        var services = new ServiceCollection();
        var method = typeof(GatewayServiceCollectionExtensions)
            .GetMethod("BindChannelOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Should not throw
        method.Invoke(null, [services, configPath, fs]);
    }

    [Fact]
    public void BindChannelOptions_MissingConfigFile_IsNoOp()
    {
        var fs = new MockFileSystem();
        var services = new ServiceCollection();
        var method = typeof(GatewayServiceCollectionExtensions)
            .GetMethod("BindChannelOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Should not throw
        method.Invoke(null, [services, "/nonexistent/config.json", fs]);
    }
}
