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
            var result = await command.ExecuteRenderAsync(
                configPath,
                "agent-a",
                "daily-status",
                ["owner=Hermes"],
                verbose: false,
                runMode: false,
                CancellationToken.None);

            result.ShouldBe(0);
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }
}
