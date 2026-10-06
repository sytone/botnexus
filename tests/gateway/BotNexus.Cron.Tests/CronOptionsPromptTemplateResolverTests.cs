using BotNexus.Cron.Prompts;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Cron.Tests;

public sealed class CronOptionsPromptTemplateResolverTests
{

    [Fact]
    public void ListTemplates_ProjectsEffectiveMetadataAndPrecedenceWithoutBodiesOrPaths()
    {
        var fileSystem = new MockFileSystem();
        const string homePath = @"C:\users\test\.botnexus";
        const string workspacePath = @"C:\users\test\.botnexus\agents\farnsworth\workspace";
        fileSystem.Directory.CreateDirectory(Path.Combine(homePath, "prompts"));
        fileSystem.Directory.CreateDirectory(Path.Combine(homePath, "agents", "farnsworth", "prompts"));
        fileSystem.Directory.CreateDirectory(Path.Combine(workspacePath, "prompts"));
        fileSystem.File.WriteAllText(Path.Combine(homePath, "prompts", "daily.prompt.json"),
            """{"name":"daily","description":"shared description","prompt":"shared {{topic}}"}""");
        fileSystem.File.WriteAllText(Path.Combine(homePath, "agents", "farnsworth", "prompts", "daily.prompt.json"),
            """{"name":"daily","description":"agent description","prompt":"agent {{topic}}"}""");
        fileSystem.File.WriteAllText(Path.Combine(workspacePath, "prompts", "daily.prompt.md"),
            """
            ---
            name: daily
            description: Safe workspace description
            parameters:
              audience:
                description: Who receives the update
                default: team
              topic:
                description: Subject to summarize
                required: true
            ---
            SECRET BODY {{topic}} for {{audience}}
            """);

        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        try
        {
            var resolver = CreateResolver(
                new CronOptions
                {
                    PromptTemplates = new Dictionary<string, ConfiguredPromptTemplate>
                    {
                        ["daily"] = new() { Description = "configured description", Prompt = "configured" }
                    }
                }, fileSystem, new StubWorkspaceManager(workspacePath));

            var descriptor = resolver.ListTemplates(AgentId.From("farnsworth"), 10).ShouldHaveSingleItem();

            descriptor.Name.ShouldBe("daily");
            descriptor.Description.ShouldBe("Safe workspace description");
            descriptor.Source.ShouldBe(PromptTemplateSource.Workspace);
            descriptor.ShadowedSources.ShouldBe([
                PromptTemplateSource.Agent,
                PromptTemplateSource.Shared,
                PromptTemplateSource.Configured
            ]);
            descriptor.Parameters.Select(parameter => parameter.Name).ShouldBe(["audience", "topic"]);
            descriptor.Parameters[0].Description.ShouldBe("Who receives the update");
            descriptor.Parameters[0].Default.ShouldBe("team");
            descriptor.Parameters[0].Required.ShouldBeFalse();
            descriptor.Parameters[1].Description.ShouldBe("Subject to summarize");
            descriptor.Parameters[1].Required.ShouldBeTrue();

            var serialized = System.Text.Json.JsonSerializer.Serialize(descriptor);
            serialized.ShouldNotContain("SECRET BODY");
            serialized.ShouldNotContain(homePath);
            serialized.ShouldNotContain(workspacePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", null);
        }
    }

    [Fact]
    public void ListTemplates_IsDeterministicAndBounded()
    {
        var resolver = CreateResolver(new CronOptions
        {
            PromptTemplates = Enumerable.Range(0, 6).Reverse().ToDictionary(
                index => $"template-{index}",
                index => new ConfiguredPromptTemplate { Prompt = $"body {index}" },
                StringComparer.OrdinalIgnoreCase)
        });

        resolver.ListTemplates(AgentId.From("farnsworth"), 3)
            .Select(template => template.Name)
            .ShouldBe(["template-0", "template-1", "template-2"]);
    }

    [Fact]
    public void Render_IgnoresUnknownParametersAndReportsMissingFields()
    {
        var resolver = CreateResolver(new CronOptions
        {
            PromptTemplates = new Dictionary<string, ConfiguredPromptTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                ["daily"] = new()
                {
                    Prompt = "{{topic}} for {{audience}}",
                    Parameters = new Dictionary<string, ConfiguredPromptTemplateParameter>
                    {
                        ["topic"] = new() { Required = true },
                        ["audience"] = new() { Required = true }
                    }
                }
            }
        });

        var missing = resolver.Render(AgentId.From("farnsworth"), "daily",
            new Dictionary<string, string?> { ["unknown"] = "ignored" });

        missing.Succeeded.ShouldBeFalse();
        missing.MissingRequiredParameters.ShouldBe(["audience", "topic"]);
        missing.UnknownParameterPolicy.ShouldBe(PromptTemplateUnknownParameterPolicy.Ignore);

        var rendered = resolver.Render(AgentId.From("farnsworth"), "daily",
            new Dictionary<string, string?>
            {
                ["topic"] = "release",
                ["audience"] = "team",
                ["unknown"] = "must not appear"
            });

        rendered.Succeeded.ShouldBeTrue();
        rendered.RenderedPrompt.ShouldBe("release for team");
        rendered.UnknownParameterPolicy.ShouldBe(PromptTemplateUnknownParameterPolicy.Ignore);
    }

    [Fact]
    public void MalformedEffectiveTemplate_IsRedactedFromCatalogueAndRenderError()
    {
        var fileSystem = new MockFileSystem();
        const string homePath = @"C:\users\test\.botnexus";
        var promptPath = Path.Combine(homePath, "prompts", "broken.prompt.json");
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        fileSystem.File.WriteAllText(promptPath, "{ definitely not JSON");

        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        try
        {
            var resolver = CreateResolver(new CronOptions(), fileSystem);

            resolver.ListTemplates(AgentId.From("farnsworth"), 10).ShouldBeEmpty();
            var result = resolver.Render(AgentId.From("farnsworth"), "broken", null);

            result.Succeeded.ShouldBeFalse();
            result.Error.ShouldBe("Prompt template 'broken' is malformed.");
            result.Error.ShouldNotContain(homePath);
            result.Error.ShouldNotContain(promptPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", null);
        }
    }

    [Fact]
    public void Malformed_higher_precedence_template_shadows_valid_lower_definition_everywhere()
    {
        var fileSystem = new MockFileSystem();
        const string homePath = @"C:\users\test\.botnexus";
        const string workspacePath = @"C:\users\test\.botnexus\agents\farnsworth\workspace";
        fileSystem.Directory.CreateDirectory(Path.Combine(homePath, "prompts"));
        fileSystem.Directory.CreateDirectory(Path.Combine(workspacePath, "prompts"));
        fileSystem.File.WriteAllText(Path.Combine(homePath, "prompts", "daily.prompt.json"), """{"name":"daily","prompt":"LOWER SECRET"}""");
        fileSystem.File.WriteAllText(Path.Combine(workspacePath, "prompts", "daily.prompt.json"), "{ malformed");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        try
        {
            var resolver = CreateResolver(new CronOptions(), fileSystem, new StubWorkspaceManager(workspacePath));
            resolver.ListTemplates(AgentId.From("farnsworth"), 10).ShouldBeEmpty();
            var result = resolver.Render(AgentId.From("farnsworth"), "daily", null);
            result.Succeeded.ShouldBeFalse();
            result.Error.ShouldBe("Prompt template 'daily' is malformed.");
            System.Text.Json.JsonSerializer.Serialize(result).ShouldNotContain("LOWER SECRET");
        }
        finally { Environment.SetEnvironmentVariable("BOTNEXUS_HOME", null); }
    }

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

        resolver.ListTemplateNames(AgentId.From("farnsworth")).ShouldBe(["Alpha", "zebra"]);
    }

    [Fact]
    public void TryRender_ReturnsErrorForUnknownTemplate()
    {
        var resolver = CreateResolver(new CronOptions());

        var ok = resolver.TryRender(AgentId.From("farnsworth"), "does-not-exist", null, out var rendered, out var error);

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

        var ok = resolver.TryRender(AgentId.From("farnsworth"),
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

            var ok = resolver.TryRender(AgentId.From("farnsworth"), "daily-status", new Dictionary<string, string?> { ["owner"] = "Hermes" }, out var rendered, out var error);

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
