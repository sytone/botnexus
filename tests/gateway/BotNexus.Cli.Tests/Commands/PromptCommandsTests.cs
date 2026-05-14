using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests.Commands;

public sealed class PromptCommandsTests
{
    [Fact]
    public void TryParseParameters_ParsesKeyValuePairs()
    {
        var ok = PromptCommands.TryParseParameters(
            ["owner=Hermes", "project=botnexus"],
            out var parameters,
            out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        parameters["owner"].ShouldBe("Hermes");
        parameters["project"].ShouldBe("botnexus");
    }

    [Fact]
    public void TryParseParameters_RejectsInvalidFormat()
    {
        var ok = PromptCommands.TryParseParameters(["invalid"], out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("Use --param key=value");
    }

    [Fact]
    public async Task ExecuteRenderAsync_RendersTemplateFromConfig()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var configPath = Path.Combine(tempHome, "config.json");

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            },
            PromptTemplates = new Dictionary<string, PromptTemplateConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["daily-status"] = new()
                {
                    Prompt = "Status for {{project}} by {{owner}}",
                    Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["project"] = "BotNexus"
                    }
                }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        try
        {
            var command = new PromptCommands();
            var writer = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(writer);
            var result = await command.ExecuteRenderAsync(
                configPath,
                "agent-a",
                "daily-status",
                ["owner=Hermes"],
                verbose: false,
                runMode: false,
                CancellationToken.None);
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            writer.ToString().ShouldContain("Status for BotNexus by Hermes");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteListAsync_ListsConfigAndSharedFileTemplates()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "shared-template.prompt.json"),
            """
            {
              "name": "shared-template",
              "prompt": "Shared {{name}}"
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-template.prompt.md"),
            """
            ---
            name: status-template
            ---
            # Status for {{name}}
            """);

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            },
            PromptTemplates = new Dictionary<string, PromptTemplateConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["config-template"] = new()
                {
                    Prompt = "Config {{name}}"
                }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var command = new PromptCommands();
            var result = await command.ExecuteListAsync(
                configPath,
                agentId: null,
                verbose: false,
                CancellationToken.None);
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            writer.ToString().ShouldContain("config-template");
            writer.ToString().ShouldContain("shared-template");
            writer.ToString().ShouldContain("status-template");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteRenderAsync_RendersMarkdownTemplateWithMultilineFormatting()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-md-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");

        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-report.prompt.md"),
            """
            ---
            name: status-report
            parameters:
              owner:
                default: team@example.com
            ---
            # Weekly Status: {{project}}

            ## Details

            - Owner: {{owner}}
            - Summary: {{summary}}

            1. Accomplishments
            2. Risks
            """);

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var command = new PromptCommands();
            var result = await command.ExecuteRenderAsync(
                configPath,
                "agent-a",
                "status-report",
                ["project=BotNexus", "summary=All tests passed"],
                verbose: false,
                runMode: false,
                CancellationToken.None);
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            writer.ToString().ShouldContain("# Weekly Status: BotNexus");
            writer.ToString().ShouldContain("- Owner: team@example.com");
            writer.ToString().ShouldContain("1. Accomplishments");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteRunAsync_RendersMarkdownTemplateAndPostsRenderedPrompt()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-md-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-report.prompt.md"),
            """
            ---
            name: status-report
            parameters:
              owner:
                default: team@example.com
            ---
            # Weekly Status: {{project}}

            - Owner: {{owner}}
            - Summary: {{summary}}
            """);

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        string? capturedBody = null;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            capturedBody = await reader.ReadToEndAsync();

            var responseJson = """{"sessionId":"session-1","content":"gateway reply"}""";
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
        });

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var command = new PromptCommands();
            var result = await command.ExecuteRunAsync(
                configPath,
                "agent-a",
                "status-report",
                ["project=BotNexus", "summary=All tests passed"],
                sessionId: "session-1",
                gatewayUrlOverride: $"http://127.0.0.1:{port}",
                verbose: false,
                CancellationToken.None);

            await serverTask;
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            capturedBody.ShouldNotBeNull();
            var payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(capturedBody!);
            payload.ShouldNotBeNull();
            payload["agentId"].ShouldBe("agent-a");
            payload["sessionId"].ShouldBe("session-1");
            var renderedMessage = payload["message"]!.Replace("\r\n", "\n");
            renderedMessage.ShouldContain("# Weekly Status: BotNexus");
            renderedMessage.ShouldContain("- Owner: team@example.com");
            renderedMessage.ShouldContain("- Summary: All tests passed");
            writer.ToString().ShouldContain("gateway reply");
        }
        finally
        {
            listener.Stop();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }
}
