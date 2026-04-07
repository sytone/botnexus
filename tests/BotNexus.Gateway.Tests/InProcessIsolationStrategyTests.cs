using BotNexus.AgentCore.Tools;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Tools;
using BotNexus.Tools;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class InProcessIsolationStrategyTests
{
    [Fact]
    public async Task CreateAsync_WithValidDescriptor_ReturnsAgentHandle()
    {
        var strategy = CreateStrategyWithRegisteredModel();

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = "session-1" });

        handle.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WhenModelNotRegistered_ThrowsInvalidOperationException()
    {
        var llmClient = new LlmClient(new ApiProviderRegistry(), new ModelRegistry());
        var strategy = new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(new PlatformConfig(), NullLogger<GatewayAuthManager>.Instance),
            new PassthroughContextBuilder(),
            new StaticAgentToolFactory(),
            new TestWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            NullLogger<InProcessIsolationStrategy>.Instance);

        var act = () => strategy.CreateAsync(
            CreateDescriptor(modelId: "missing-model"),
            new AgentExecutionContext { SessionId = "session-1" });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_WithValidDescriptor_SetsAgentAndSessionIds()
    {
        var strategy = CreateStrategyWithRegisteredModel();

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = "session-123" });

        handle.AgentId.Should().Be("agent-a");
        handle.SessionId.Should().Be("session-123");
    }

    [Fact]
    public void Name_WhenRead_ReturnsInProcess()
    {
        var strategy = CreateStrategyWithRegisteredModel();

        strategy.Name.Should().Be("in-process");
    }

    private static InProcessIsolationStrategy CreateStrategyWithRegisteredModel()
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var llmClient = new LlmClient(new ApiProviderRegistry(), modelRegistry);
        return new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(new PlatformConfig(), NullLogger<GatewayAuthManager>.Instance),
            new PassthroughContextBuilder(),
            new StaticAgentToolFactory(),
            new TestWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            NullLogger<InProcessIsolationStrategy>.Instance);
    }

    private static AgentDescriptor CreateDescriptor(string modelId = "test-model")
        => new()
        {
            AgentId = "agent-a",
            DisplayName = "Agent A",
            ModelId = modelId,
            ApiProvider = "test-provider",
            SystemPrompt = "You are a test agent."
        };

    private sealed class PassthroughContextBuilder : IContextBuilder
    {
        public Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken ct = default)
            => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
    }

    private sealed class StaticAgentToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory)
            => [new ReadTool(workingDirectory)];
    }

    private sealed class TestWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName)
            => AppContext.BaseDirectory;
    }
}
