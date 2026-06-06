using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// Tests for McpServerConfig.Auth field and McpToolContributor auth resolution logic.
/// </summary>
public sealed class McpAuthReferenceTests
{
    // --- helpers ---

    private static AgentDescriptor BuildDescriptor(McpExtensionConfig config)
    {
        var element = JsonSerializer.SerializeToElement(config, JsonContext.Default.McpExtensionConfig);
        return new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["botnexus-mcp"] = element,
            },
        };
    }

    private static AgentToolContributionContext BuildContext(
        AgentDescriptor descriptor,
        Func<string, CancellationToken, Task<string?>>? getApiKey = null)
        => new(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.GetTempPath(),
            new AllowAllPathValidator(),
            _ => null,
            getApiKey ?? ((_, _) => Task.FromResult<string?>(null)));

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }

    // --- McpServerConfig.Auth deserialization ---

    [Fact]
    public void McpServerConfig_DeserializesAuth_FromJson()
    {
        var json = """
        {
            "servers": {
                "github": {
                    "url": "https://api.githubcopilot.com/mcp/",
                    "auth": "github-copilot"
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json)!;

        config.Servers["github"].Auth.ShouldBe("github-copilot");
        config.Servers["github"].Url.ShouldBe("https://api.githubcopilot.com/mcp/");
    }

    [Fact]
    public void McpServerConfig_Auth_DefaultsToNull()
    {
        var config = new McpServerConfig();
        config.Auth.ShouldBeNull();
    }

    [Fact]
    public void McpServerConfig_Auth_SerializesAsJsonProperty()
    {
        var srv = new McpServerConfig { Url = "http://example.com/mcp", Auth = "my-provider" };
        var json = JsonSerializer.Serialize(srv);
        json.ShouldContain("\"auth\"");
        json.ShouldContain("my-provider");
    }

    // --- McpToolContributor auth routing ---

    [Fact]
    public async Task ContributeAsync_AuthServer_NullToken_SkipsServer_ReturnsNoTools()
    {
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["copilot-mcp"] = new McpServerConfig
                {
                    Url = "https://api.githubcopilot.com/mcp/",
                    Auth = "github-copilot",
                },
            },
        };

        var descriptor = BuildDescriptor(config);
        var context = BuildContext(descriptor, (_, _) => Task.FromResult<string?>(null));

        var contributor = new McpToolContributor(NullLoggerFactory.Instance);
        var contribution = await contributor.ContributeAsync(context);

        contribution.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ContributeAsync_AuthServer_ResolvesProviderKeyFromContext()
    {
        string? resolvedProvider = null;
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["copilot-mcp"] = new McpServerConfig
                {
                    Url = "https://example-mcp.invalid/mcp",
                    Auth = "github-copilot",
                },
            },
        };

        var descriptor = BuildDescriptor(config);
        var context = BuildContext(descriptor, (providerKey, _) =>
        {
            resolvedProvider = providerKey;
            return Task.FromResult<string?>("test-bearer-token");
        });

        var contributor = new McpToolContributor(NullLoggerFactory.Instance);
        await contributor.ContributeAsync(context, CancellationToken.None);

        // The auth provider key should have been passed to GetProviderApiKeyAsync
        resolvedProvider.ShouldBe("github-copilot");
    }

    [Fact]
    public void ContributeAsync_ExplicitHeaderAuthorization_RoundTripsCorrectly()
    {
        // Verify that config with both auth and explicit Authorization header round-trips via descriptor
        var json = """
        {
            "servers": {
                "srv": {
                    "url": "https://example.com/mcp",
                    "auth": "my-provider",
                    "headers": { "Authorization": "Bearer explicit-token" }
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json)!;

        config.Servers["srv"].Auth.ShouldBe("my-provider");
        config.Servers["srv"].Headers!["Authorization"].ShouldBe("Bearer explicit-token");

        // Confirm the descriptor round-trips correctly through ResolveMcpExtensionConfig
        var descriptor = BuildDescriptor(config);
        var resolved = McpToolContributor.ResolveMcpExtensionConfig(descriptor)!;
        resolved.Servers["srv"].Auth.ShouldBe("my-provider");
        resolved.Servers["srv"].Headers!["Authorization"].ShouldBe("Bearer explicit-token");
    }

    [Fact]
    public async Task ContributeAsync_NoAuthServers_DoesNotCallGetProviderApiKeyAsync()
    {
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["local"] = new McpServerConfig { Command = "true" }, // stdio, no auth
            },
        };

        var tokenCallCount = 0;
        var descriptor = BuildDescriptor(config);
        var context = BuildContext(descriptor, (_, _) =>
        {
            tokenCallCount++;
            return Task.FromResult<string?>("should-not-be-called");
        });

        var contributor = new McpToolContributor(NullLoggerFactory.Instance);
        await contributor.ContributeAsync(context, CancellationToken.None);

        tokenCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ContributeAsync_StdioServerWithAuthField_IsRoutedThroughWarmupCache()
    {
        // Auth is only meaningful for HTTP/SSE servers (with Url set).
        // A stdio server (command only) with auth= set is silently treated as a non-auth server.
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["stdio-with-auth"] = new McpServerConfig
                {
                    Command = "echo",
                    Auth = "github-copilot",
                },
            },
        };

        var tokenCallCount = 0;
        var descriptor = BuildDescriptor(config);
        var context = BuildContext(descriptor, (_, _) =>
        {
            tokenCallCount++;
            return Task.FromResult<string?>("ignored");
        });

        var contributor = new McpToolContributor(NullLoggerFactory.Instance);
        await contributor.ContributeAsync(context, CancellationToken.None);

        // Token resolver not called — stdio servers bypass the auth path
        tokenCallCount.ShouldBe(0);
    }
}
