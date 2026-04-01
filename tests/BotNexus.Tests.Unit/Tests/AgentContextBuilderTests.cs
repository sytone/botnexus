using BotNexus.Agent;
using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class AgentContextBuilderTests
{
    [Fact]
    public async Task BuildSystemPromptAsync_IncludesSoulFileWhenPresent()
    {
        var builder = CreateBuilder(workspaceFiles: new Dictionary<string, string?> { ["SOUL.md"] = "soul-content" });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## SOUL.md");
        prompt.Should().Contain("soul-content");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_IncludesIdentityFileWhenPresent()
    {
        var builder = CreateBuilder(workspaceFiles: new Dictionary<string, string?> { ["IDENTITY.md"] = "identity-content" });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## IDENTITY.md");
        prompt.Should().Contain("identity-content");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_IncludesUserFileWhenPresent()
    {
        var builder = CreateBuilder(workspaceFiles: new Dictionary<string, string?> { ["USER.md"] = "user-content" });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## USER.md");
        prompt.Should().Contain("user-content");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_MissingWorkspaceFiles_DoesNotThrowAndBuildsValidPrompt()
    {
        var builder = CreateBuilder();
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## Identity");
        prompt.Should().Contain("## AGENTS.md");
        prompt.Should().Contain("## TOOLS.md");
        prompt.Should().NotContain("## SOUL.md");
        prompt.Should().NotContain("## IDENTITY.md");
        prompt.Should().NotContain("## USER.md");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_AutoGeneratesAgentsMarkdownFromConfig()
    {
        var builder = CreateBuilder(configureAgents: agents =>
        {
            agents.Model = "gpt-4o";
            agents.Named["fry"] = new AgentConfig { Model = "gpt-4o-mini", SystemPrompt = "Frontend developer" };
            agents.Named["amy"] = new AgentConfig { Provider = "copilot", SystemPromptFile = "AMY_PROMPT.md" };
        });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## AGENTS.md");
        prompt.Should().Contain("### default");
        prompt.Should().Contain("- Model: gpt-4o");
        prompt.Should().Contain("### amy");
        prompt.Should().Contain("- Provider: copilot");
        prompt.Should().Contain("- Role: from AMY_PROMPT.md");
        prompt.Should().Contain("### fry");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_AutoGeneratesToolsMarkdownFromRegistry()
    {
        var alpha = new Mock<ITool>();
        alpha.SetupGet(t => t.Definition).Returns(new ToolDefinition("alpha", "Alpha tool", new Dictionary<string, ToolParameterSchema>()));
        var zeta = new Mock<ITool>();
        zeta.SetupGet(t => t.Definition).Returns(new ToolDefinition("zeta", "Zeta tool", new Dictionary<string, ToolParameterSchema>()));

        var builder = CreateBuilder(tools: [zeta.Object, alpha.Object]);
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## TOOLS.md");
        prompt.Should().Contain("- alpha: Alpha tool");
        prompt.Should().Contain("- zeta: Zeta tool");
        prompt.IndexOf("- alpha: Alpha tool", StringComparison.Ordinal)
            .Should()
            .BeLessThan(prompt.IndexOf("- zeta: Zeta tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSystemPromptAsync_LoadsMemorySection_WhenAutoLoadMemoryEnabled()
    {
        var memoryStore = CreateMemoryStoreMock(new Dictionary<string, string?>
        {
            ["MEMORY"] = "long-term-memory"
        });
        var builder = CreateBuilder(memoryStore: memoryStore.Object, configureAgents: agents =>
        {
            agents.Named["bender"].AutoLoadMemory = true;
        });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## MEMORY.md");
        prompt.Should().Contain("long-term-memory");
        memoryStore.Verify(m => m.ReadAsync("bender", "MEMORY", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildSystemPromptAsync_LoadsTodaysDailyMemory()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var builder = CreateBuilder(memoryEntries: new Dictionary<string, string?>
        {
            [$"daily/{today}"] = "today-memory"
        });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain($"## memory/daily/{today}.md");
        prompt.Should().Contain("today-memory");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_LoadsYesterdaysDailyMemory()
    {
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var builder = CreateBuilder(memoryEntries: new Dictionary<string, string?>
        {
            [$"daily/{yesterday}"] = "yesterday-memory"
        });
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain($"## memory/daily/{yesterday}.md");
        prompt.Should().Contain("yesterday-memory");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_DoesNotLoadOlderDailyMemoryFiles()
    {
        var olderDate = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var memoryStore = CreateMemoryStoreMock(new Dictionary<string, string?>
        {
            [$"daily/{olderDate}"] = "older-memory"
        });
        var builder = CreateBuilder(memoryStore: memoryStore.Object);
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().NotContain("older-memory");
        memoryStore.Verify(m => m.ReadAsync("bender", $"daily/{olderDate}", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuildSystemPromptAsync_TruncatesSectionsOverConfiguredLimit()
    {
        var builder = CreateBuilder(
            workspaceFiles: new Dictionary<string, string?> { ["SOUL.md"] = new string('x', 20) },
            configureAgents: agents => agents.Named["bender"].MaxContextFileChars = 10);
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("xxxxxxxxxx");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_AppendsTruncationMarkerForLongContent()
    {
        var builder = CreateBuilder(
            workspaceFiles: new Dictionary<string, string?> { ["SOUL.md"] = new string('x', 20) },
            configureAgents: agents => agents.Named["bender"].MaxContextFileChars = 10);
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("[truncated]");
    }

    [Fact]
    public async Task BuildMessagesAsync_IncludesSystemPromptHistoryAndUserMessage()
    {
        var builder = CreateBuilder(configureAgents: agents => agents.ContextWindowTokens = 1024);
        var history = new List<ChatMessage>
        {
            new("user", "old-user"),
            new("assistant", "old-assistant")
        };

        var messages = await builder.BuildMessagesAsync("bender", history, "current message", "telegram", "chat-123");

        messages.Select(m => m.Role).Should().Equal("system", "user", "assistant", "user");
        messages[1].Content.Should().Be("old-user");
        messages[2].Content.Should().Be("old-assistant");
        messages[^1].Content.Should().Contain("current message");
    }

    [Fact]
    public async Task BuildMessagesAsync_InjectsRuntimeContextFields()
    {
        var builder = CreateBuilder(configureAgents: agents => agents.ContextWindowTokens = 1024);
        var messages = await builder.BuildMessagesAsync("bender", [], "current message", "telegram", "chat-123");
        var runtime = messages[^1].Content;

        runtime.Should().Contain("## Runtime Context");
        runtime.Should().Contain("Time (UTC):");
        runtime.Should().Contain("Channel: telegram");
        runtime.Should().Contain("Chat ID: chat-123");
        runtime.Should().Contain("## User Message");
        runtime.Should().Contain("current message");
    }

    [Fact]
    public async Task BuildMessagesAsync_TrimsHistoryToContextBudget()
    {
        var builder = CreateBuilder(configureAgents: agents => agents.ContextWindowTokens = 500);
        var history = new List<ChatMessage>
        {
            new("user", new string('a', 700)),
            new("assistant", new string('b', 700)),
            new("user", new string('c', 700))
        };

        var messages = await builder.BuildMessagesAsync("bender", history, "current message", "telegram", "chat-123");

        messages.Should().ContainSingle(m => m.Role == "user" && m.Content == new string('c', 700));
        messages.Should().NotContain(m => m.Content == new string('a', 700));
        messages.Count(m => m.Content.Length == 700)
            .Should()
            .BeLessThan(3);
    }

    [Fact]
    public async Task BuildSystemPromptAsync_EmptyWorkspace_ProducesMinimalValidPrompt()
    {
        var builder = CreateBuilder();
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## Identity");
        prompt.Should().Contain("## AGENTS.md");
        prompt.Should().Contain("## TOOLS.md");
        prompt.Should().Contain("No tools registered.");
    }

    private static AgentContextBuilder CreateBuilder(
        Dictionary<string, string?>? workspaceFiles = null,
        Dictionary<string, string?>? memoryEntries = null,
        IMemoryStore? memoryStore = null,
        Action<AgentDefaults>? configureAgents = null,
        IEnumerable<ITool>? tools = null)
    {
        var workspace = CreateWorkspaceMock(workspaceFiles);
        var resolvedMemoryStore = memoryStore ?? CreateMemoryStoreMock(memoryEntries).Object;
        var registry = new ToolRegistry();

        if (tools is not null)
        {
            foreach (var tool in tools)
                registry.Register(tool);
        }

        var agents = new AgentDefaults
        {
            Model = "gpt-4o",
            ContextWindowTokens = 1024,
            Named = new Dictionary<string, AgentConfig>
            {
                ["bender"] = new() { Model = "gpt-5-mini", SystemPrompt = "Runtime developer", MaxContextFileChars = 8000, AutoLoadMemory = true }
            }
        };
        configureAgents?.Invoke(agents);

        var config = Options.Create(new BotNexusConfig { Agents = agents });
        return new AgentContextBuilder(workspace.Object, resolvedMemoryStore, registry, config, NullLogger<AgentContextBuilder>.Instance);
    }

    private static Mock<IAgentWorkspace> CreateWorkspaceMock(Dictionary<string, string?>? files = null)
    {
        var workspace = new Mock<IAgentWorkspace>();
        workspace.SetupGet(w => w.WorkspacePath).Returns(@"C:\agent\workspace");
        workspace.Setup(w => w.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        workspace.Setup(w => w.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string fileName, CancellationToken _) =>
            {
                if (files is null)
                    return null;

                return files.GetValueOrDefault(fileName);
            });
        return workspace;
    }

    private static Mock<IMemoryStore> CreateMemoryStoreMock(Dictionary<string, string?>? entries = null)
    {
        var memoryStore = new Mock<IMemoryStore>();
        memoryStore.Setup(m => m.ReadAsync("bender", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string key, CancellationToken _) =>
            {
                if (entries is null)
                    return null;

                return entries.GetValueOrDefault(key);
            });
        return memoryStore;
    }
}
