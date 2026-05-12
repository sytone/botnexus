using System.Text.Json;

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

        config.ShouldNotBeNull();
        var extensionConfig = config ?? throw new InvalidOperationException("Expected MCP config.");
        extensionConfig.Servers.Count().ShouldBe(2);
        extensionConfig.ToolPrefix.ShouldBeTrue();

        extensionConfig.Servers["github"].Command.ShouldBe("npx");
        extensionConfig.Servers["github"].Args.ShouldNotBeNull();
        var githubArgs = extensionConfig.Servers["github"].Args ?? throw new InvalidOperationException("Expected github args.");
        githubArgs.ShouldContain("-y");
        extensionConfig.Servers["github"].Env.ShouldNotBeNull();
        var githubEnv = extensionConfig.Servers["github"].Env ?? throw new InvalidOperationException("Expected github env.");
        githubEnv.ShouldContainKey("GITHUB_TOKEN");

        extensionConfig.Servers["filesystem"].Command.ShouldBe("node");
        extensionConfig.Servers["filesystem"].WorkingDirectory.ShouldBe("/opt/mcp");
    }

    [Fact]
    public void Defaults_ToolPrefixToTrue()
    {
        var config = new McpExtensionConfig();
        config.ToolPrefix.ShouldBeTrue();
    }

    [Fact]
    public void Defaults_Timeouts()
    {
        var serverConfig = new McpServerConfig();
        serverConfig.InitTimeoutMs.ShouldBe(30_000);
        serverConfig.CallTimeoutMs.ShouldBe(60_000);
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

        config.ShouldNotBeNull();
        var extensionConfig = config ?? throw new InvalidOperationException("Expected MCP config.");
        extensionConfig.Servers["slow-server"].InitTimeoutMs.ShouldBe(60_000);
        extensionConfig.Servers["slow-server"].CallTimeoutMs.ShouldBe(120_000);
    }

    [Fact]
    public void Deserializes_HttpServerConfig()
    {
        var json = """
        {
            "servers": {
                "remote": {
                    "url": "http://localhost:3000/mcp",
                    "headers": {
                        "Authorization": "Bearer my-token"
                    }
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config.ShouldNotBeNull();
        var extensionConfig = config ?? throw new InvalidOperationException("Expected MCP config.");
        extensionConfig.Servers["remote"].Url.ShouldBe("http://localhost:3000/mcp");
        extensionConfig.Servers["remote"].Headers.ShouldNotBeNull();
        var headers = extensionConfig.Servers["remote"].Headers ?? throw new InvalidOperationException("Expected headers.");
        headers.ShouldContainKey("Authorization");
        headers["Authorization"].ShouldBe("Bearer my-token");
        extensionConfig.Servers["remote"].Command.ShouldBeNull();
    }

    [Fact]
    public void Deserializes_MixedTransportConfig()
    {
        var json = """
        {
            "servers": {
                "local": {
                    "command": "npx",
                    "args": ["-y", "@modelcontextprotocol/server-github"]
                },
                "remote": {
                    "url": "http://remote-host:8080/mcp"
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config.ShouldNotBeNull();
        var extensionConfig = config ?? throw new InvalidOperationException("Expected MCP config.");
        extensionConfig.Servers.Count().ShouldBe(2);
        extensionConfig.Servers["local"].Command.ShouldBe("npx");
        extensionConfig.Servers["local"].Url.ShouldBeNull();
        extensionConfig.Servers["remote"].Url.ShouldBe("http://remote-host:8080/mcp");
        extensionConfig.Servers["remote"].Command.ShouldBeNull();
    }

    [Fact]
    public void Deserializes_EmptyServers()
    {
        var json = """{ "servers": {} }""";

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config.ShouldNotBeNull();
        config!.Servers.ShouldBeEmpty();
    }

    [Fact]
    public void Deserializes_MinimalServerConfig()
    {
        var json = """
        {
            "servers": {
                "simple": {
                    "command": "echo"
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config!.Servers["simple"].Command.ShouldBe("echo");
        config.Servers["simple"].Args.ShouldBeNull();
        config.Servers["simple"].Env.ShouldBeNull();
        config.Servers["simple"].WorkingDirectory.ShouldBeNull();
        config.Servers["simple"].Url.ShouldBeNull();
        config.Servers["simple"].Headers.ShouldBeNull();
    }

    [Fact]
    public void Deserializes_WithBothCommandAndUrl()
    {
        var json = """
        {
            "servers": {
                "both": {
                    "command": "node",
                    "args": ["server.js"],
                    "url": "http://localhost:3000/mcp"
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        // Both fields are deserialized; runtime decides precedence (command wins in Phase 1-2)
        config!.Servers["both"].Command.ShouldBe("node");
        config.Servers["both"].Url.ShouldBe("http://localhost:3000/mcp");
    }

    [Fact]
    public void Defaults_ServersToEmptyDictionary()
    {
        var config = new McpExtensionConfig();
        config.Servers.ShouldNotBeNull();
        config.Servers.ShouldBeEmpty();
    }

    [Fact]
    public void ServerConfig_Defaults_AllOptionalFields()
    {
        var config = new McpServerConfig();

        config.Command.ShouldBeNull();
        config.Args.ShouldBeNull();
        config.Env.ShouldBeNull();
        config.WorkingDirectory.ShouldBeNull();
        config.Url.ShouldBeNull();
        config.Headers.ShouldBeNull();
        config.InitTimeoutMs.ShouldBe(30_000);
        config.CallTimeoutMs.ShouldBe(60_000);
    }

    [Fact]
    public void Deserializes_ToolPrefixFalse()
    {
        var json = """
        {
            "servers": {},
            "toolPrefix": false
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config.ShouldNotBeNull();
        var extensionConfig = config ?? throw new InvalidOperationException("Expected MCP config.");
        extensionConfig.ToolPrefix.ShouldBeFalse();
    }

    [Fact]
    public void Deserializes_MultipleEnvVars()
    {
        var json = """
        {
            "servers": {
                "srv": {
                    "command": "node",
                    "env": {
                        "TOKEN": "${env:MY_TOKEN}",
                        "API_KEY": "${env:API_KEY:-default-key}",
                        "PLAIN": "literal-value"
                    }
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<McpExtensionConfig>(json);

        config.ShouldNotBeNull();
        var extensionConfig = config ?? throw new InvalidOperationException("Expected MCP config.");
        extensionConfig.Servers["srv"].Env.ShouldNotBeNull();
        var env = extensionConfig.Servers["srv"].Env ?? throw new InvalidOperationException("Expected env values.");
        env.Count().ShouldBe(3);
        env["TOKEN"].ShouldBe("${env:MY_TOKEN}");
        env["API_KEY"].ShouldBe("${env:API_KEY:-default-key}");
        env["PLAIN"].ShouldBe("literal-value");
    }
}
