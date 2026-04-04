using BotNexus.Agent;
using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using BotNexus.Session;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class AgentLoopTests : IDisposable
{
    private readonly string _tempPath;
    private readonly SessionManager _sessionManager;

    public AgentLoopTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"botnexus-agent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);
        _sessionManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
    }

    private static InboundMessage MakeMessage(string content = "hello") =>
        new("telegram", "user1", "chat1", content, DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

    [Fact]
    public async Task ProcessAsync_SimpleResponse_ReturnsContent()
    {
        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("Hello back!", FinishReason.Stop));

        var loop = CreateLoop(mockProvider.Object);
        var result = await loop.ProcessAsync(MakeMessage("hello"));

        result.Should().Be("Hello back!");
    }

    [Fact]
    public async Task ProcessAsync_AddsUserMessageToSession()
    {
        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("reply", FinishReason.Stop));

        var loop = CreateLoop(mockProvider.Object);
        var message = MakeMessage("test input");
        await loop.ProcessAsync(message);

        var session = await _sessionManager.GetOrCreateAsync(message.SessionKey, "test-agent");
        session.History.Should().Contain(e => e.Content == "test input" && e.Role == MessageRole.User);
    }

    [Fact]
    public async Task ProcessAsync_AddsAssistantResponseToSession()
    {
        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("assistant response", FinishReason.Stop));

        var loop = CreateLoop(mockProvider.Object);
        var message = MakeMessage("hello");
        await loop.ProcessAsync(message);

        var session = await _sessionManager.GetOrCreateAsync(message.SessionKey, "test-agent");
        session.History.Should().Contain(e => e.Content == "assistant response" && e.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task ProcessAsync_BuildsSystemPromptAtRunStart()
    {
        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("ok", FinishReason.Stop));

        var contextBuilder = new Mock<IContextBuilder>(MockBehavior.Strict);
        contextBuilder
            .Setup(cb => cb.BuildSystemPromptAsync("test-agent", It.IsAny<CancellationToken>()))
            .ReturnsAsync("You are test-agent")
            .Verifiable();
        contextBuilder
            .Setup(cb => cb.BuildMessagesAsync(
                "test-agent",
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage> { new("system", "You are test-agent"), new("user", "hello") });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: contextBuilder.Object,
            toolRegistry: new ToolRegistry(),
            settings: new GenerationSettings(),
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 5);

        await loop.ProcessAsync(MakeMessage("hello"));

        contextBuilder.Verify(cb => cb.BuildSystemPromptAsync("test-agent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ExecutesToolCalls_AddsToolResultToSession()
    {
        var toolRegistry = new ToolRegistry();
        var mockTool = new Mock<ITool>();
        mockTool.Setup(t => t.Definition).Returns(
            new ToolDefinition("test_tool", "Test tool", new Dictionary<string, ToolParameterSchema>()));
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tool output");
        toolRegistry.Register(mockTool.Object);

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        var callCount = 0;
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id1", "test_tool", new Dictionary<string, object?>())]);
                return new LlmResponse("final answer", FinishReason.Stop);
            });

        var loop = CreateLoop(mockProvider.Object, toolRegistry);
        var message = new InboundMessage("telegram", "u", "toolchat", "use tool",
            DateTimeOffset.UtcNow, [], new Dictionary<string, object>());
        var result = await loop.ProcessAsync(message);

        result.Should().Be("final answer");
        var session = await _sessionManager.GetOrCreateAsync(message.SessionKey, "test-agent");
        session.History.Should().Contain(e => e.Role == MessageRole.Tool && e.Content == "tool output");
    }

    [Fact]
    public async Task ProcessAsync_ExecutesToolCalls_FromAdditionalTools()
    {
        var dynamicTool = new Mock<ITool>();
        dynamicTool.Setup(t => t.Definition).Returns(
            new ToolDefinition("dynamic_tool", "Dynamic tool", new Dictionary<string, ToolParameterSchema>()));
        dynamicTool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dynamic output");

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        var callCount = 0;
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id1", "dynamic_tool", new Dictionary<string, object?>())]);
                return new LlmResponse("done", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: new ToolRegistry(),
            settings: new GenerationSettings(),
            additionalTools: [dynamicTool.Object],
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 5);

        var result = await loop.ProcessAsync(MakeMessage("use dynamic tool"));

        result.Should().Be("done");
        dynamicTool.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_EnableMemoryTrue_IncludesMemoryToolsInChatRequest()
    {
        ChatRequest? capturedRequest = null;
        var memoryStore = new Mock<IMemoryStore>();

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new LlmResponse("ok", FinishReason.Stop));

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: new ToolRegistry(),
            settings: new GenerationSettings(),
            enableMemory: true,
            memoryStore: memoryStore.Object,
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 5);

        await loop.ProcessAsync(MakeMessage("hello"));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Tools.Should().NotBeNull();
        capturedRequest.Tools!.Select(t => t.Name).Should().Contain(["memory_search", "memory_save", "memory_get"]);
    }

    [Fact]
    public async Task ProcessAsync_EnableMemoryFalse_DoesNotIncludeMemoryToolsInChatRequest()
    {
        ChatRequest? capturedRequest = null;
        var memoryStore = new Mock<IMemoryStore>();

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new LlmResponse("ok", FinishReason.Stop));

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: new ToolRegistry(),
            settings: new GenerationSettings(),
            enableMemory: false,
            memoryStore: memoryStore.Object,
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 5);

        await loop.ProcessAsync(MakeMessage("hello"));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Tools.Should().NotBeNull();
        capturedRequest.Tools!.Select(t => t.Name).Should().NotContain(["memory_search", "memory_save", "memory_get"]);
    }

    [Fact]
    public async Task ProcessAsync_CallsHooks()
    {
        var mockHook = new Mock<IAgentHook>();
        mockHook.Setup(h => h.OnBeforeAsync(It.IsAny<AgentHookContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockHook.Setup(h => h.OnAfterAsync(It.IsAny<AgentHookContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("ok", FinishReason.Stop));

        var loop = CreateLoop(mockProvider.Object, hooks: [mockHook.Object]);
        await loop.ProcessAsync(MakeMessage("hello"));

        mockHook.Verify(h => h.OnBeforeAsync(It.IsAny<AgentHookContext>(), It.IsAny<CancellationToken>()), Times.Once);
        mockHook.Verify(h => h.OnAfterAsync(It.IsAny<AgentHookContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UsesProviderFromModelPrefix_WhenConfigured()
    {
        var openAiProvider = new Mock<ILlmProvider>();
        openAiProvider.Setup(p => p.DefaultModel).Returns("gpt-4o");
        openAiProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        openAiProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("openai", FinishReason.Stop));

        var otherProvider = new Mock<ILlmProvider>();
        otherProvider.Setup(p => p.DefaultModel).Returns("claude-3-5-sonnet");
        otherProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        otherProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("other", FinishReason.Stop));

        var registry = new ProviderRegistry();
        registry.Register("other", otherProvider.Object);
        registry.Register("openai", openAiProvider.Object);

        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: new ToolRegistry(),
            settings: new GenerationSettings { Model = "openai:gpt-4o" },
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 5);

        var result = await loop.ProcessAsync(MakeMessage("hello"));

        result.Should().Be("openai");
        openAiProvider.Verify(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        otherProvider.Verify(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private AgentLoop CreateLoop(
        ILlmProvider provider,
        ToolRegistry? toolRegistry = null,
        IReadOnlyList<IAgentHook>? hooks = null)
    {
        var registry = new ProviderRegistry();
        registry.Register("test", provider);

        return new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry ?? new ToolRegistry(),
            settings: new GenerationSettings(),
            hooks: hooks,
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 5);
    }

    private static IContextBuilder CreateContextBuilder(string expectedAgentName)
    {
        var contextBuilder = new Mock<IContextBuilder>();
        contextBuilder
            .Setup(cb => cb.BuildSystemPromptAsync(expectedAgentName, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"You are {expectedAgentName}");
        contextBuilder
            .Setup(cb => cb.BuildMessagesAsync(
                expectedAgentName,
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<ChatMessage> history, string currentMessage, string? channel, string? chatId, CancellationToken _) =>
            {
                var messages = new List<ChatMessage>(history.Count + 2)
                {
                    new("system", $"You are {expectedAgentName}")
                };
                messages.AddRange(history);
                messages.Add(new("user", currentMessage));
                return messages;
            });

        return contextBuilder.Object;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
    }
}
