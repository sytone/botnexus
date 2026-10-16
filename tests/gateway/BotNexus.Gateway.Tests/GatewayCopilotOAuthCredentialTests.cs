using BotNexus.Agent.Providers.Core;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayCopilotOAuthCredentialTests
{
    [Fact]
    public async Task GetCopilotOAuthTokenAsync_ReturnsRefreshOAuthTokenInsteadOfSessionAccessToken()
    {
        var fileSystem = new MockFileSystem();
        var authPath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem), "auth.json");
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(authPath) ?? throw new InvalidOperationException("Auth path has no directory."));
        await fileSystem.File.WriteAllTextAsync(authPath, """
            {
              "github-copilot": {
                "type": "oauth",
                "refresh": "ghu_github_oauth_token",
                "access": "tid_copilot_session_token",
                "expires": 4102444800000,
                "endpoint": "https://api.githubcopilot.com"
              }
            }
            """);
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig());
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fileSystem);

        var token = await auth.GetCopilotOAuthTokenAsync();

        token.ShouldBe("ghu_github_oauth_token");
        (await auth.GetApiKeyAsync("copilot")).ShouldBe("tid_copilot_session_token");
    }

    [Fact]
    public async Task GetCopilotOAuthTokenAsync_ReturnsNullWhenCopilotAuthEntryDoesNotExist()
    {
        var fileSystem = new MockFileSystem();
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig());
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fileSystem);

        var token = await auth.GetCopilotOAuthTokenAsync();

        token.ShouldBeNull();
    }
}
