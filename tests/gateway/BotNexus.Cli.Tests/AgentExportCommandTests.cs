using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests;

public sealed class AgentExportCommandTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _configPath;

    private const string SampleConfig = """
        {
          "providers": {
            "copilot": { "apiKey": "sk-super-secret-token-DO-NOT-LEAK" }
          },
          "agents": {
            "assistant": {
              "provider": "copilot",
              "model": "gpt-4.1",
              "displayName": "Assistant",
              "description": "A helpful assistant.",
              "emoji": "\ud83e\udd16",
              "toolIds": ["read", "write"],
              "contextWindow": 128000
            }
          }
        }
        """;

    public AgentExportCommandTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
        File.WriteAllText(_configPath, SampleConfig);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    [Fact]
    public async Task Export_ExistingAgent_WritesValidAgentTemplateV1()
    {
        var outputPath = Path.Combine(_rootPath, "assistant.agent.json");

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteExportAsync("assistant", _configPath, outputPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        File.Exists(outputPath).ShouldBeTrue();

        var json = await File.ReadAllTextAsync(outputPath);
        var template = AgentTemplate.FromJson(json);
        template.ShouldNotBeNull();
        template!.Schema.ShouldBe("agentTemplate/v1");
        template.Agent.ModelId.ShouldBe("gpt-4.1");
        template.Agent.ApiProvider.ShouldBe("copilot");
        template.Agent.DisplayName.ShouldBe("Assistant");
        template.Agent.Description.ShouldBe("A helpful assistant.");
        template.Agent.ContextWindow.ShouldBe(128000);
        template.Agent.ToolIds.ShouldNotBeNull();
        template.Agent.ToolIds!.ShouldContain("read");

        // Round-trip schema validation must pass.
        template.Validate().ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_PopulatesRequiredSecretsManifest()
    {
        var outputPath = Path.Combine(_rootPath, "assistant.agent.json");

        var commands = new AgentCommands();
        await commands.ExecuteExportAsync("assistant", _configPath, outputPath, verbose: false, CancellationToken.None);

        var template = AgentTemplate.FromJson(await File.ReadAllTextAsync(outputPath))!;
        template.RequiredSecrets.ShouldNotBeEmpty();
        template.RequiredSecrets.ShouldContain(s => s.Provider == "copilot" && s.Key == "apiKey");
    }

    [Fact]
    public async Task Export_UnknownAgent_ReturnsOneWithClearError()
    {
        var outputPath = Path.Combine(_rootPath, "missing.agent.json");

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteExportAsync("does-not-exist", _configPath, outputPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
        File.Exists(outputPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_ContainsNoSecretSubstrings()
    {
        var outputPath = Path.Combine(_rootPath, "assistant.agent.json");

        var commands = new AgentCommands();
        await commands.ExecuteExportAsync("assistant", _configPath, outputPath, verbose: false, CancellationToken.None);

        var json = await File.ReadAllTextAsync(outputPath);
        json.ShouldNotContain("sk-super-secret-token-DO-NOT-LEAK");
        json.ShouldNotContain("apiKey\": \"sk-");
    }
}
