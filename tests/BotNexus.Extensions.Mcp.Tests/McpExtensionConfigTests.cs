using System.Text.Json;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests;

public class McpExtensionConfigTests
{
    [Fact]
    public void Deserializes_FromJson()
    {
        var json = """
        {
            "servers": {
                "github": {
                    "command": "npx",
                    "args": ["-y", "@modelcontextprotocol/server-github"],
                    "env": {
                        "GITHUB_TOKEN": "${env:GITHUB_TOKEN}"
                    }
                },
                "filesystem": {
                    "command": "node",
                    "args": ["server.js"],
                    "workingDirectory": "/opt/mcp"
                }
            },
            "toolPrefix": true
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config.Should().NotBeNull();
        config!.Servers.Should().HaveCount(2);
        config.ToolPrefix.Should().BeTrue();

        config.Servers["github"].Command.Should().Be("npx");
        config.Servers["github"].Args.Should().Contain("-y");
        config.Servers["github"].Env.Should().ContainKey("GITHUB_TOKEN");

        config.Servers["filesystem"].Command.Should().Be("node");
        config.Servers["filesystem"].WorkingDirectory.Should().Be("/opt/mcp");
    }

    [Fact]
    public void Defaults_ToolPrefixToTrue()
    {
        var config = new McpExtensionConfig();
        config.ToolPrefix.Should().BeTrue();
    }

    [Fact]
    public void Defaults_Timeouts()
    {
        var serverConfig = new McpServerConfig();
        serverConfig.InitTimeoutMs.Should().Be(30_000);
        serverConfig.CallTimeoutMs.Should().Be(60_000);
    }

    [Fact]
    public void Deserializes_WithCustomTimeouts()
    {
        var json = """
        {
            "servers": {
                "slow-server": {
                    "command": "python",
                    "args": ["server.py"],
                    "initTimeoutMs": 60000,
                    "callTimeoutMs": 120000
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config!.Servers["slow-server"].InitTimeoutMs.Should().Be(60_000);
        config.Servers["slow-server"].CallTimeoutMs.Should().Be(120_000);
    }
}
