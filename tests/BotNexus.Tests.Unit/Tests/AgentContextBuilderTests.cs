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
    public async Task BuildSystemPromptAsync_AssemblesExpectedSectionsInOrder()
    {
        var workspace = new Mock<IAgentWorkspace>();
        workspace.SetupGet(w => w.WorkspacePath).Returns(@"C:\agent\workspace");
        workspace.Setup(w => w.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        workspace.Setup(w => w.ReadFileAsync("SOUL.md", It.IsAny<CancellationToken>())).ReturnsAsync("soul");
        workspace.Setup(w => w.ReadFileAsync("IDENTITY.md", It.IsAny<CancellationToken>())).ReturnsAsync("identity");
        workspace.Setup(w => w.ReadFileAsync("USER.md", It.IsAny<CancellationToken>())).ReturnsAsync("user");

        var memoryStore = new Mock<IMemoryStore>();
        memoryStore.Setup(m => m.ReadAsync("bender", "MEMORY", It.IsAny<CancellationToken>())).ReturnsAsync("long-memory");
        memoryStore.Setup(m => m.ReadAsync("bender", $"daily/{DateTime.UtcNow:yyyy-MM-dd}", It.IsAny<CancellationToken>())).ReturnsAsync("today");
        memoryStore.Setup(m => m.ReadAsync("bender", $"daily/{DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}", It.IsAny<CancellationToken>())).ReturnsAsync("yesterday");

        var registry = new ToolRegistry();
        var tool = new Mock<ITool>();
        tool.SetupGet(t => t.Definition).Returns(new ToolDefinition("search", "Searches memory", new Dictionary<string, ToolParameterSchema>()));
        registry.Register(tool.Object);

        var config = Options.Create(new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                Model = "gpt-4o",
                Named = new Dictionary<string, AgentConfig>
                {
                    ["bender"] = new() { Model = "gpt-5-mini", SystemPrompt = "Runtime developer", MaxContextFileChars = 8000 },
                    ["fry"] = new() { Model = "gpt-4o-mini", SystemPrompt = "Frontend developer" }
                }
            }
        });

        var builder = new AgentContextBuilder(workspace.Object, memoryStore.Object, registry, config, NullLogger<AgentContextBuilder>.Instance);
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("## Identity");
        prompt.Should().Contain("## SOUL.md");
        prompt.Should().Contain("## IDENTITY.md");
        prompt.Should().Contain("## USER.md");
        prompt.Should().Contain("## AGENTS.md");
        prompt.Should().Contain("## TOOLS.md");
        prompt.Should().Contain("## MEMORY.md");
        prompt.Should().Contain("## memory/daily/");
        prompt.IndexOf("## SOUL.md", StringComparison.Ordinal).Should().BeGreaterThan(prompt.IndexOf("## Identity", StringComparison.Ordinal));
        prompt.IndexOf("## AGENTS.md", StringComparison.Ordinal).Should().BeGreaterThan(prompt.IndexOf("## USER.md", StringComparison.Ordinal));
        prompt.IndexOf("## TOOLS.md", StringComparison.Ordinal).Should().BeGreaterThan(prompt.IndexOf("## AGENTS.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSystemPromptAsync_TruncatesLongSections()
    {
        var workspace = new Mock<IAgentWorkspace>();
        workspace.SetupGet(w => w.WorkspacePath).Returns(@"C:\agent\workspace");
        workspace.Setup(w => w.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        workspace.Setup(w => w.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new string('x', 20));

        var memoryStore = new Mock<IMemoryStore>();
        memoryStore.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var config = Options.Create(new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                Named = new Dictionary<string, AgentConfig>
                {
                    ["bender"] = new() { MaxContextFileChars = 10 }
                }
            }
        });

        var builder = new AgentContextBuilder(workspace.Object, memoryStore.Object, new ToolRegistry(), config, NullLogger<AgentContextBuilder>.Instance);
        var prompt = await builder.BuildSystemPromptAsync("bender");

        prompt.Should().Contain("[truncated]");
    }

    [Fact]
    public async Task BuildMessagesAsync_AddsSystemPromptAndRuntimeEnvelope()
    {
        var workspace = new Mock<IAgentWorkspace>();
        workspace.SetupGet(w => w.WorkspacePath).Returns(@"C:\agent\workspace");
        workspace.Setup(w => w.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        workspace.Setup(w => w.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("base");

        var memoryStore = new Mock<IMemoryStore>();
        memoryStore.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var config = Options.Create(new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                ContextWindowTokens = 200
            }
        });

        var builder = new AgentContextBuilder(workspace.Object, memoryStore.Object, new ToolRegistry(), config, NullLogger<AgentContextBuilder>.Instance);
        var history = new List<ChatMessage>
        {
            new("user", "old-user"),
            new("assistant", "old-assistant")
        };

        var messages = await builder.BuildMessagesAsync("bender", history, "current message", "telegram", "chat-123");

        messages[0].Role.Should().Be("system");
        messages[^1].Role.Should().Be("user");
        messages[^1].Content.Should().Contain("Channel: telegram");
        messages[^1].Content.Should().Contain("Chat ID: chat-123");
        messages[^1].Content.Should().Contain("current message");
    }
}
