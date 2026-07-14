using BotNexus.Cli.Commands;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Tests for <c>botnexus agent import</c>: reconstructing an agent from a redacted
/// <c>agentTemplate/v1</c> template, applying <c>--set</c> overrides, and refusing to
/// silently overwrite an existing agent.
/// </summary>
public sealed class AgentImportCommandTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _configPath;

    private const string EmptyConfig = """
        {
          "providers": {
            "copilot": { "apiKey": "sk-secret" }
          },
          "agents": {}
        }
        """;

    private const string ConfigWithExisting = """
        {
          "providers": {
            "copilot": { "apiKey": "sk-secret" }
          },
          "agents": {
            "assistant": {
              "provider": "copilot",
              "model": "gpt-4.1",
              "displayName": "Existing"
            }
          }
        }
        """;

    public AgentImportCommandTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
        File.WriteAllText(_configPath, EmptyConfig);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private string WriteTemplate(string fileName = "assistant.agent.json")
    {
        var template = new AgentTemplate
        {
            Agent = new AgentTemplateDescriptor
            {
                DisplayName = "Assistant",
                Description = "A helpful assistant.",
                Emoji = "\ud83e\udd16",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                SystemPrompt = "You are a helpful assistant.",
                ToolIds = ["read", "write"],
                ContextWindow = 128000
            }
        };
        var path = Path.Combine(_rootPath, fileName);
        File.WriteAllText(path, template.ToJson());
        return path;
    }

    private static async Task<AgentDefinitionConfig?> LoadAgentAsync(string configPath, string id)
    {
        var config = await PlatformConfigLoader.LoadAsync(configPath, CancellationToken.None, validateOnLoad: false);
        if (config.Agents is null)
            return null;
        return config.Agents.TryGetValue(id, out var agent) ? agent : null;
    }

    [Fact]
    public async Task Import_IntoCleanEnvironment_CreatesAgentFromTemplate()
    {
        var templatePath = WriteTemplate();

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            templatePath, _configPath, idOverride: "assistant", sets: [], overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        var agent = await LoadAgentAsync(_configPath, "assistant");
        agent.ShouldNotBeNull();
        agent!.Provider.ShouldBe("copilot");
        agent.Model.ShouldBe("gpt-4.1");
        agent.DisplayName.ShouldBe("Assistant");
        agent.Description.ShouldBe("A helpful assistant.");
        agent.ContextWindow.ShouldBe(128000);
        agent.ToolIds.ShouldNotBeNull();
        agent.ToolIds!.ShouldContain("read");
    }

    [Fact]
    public async Task Import_WithSetOverrides_SupersedeTemplateValues()
    {
        var templatePath = WriteTemplate();

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            templatePath, _configPath, idOverride: null,
            sets: ["id=copybot", "displayName=Copy Bot", "model=gpt-5", "contextWindow=64000"],
            overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        (await LoadAgentAsync(_configPath, "assistant")).ShouldBeNull();
        var agent = await LoadAgentAsync(_configPath, "copybot");
        agent.ShouldNotBeNull();
        agent!.DisplayName.ShouldBe("Copy Bot");
        agent.Model.ShouldBe("gpt-5");
        agent.ContextWindow.ShouldBe(64000);
    }

    [Fact]
    public async Task Import_PersistsSystemPromptToFile()
    {
        var templatePath = WriteTemplate();

        var commands = new AgentCommands();
        await commands.ExecuteImportAsync(
            templatePath, _configPath, idOverride: "assistant", sets: [], overwrite: false, verbose: false, CancellationToken.None);

        var agent = await LoadAgentAsync(_configPath, "assistant");
        agent.ShouldNotBeNull();
        agent!.SystemPromptFile.ShouldNotBeNullOrWhiteSpace();
        var promptPath = Path.IsPathRooted(agent.SystemPromptFile!)
            ? agent.SystemPromptFile!
            : Path.Combine(_rootPath, agent.SystemPromptFile!);
        File.Exists(promptPath).ShouldBeTrue();
        (await File.ReadAllTextAsync(promptPath)).ShouldContain("You are a helpful assistant.");
    }

    [Fact]
    public async Task Import_ExistingAgentWithoutOverwrite_ReturnsOneAndDoesNotClobber()
    {
        File.WriteAllText(_configPath, ConfigWithExisting);
        var templatePath = WriteTemplate();

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            templatePath, _configPath, idOverride: "assistant", sets: [], overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
        var agent = await LoadAgentAsync(_configPath, "assistant");
        agent.ShouldNotBeNull();
        // Original untouched.
        agent!.DisplayName.ShouldBe("Existing");
    }

    [Fact]
    public async Task Import_ExistingAgentWithOverwrite_ReplacesDefinition()
    {
        File.WriteAllText(_configPath, ConfigWithExisting);
        var templatePath = WriteTemplate();

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            templatePath, _configPath, idOverride: "assistant", sets: [], overwrite: true, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        var agent = await LoadAgentAsync(_configPath, "assistant");
        agent.ShouldNotBeNull();
        agent!.DisplayName.ShouldBe("Assistant");
    }

    [Fact]
    public async Task Import_MissingTemplateFile_ReturnsOne()
    {
        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            Path.Combine(_rootPath, "nope.agent.json"), _configPath, idOverride: "x", sets: [], overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Import_MalformedTemplate_ReturnsOne()
    {
        var badPath = Path.Combine(_rootPath, "bad.agent.json");
        File.WriteAllText(badPath, "{ not valid json ");

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            badPath, _configPath, idOverride: "x", sets: [], overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Import_SchemaValidationFailure_ReturnsOne()
    {
        // Template missing required modelId / apiProvider.
        var template = new AgentTemplate { Agent = new AgentTemplateDescriptor { DisplayName = "Broken" } };
        var badPath = Path.Combine(_rootPath, "broken.agent.json");
        File.WriteAllText(badPath, template.ToJson());

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            badPath, _configPath, idOverride: "broken", sets: [], overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Import_NoTargetId_DerivesFromTemplateFileName()
    {
        var templatePath = WriteTemplate("mybot.agent.json");

        var commands = new AgentCommands();
        var exitCode = await commands.ExecuteImportAsync(
            templatePath, _configPath, idOverride: null, sets: [], overwrite: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        (await LoadAgentAsync(_configPath, "mybot")).ShouldNotBeNull();
    }
}
