using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class AgentManagementToolTests
{
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "agmt-tests-" + Guid.NewGuid());

    private static AgentDescriptor MakeDescriptor(string id, string displayName = "Test Agent") =>
        new()
        {
            AgentId = AgentId.From(id),
            DisplayName = displayName,
            ModelId = "test-model",
            ApiProvider = "test"
        };

    private static (Mock<IAgentRegistry> registry, Mock<IAgentConfigurationWriter> writer, BotNexusHome home, Mock<IAgentChangeNotifier> notifier)
        MakeDeps(bool agentExists = false, string? existingId = null, AgentDescriptor? existingDescriptor = null)
    {
        var registry = new Mock<IAgentRegistry>();
        var writer = new Mock<IAgentConfigurationWriter>();
        var notifier = new Mock<IAgentChangeNotifier>();
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        if (agentExists && existingId is not null)
        {
            registry.Setup(r => r.Contains(AgentId.From(existingId))).Returns(true);
            registry.Setup(r => r.Get(AgentId.From(existingId))).Returns(existingDescriptor ?? MakeDescriptor(existingId));
        }

        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (registry, writer, home, notifier);
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    // --- CreateAgentTool Tests ---

    [Fact]
    public void CreateAgent_HasExpectedNameAndLabel()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);
        tool.Name.ShouldBe("create_agent");
        tool.Label.ShouldBe("Create Agent");
    }

    [Fact]
    public async Task CreateAgent_CreatesDescriptorAndScaffoldsWorkspace()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "my-new-agent"),
            ("displayName", "My New Agent"),
            ("modelId", "claude-sonnet"),
            ("apiProvider", "anthropic")));

        var text = result.Content[0].Value;
        text.ShouldContain("my-new-agent");
        text.ShouldContain("created");
        text.ShouldNotContain("error");

        registry.Verify(r => r.Register(It.Is<AgentDescriptor>(d =>
            d.AgentId == AgentId.From("my-new-agent") &&
            d.DisplayName == "My New Agent" &&
            d.ModelId == "claude-sonnet" &&
            d.ApiProvider == "anthropic")), Times.Once);

        writer.Verify(w => w.SaveAsync(It.Is<AgentDescriptor>(d => d.AgentId == AgentId.From("my-new-agent")), It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.NotifyAgentsChangedAsync("added", "my-new-agent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("coder")]
    [InlineData("reviewer")]
    [InlineData("researcher")]
    public async Task CreateAgent_ReservedArchetypeId_ReturnsError(string archetypeId)
    {
        // #2136: reserved worker-archetype ids cannot be created as named agents.
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", archetypeId),
            ("displayName", "Should Fail"),
            ("modelId", "m"),
            ("apiProvider", "p")));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("reserved");
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task CreateAgent_DuplicateId_ReturnsError()
    {
        var (registry, writer, home, notifier) = MakeDeps(agentExists: true, existingId: "existing-agent");
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "existing-agent"),
            ("displayName", "Dupe"),
            ("modelId", "m"),
            ("apiProvider", "p")));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("already registered");

        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task CreateAgent_InvalidId_ReturnsError()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "Invalid_ID!"),
            ("displayName", "Bad"),
            ("modelId", "m"),
            ("apiProvider", "p")));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("Invalid agent ID");
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task CreateAgent_MissingRequiredField_ReturnsError()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "ok-agent"),
            ("displayName", "OK")));
        // missing modelId and apiProvider

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task CreateAgent_WithOptionalFields_StoresThemCorrectly()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "full-agent"),
            ("displayName", "Full Agent"),
            ("description", "Does everything"),
            ("emoji", "robot"),
            ("modelId", "gpt-4o"),
            ("apiProvider", "openai"),
            ("systemPrompt", "You are helpful."),
            ("toolIds", """["read","write"]""")));

        result.Content[0].Value.ShouldNotContain("error");

        registry.Verify(r => r.Register(It.Is<AgentDescriptor>(d =>
            d.Description == "Does everything" &&
            d.Emoji == "robot" &&
            d.SystemPrompt == "You are helpful." &&
            d.ToolIds.Contains("read") &&
            d.ToolIds.Contains("write"))), Times.Once);
    }

    [Fact]
    public async Task CreateAgent_NotifierThrows_BestEffortSwallowsException()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        notifier.Setup(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("network down"));

        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        // Should not throw
        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "agent-notify-fail"),
            ("displayName", "X"),
            ("modelId", "m"),
            ("apiProvider", "p")));

        result.Content[0].Value.ShouldNotContain("error");
    }

    // --- UpdateAgentTool Tests ---

    [Fact]
    public void UpdateAgent_HasExpectedNameAndLabel()
    {
        var (registry, writer, _, notifier) = MakeDeps();
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);
        tool.Name.ShouldBe("update_agent");
        tool.Label.ShouldBe("Update Agent");
    }

    [Theory]
    [InlineData("coder")]
    [InlineData("writer")]
    public async Task UpdateAgent_ReservedArchetypeId_ReturnsError(string archetypeId)
    {
        // #2136: reserved worker-archetype ids are not real named agents and cannot be updated.
        var (registry, writer, _, notifier) = MakeDeps();
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", archetypeId),
            ("displayName", "Should Fail")));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("reserved");
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_PatchesFields()
    {
        var existing = MakeDescriptor("my-agent", "Old Name");
        var (registry, writer, _, notifier) = MakeDeps(agentExists: true, existingId: "my-agent", existingDescriptor: existing);
        registry.Setup(r => r.Update(AgentId.From("my-agent"), It.IsAny<AgentDescriptor>())).Returns(true);

        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "my-agent"),
            ("displayName", "New Name"),
            ("description", "Updated desc")));

        var text = result.Content[0].Value;
        text.ShouldNotContain("error");
        text.ShouldContain("New Name");

        registry.Verify(r => r.Update(AgentId.From("my-agent"), It.Is<AgentDescriptor>(d =>
            d.DisplayName == "New Name" &&
            d.Description == "Updated desc" &&
            d.ModelId == "test-model")), Times.Once);

        writer.Verify(w => w.SaveAsync(It.Is<AgentDescriptor>(d => d.DisplayName == "New Name"), It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.NotifyAgentsChangedAsync("updated", "my-agent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAgent_NotFound_ReturnsError()
    {
        var (registry, writer, _, notifier) = MakeDeps();
        registry.Setup(r => r.Get(AgentId.From("ghost"))).Returns((AgentDescriptor?)null);

        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "ghost"),
            ("displayName", "Does not matter")));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("not registered");

        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_OmittedFields_PreservesOriginalValues()
    {
        var existing = new AgentDescriptor
        {
            AgentId = AgentId.From("preserve-agent"),
            DisplayName = "Original Name",
            Description = "Original desc",
            Emoji = "test-emoji",
            ModelId = "original-model",
            ApiProvider = "original-provider",
            SystemPrompt = "Original prompt",
            ToolIds = ["read", "write"]
        };
        var (registry, writer, _, notifier) = MakeDeps(agentExists: true, existingId: "preserve-agent", existingDescriptor: existing);
        registry.Setup(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>())).Returns(true);

        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        // Only update modelId
        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "preserve-agent"),
            ("modelId", "new-model")));

        result.Content[0].Value.ShouldNotContain("error");

        registry.Verify(r => r.Update(AgentId.From("preserve-agent"), It.Is<AgentDescriptor>(d =>
            d.DisplayName == "Original Name" &&
            d.Description == "Original desc" &&
            d.Emoji == "test-emoji" &&
            d.ModelId == "new-model" &&
            d.ApiProvider == "original-provider" &&
            d.SystemPrompt == "Original prompt" &&
            d.ToolIds.Contains("read"))), Times.Once);
    }

    [Fact]
    public async Task UpdateAgent_NotifierThrows_BestEffortSwallowsException()
    {
        var existing = MakeDescriptor("notif-agent");
        var (registry, writer, _, notifier) = MakeDeps(agentExists: true, existingId: "notif-agent", existingDescriptor: existing);
        registry.Setup(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>())).Returns(true);
        notifier.Setup(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("connection lost"));

        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        // Should not throw
        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "notif-agent"),
            ("displayName", "Updated")));

        result.Content[0].Value.ShouldNotContain("error");
    }

    // --- Bootstrap Config Tests ---

    [Fact]
    public async Task CreateAgent_PopulatesMemoryConfig()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        AgentDescriptor? savedDescriptor = null;
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback<AgentDescriptor, CancellationToken>((d, _) => savedDescriptor = d)
            .Returns(Task.CompletedTask);

        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "mem-agent"),
            ("displayName", "Memory Agent"),
            ("modelId", "claude-sonnet"),
            ("apiProvider", "anthropic")));

        result.Content[0].Value.ShouldNotContain("error");
        savedDescriptor.ShouldNotBeNull();
        savedDescriptor!.Memory.ShouldNotBeNull();
        savedDescriptor.Memory!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAgent_PopulatesSkillsExtensionConfig_WhenSkillsEnabled()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        AgentDescriptor? savedDescriptor = null;
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback<AgentDescriptor, CancellationToken>((d, _) => savedDescriptor = d)
            .Returns(Task.CompletedTask);

        var skillsElement = JsonSerializer.SerializeToElement(new { enabled = true });
        var platformConfig = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Extensions = new ExtensionsConfig
                {
                    Defaults = new Dictionary<string, JsonElement>
                    {
                        ["botnexus-skills"] = skillsElement
                    }
                }
            }
        };
        var options = Options.Create(platformConfig);
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, options);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "skills-agent"),
            ("displayName", "Skills Agent"),
            ("modelId", "gpt-4o"),
            ("apiProvider", "openai")));

        result.Content[0].Value.ShouldNotContain("error");
        savedDescriptor.ShouldNotBeNull();
        savedDescriptor!.ExtensionConfig.ShouldContainKey("botnexus-skills");
    }

    [Fact]
    public async Task UpdateAgent_PreservesExistingMemoryConfig()
    {
        var existingMemory = new MemoryAgentConfig
        {
            Enabled = true,
            Indexing = "auto",
            PromptInjection = "full"
        };
        var existing = new AgentDescriptor
        {
            AgentId = AgentId.From("mem-preserve"),
            DisplayName = "Preserve Memory",
            ModelId = "test-model",
            ApiProvider = "test",
            Memory = existingMemory
        };
        var (registry, writer, _, notifier) = MakeDeps(agentExists: true, existingId: "mem-preserve", existingDescriptor: existing);
        registry.Setup(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>())).Returns(true);
        AgentDescriptor? savedDescriptor = null;
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback<AgentDescriptor, CancellationToken>((d, _) => savedDescriptor = d)
            .Returns(Task.CompletedTask);

        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "mem-preserve"),
            ("displayName", "New Name")));

        result.Content[0].Value.ShouldNotContain("error");
        savedDescriptor.ShouldNotBeNull();
        savedDescriptor!.Memory.ShouldNotBeNull();
        savedDescriptor.Memory!.Enabled.ShouldBeTrue();
        savedDescriptor.Memory.Indexing.ShouldBe("auto");
    }

    // --- Provider Validation Tests ---

    [Fact]
    public async Task CreateAgent_UnknownProvider_ReturnsError()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeProvider("anthropic"));
        providerRegistry.Register(new FakeProvider("copilot"));
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, providerRegistry);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "bad-provider"),
            ("displayName", "Bad"),
            ("modelId", "model"),
            ("apiProvider", "nonexistent")));

        result.Content[0].Value.ShouldContain("error");
        result.Content[0].Value.ShouldContain("Unknown API provider");
        result.Content[0].Value.ShouldContain("nonexistent");
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task CreateAgent_ValidProvider_Succeeds()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeProvider("anthropic"));
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, providerRegistry);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "valid-provider"),
            ("displayName", "Good"),
            ("modelId", "model"),
            ("apiProvider", "anthropic")));

        result.Content[0].Value.ShouldNotContain("error");
        result.Content[0].Value.ShouldContain("created");
    }

    [Fact]
    public async Task CreateAgent_NoProviderRegistry_SkipsValidation()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, null);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "no-registry"),
            ("displayName", "No Registry"),
            ("modelId", "model"),
            ("apiProvider", "anything-goes")));

        result.Content[0].Value.ShouldNotContain("error");
    }

    [Fact]
    public async Task UpdateAgent_UnknownProvider_ReturnsError()
    {
        var (registry, writer, _, notifier) = MakeDeps(agentExists: true, existingId: "update-test");
        registry.Setup(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>())).Returns(true);
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeProvider("anthropic"));
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], providerRegistry);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "update-test"),
            ("apiProvider", "bad-provider")));

        result.Content[0].Value.ShouldContain("error");
        result.Content[0].Value.ShouldContain("Unknown API provider");
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_ValidProvider_Succeeds()
    {
        var (registry, writer, _, notifier) = MakeDeps(agentExists: true, existingId: "update-valid");
        registry.Setup(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>())).Returns(true);
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeProvider("copilot"));
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], providerRegistry);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "update-valid"),
            ("apiProvider", "copilot")));

        result.Content[0].Value.ShouldNotContain("error");
    }


    // --- PBI4 (#1705): thinking + context defaults validated against model capabilities ---

    private static ModelRegistry MakeCapabilityRegistry()
    {
        var registry = new ModelRegistry();
        // Reasoning model with extended context: supports thinking levels and 200K/1M windows.
        registry.Register("anthropic", new LlmModel(
            Id: "claude-reasoning",
            Name: "Claude Reasoning",
            Api: "anthropic-messages",
            Provider: "anthropic",
            BaseUrl: "https://example.invalid",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 200_000,
            MaxTokens: 64_000,
            SupportsExtraHighThinking: true,
            SupportsExtendedContextWindow: true));
        // Non-reasoning, fixed-window model: any thinking or non-default context is invalid.
        registry.Register("anthropic", new LlmModel(
            Id: "claude-plain",
            Name: "Claude Plain",
            Api: "anthropic-messages",
            Provider: "anthropic",
            BaseUrl: "https://example.invalid",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 128_000,
            MaxTokens: 16_000));
        return registry;
    }

    [Fact]
    public async Task CreateAgent_WithSupportedThinkingAndContext_PersistsThem()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, null, MakeCapabilityRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "thinky-agent"),
            ("displayName", "Thinky"),
            ("modelId", "claude-reasoning"),
            ("apiProvider", "anthropic"),
            ("thinking", "high"),
            ("contextWindow", 1_000_000)));

        result.Content[0].Value.ShouldNotContain("error");
        registry.Verify(r => r.Register(It.Is<AgentDescriptor>(d =>
            d.Thinking == "high" && d.ContextWindow == 1_000_000)), Times.Once);
    }

    [Fact]
    public async Task CreateAgent_WithThinkingOnNonReasoningModel_ReturnsErrorAndDoesNotRegister()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, null, MakeCapabilityRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "bad-agent"),
            ("displayName", "Bad"),
            ("modelId", "claude-plain"),
            ("apiProvider", "anthropic"),
            ("thinking", "high")));

        result.Content[0].Value.ShouldContain("error");
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task CreateAgent_WithUnsupportedContextWindow_ReturnsErrorAndDoesNotRegister()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, null, MakeCapabilityRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "bad-agent"),
            ("displayName", "Bad"),
            ("modelId", "claude-reasoning"),
            ("apiProvider", "anthropic"),
            ("contextWindow", 999_999)));

        result.Content[0].Value.ShouldContain("error");
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_WithSupportedThinking_PersistsIt()
    {
        var existing = MakeDescriptor("thinky-agent") with { ModelId = "claude-reasoning", ApiProvider = "anthropic" };
        var (registry, writer, home, notifier) = MakeDeps(agentExists: true, existingId: "thinky-agent", existingDescriptor: existing);
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], null, MakeCapabilityRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "thinky-agent"),
            ("thinking", "medium")));

        result.Content[0].Value.ShouldNotContain("error");
        registry.Verify(r => r.Update(AgentId.From("thinky-agent"),
            It.Is<AgentDescriptor>(d => d.Thinking == "medium")), Times.Once);
    }

    [Fact]
    public async Task UpdateAgent_WithUnsupportedThinking_ReturnsErrorAndDoesNotUpdate()
    {
        var existing = MakeDescriptor("thinky-agent") with { ModelId = "claude-plain", ApiProvider = "anthropic" };
        var (registry, writer, home, notifier) = MakeDeps(agentExists: true, existingId: "thinky-agent", existingDescriptor: existing);
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], null, MakeCapabilityRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "thinky-agent"),
            ("thinking", "high")));

        result.Content[0].Value.ShouldContain("error");
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
    }

    private sealed class FakeProvider(string api) : IApiProvider
    {
        public string Api => api;
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null) => throw new NotImplementedException();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null) => throw new NotImplementedException();
    }
}
