using BotNexus.Cron.Prompts;
using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Cron.Tests;

public sealed class CronOptionsPromptTemplateResolverTests
{
    [Fact]
    public void ListTemplateNames_ReturnsCaseInsensitiveSortedNames()
    {
        var resolver = CreateResolver(new CronOptions
        {
            PromptTemplates = new Dictionary<string, ConfiguredPromptTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                ["zebra"] = new() { Prompt = "Z" },
                ["Alpha"] = new() { Prompt = "A" }
            }
        });

        resolver.ListTemplateNames("farnsworth").ShouldBe(["Alpha", "zebra"]);
    }

    [Fact]
    public void TryRender_ReturnsErrorForUnknownTemplate()
    {
        var resolver = CreateResolver(new CronOptions());

        var ok = resolver.TryRender("farnsworth", "does-not-exist", null, out var rendered, out var error);

        ok.ShouldBeFalse();
        rendered.ShouldBeEmpty();
        error.ShouldBe("Prompt template 'does-not-exist' was not found.");
    }

    [Fact]
    public void TryRender_LoadsTemplateAndRendersWithDefaults()
    {
        var resolver = CreateResolver(new CronOptions
        {
            PromptTemplates = new Dictionary<string, ConfiguredPromptTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                ["daily-status"] = new()
                {
                    Prompt = "Status for {{project}} by {{owner}}",
                    Defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["project"] = "BotNexus"
                    }
                }
            }
        });

        var ok = resolver.TryRender(
            "farnsworth",
            "DAILY-STATUS",
            new Dictionary<string, string?> { ["owner"] = "Hermes" },
            out var rendered,
            out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        rendered.ShouldBe("Status for BotNexus by Hermes");
    }

    [Fact]
    public void TryRender_WorkspaceTemplate_OverridesSharedAndOptions()
    {
        var fileSystem = new MockFileSystem();
        const string homePath = @"C:\users\test\.botnexus";
        const string workspacePath = @"C:\users\test\.botnexus\agents\farnsworth\workspace";
        fileSystem.Directory.CreateDirectory(Path.Combine(homePath, "prompts"));
        fileSystem.Directory.CreateDirectory(Path.Combine(homePath, "agents", "farnsworth", "prompts"));
        fileSystem.Directory.CreateDirectory(Path.Combine(workspacePath, "prompts"));

        fileSystem.File.WriteAllText(Path.Combine(homePath, "prompts", "daily-status.prompt.json"), """{"name":"daily-status","prompt":"shared"}""");
        fileSystem.File.WriteAllText(Path.Combine(homePath, "agents", "farnsworth", "prompts", "daily-status.prompt.json"), """{"name":"daily-status","prompt":"agent"}""");
        fileSystem.File.WriteAllText(Path.Combine(workspacePath, "prompts", "daily-status.prompt.json"), """{"name":"daily-status","prompt":"workspace {{owner}}"}""");

        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        try
        {
            var resolver = CreateResolver(
                new CronOptions
                {
                    PromptTemplates = new Dictionary<string, ConfiguredPromptTemplate>
                    {
                        ["daily-status"] = new() { Prompt = "options {{owner}}" }
                    }
                },
                fileSystem,
                new StubWorkspaceManager(workspacePath));

            var ok = resolver.TryRender("farnsworth", "daily-status", new Dictionary<string, string?> { ["owner"] = "Hermes" }, out var rendered, out var error);

            ok.ShouldBeTrue();
            error.ShouldBeNull();
            rendered.ShouldBe("workspace Hermes");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", null);
        }
    }

    private static IPromptTemplateResolver CreateResolver(
        CronOptions options,
        MockFileSystem? fileSystem = null,
        IAgentWorkspaceManager? workspaceManager = null)
        => new CronOptionsPromptTemplateResolver(
            new StaticOptionsMonitor<CronOptions>(options),
            workspaceManager,
            fileSystem);

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => workspacePath;
    }
}
