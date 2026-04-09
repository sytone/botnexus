using BotNexus.Extensions.Mcp.Transport;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Hooks;
using BotNexus.Gateway.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Mcp.Tests;

public sealed class McpSecurityTests
{
    private static DefaultToolPolicyProvider CreateProvider(PlatformConfig? config = null)
    {
        config ??= new PlatformConfig();
        return new DefaultToolPolicyProvider(
            Options.Create(config),
            NullLogger<DefaultToolPolicyProvider>.Instance);
    }

    // -- MCP tool risk level defaults --

    [Fact]
    public void McpTool_DefaultsToModerate_WhenServerRegistered()
    {
        var provider = CreateProvider();
        provider.RegisterMcpServerId("github");

        provider.GetRiskLevel("github_search_repositories").Should().Be(ToolRiskLevel.Moderate);
    }

    [Fact]
    public void McpTool_ReturnsSafe_WhenServerNotRegistered()
    {
        var provider = CreateProvider();
        provider.GetRiskLevel("github_search_repositories").Should().Be(ToolRiskLevel.Safe);
    }

    [Theory]
    [InlineData("github_list_repos")]
    [InlineData("github_create_issue")]
    [InlineData("filesystem_read_file")]
    public void McpTool_AllToolsFromRegisteredServer_AreModerate(string toolName)
    {
        var provider = CreateProvider();
        provider.RegisterMcpServerId("github");
        provider.RegisterMcpServerId("filesystem");

        provider.GetRiskLevel(toolName).Should().Be(ToolRiskLevel.Moderate);
    }

    [Fact]
    public void McpTool_BuiltInDangerous_StillDangerous()
    {
        var provider = CreateProvider();
        provider.GetRiskLevel("exec").Should().Be(ToolRiskLevel.Dangerous);
        provider.GetRiskLevel("bash").Should().Be(ToolRiskLevel.Dangerous);
    }

    // -- MCP wildcard server deny --

    [Fact]
    public void IsDenied_WildcardServerPattern_BlocksAllToolsFromServer()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        Denied = ["github_*"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);
        provider.RegisterMcpServerId("github");

        provider.IsDenied("github_search_repositories", "agent-1").Should().BeTrue();
        provider.IsDenied("github_create_issue", "agent-1").Should().BeTrue();
        provider.IsDenied("filesystem_read_file", "agent-1").Should().BeFalse();
    }

    [Fact]
    public void IsDenied_ExactToolName_StillWorks()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        Denied = ["github_delete_repo"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);

        provider.IsDenied("github_delete_repo", "agent-1").Should().BeTrue();
        provider.IsDenied("github_list_repos", "agent-1").Should().BeFalse();
    }

    // -- Hook handler: denied MCP tool --

    [Fact]
    public async Task HookHandler_DeniedMcpTool_ReturnsDenyResult()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        Denied = ["github_*"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);
        provider.RegisterMcpServerId("github");

        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            "agent-1", "github_search_repositories", "tc-1",
            new Dictionary<string, object?> { ["query"] = "test" });

        var result = await handler.HandleAsync(evt);

        result.Should().NotBeNull();
        result!.Denied.Should().BeTrue();
        result.DenyReason.Should().Contain("github_search_repositories");
    }

    // -- IsMcpTool detection --

    [Theory]
    [InlineData("github_search", true)]
    [InlineData("filesystem_read", true)]
    [InlineData("read", false)]
    [InlineData("exec", false)]
    [InlineData("_leading_underscore", false)]
    public void IsMcpTool_DetectsCorrectly(string toolName, bool expected)
    {
        var provider = CreateProvider();
        provider.RegisterMcpServerId("github");
        provider.RegisterMcpServerId("filesystem");

        provider.IsMcpTool(toolName).Should().Be(expected);
    }

    // -- Env var substitution --

    [Fact]
    public void ResolveEnvValue_SubstitutesAtCallTime()
    {
        Environment.SetEnvironmentVariable("MCP_SEC_TEST", "my-secret-123");
        try
        {
            var result = StdioMcpTransport.ResolveEnvValue("${env:MCP_SEC_TEST}");
            result.Should().Be("my-secret-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_SEC_TEST", null);
        }
    }

    [Fact]
    public void ResolveEnvValue_PlainValues_NotSubstituted()
    {
        StdioMcpTransport.ResolveEnvValue("plain-value").Should().Be("plain-value");
    }

    [Fact]
    public void ResolveEnvValue_MissingVar_ReturnsDefault()
    {
        Environment.SetEnvironmentVariable("MCP_DEFINITELY_MISSING", null);
        StdioMcpTransport.ResolveEnvValue("${env:MCP_DEFINITELY_MISSING:-fallback}")
            .Should().Be("fallback");
    }

    // -- Sensitive env var masking --

    [Theory]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("API_KEY", true)]
    [InlineData("APIKEY", true)]
    [InlineData("MY_SECRET", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("AUTH_HEADER", true)]
    [InlineData("CREDENTIAL_PATH", true)]
    [InlineData("NODE_ENV", false)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    [InlineData("LOG_LEVEL", false)]
    public void IsSensitiveEnvKey_ClassifiesCorrectly(string key, bool expected)
    {
        StdioMcpTransport.IsSensitiveEnvKey(key).Should().Be(expected);
    }

    [Fact]
    public void MaskValue_MasksSensitiveValues()
    {
        StdioMcpTransport.MaskValue("GITHUB_TOKEN", "ghp_abc123").Should().Be("***");
        StdioMcpTransport.MaskValue("API_KEY", "sk-12345").Should().Be("***");
    }

    [Fact]
    public void MaskValue_DoesNotMaskNonSensitiveValues()
    {
        StdioMcpTransport.MaskValue("NODE_ENV", "production").Should().Be("production");
        StdioMcpTransport.MaskValue("LOG_LEVEL", "debug").Should().Be("debug");
    }

    // -- InheritEnv config default --

    [Fact]
    public void McpServerConfig_InheritEnv_DefaultsToTrue()
    {
        var config = new McpServerConfig();
        config.InheritEnv.Should().BeTrue();
    }

    [Fact]
    public void McpServerConfig_InheritEnv_CanBeSetToFalse()
    {
        var json = """
        {
            "servers": {
                "github": {
                    "command": "npx",
                    "args": ["-y", "@modelcontextprotocol/server-github"],
                    "env": { "GITHUB_TOKEN": "${env:GITHUB_TOKEN}" },
                    "inheritEnv": false
                }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<McpExtensionConfig>(json);
        config!.Servers["github"].InheritEnv.Should().BeFalse();
    }

    // -- MCP server registration --

    [Fact]
    public void RegisterMcpServerId_CanRegisterMultipleServers()
    {
        var provider = CreateProvider();
        provider.RegisterMcpServerId("github");
        provider.RegisterMcpServerId("filesystem");
        provider.RegisterMcpServerId("database");

        provider.McpServerIds.Should().HaveCount(3);
        provider.McpServerIds.Should().Contain("github");
        provider.McpServerIds.Should().Contain("filesystem");
        provider.McpServerIds.Should().Contain("database");
    }

    [Fact]
    public void RegisterMcpServerId_DuplicateIsIgnored()
    {
        var provider = CreateProvider();
        provider.RegisterMcpServerId("github");
        provider.RegisterMcpServerId("github");

        provider.McpServerIds.Should().HaveCount(1);
    }
}
