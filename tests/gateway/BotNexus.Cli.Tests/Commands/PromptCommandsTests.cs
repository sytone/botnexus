using System.Text.Json;
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
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }
}
