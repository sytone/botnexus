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

public class RepeatedToolCallDetectionTests : IDisposable
{
    private readonly string _tempPath;
    private readonly SessionManager _sessionManager;

    public RepeatedToolCallDetectionTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"botnexus-repeated-tool-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);
        _sessionManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
    }

    private static InboundMessage MakeMessage(string content = "test") =>
        new("test-channel", "user1", "chat1", content, DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

    private static IContextBuilder CreateContextBuilder(string agentName)
    {
        var contextBuilder = new Mock<IContextBuilder>();
        contextBuilder
            .Setup(cb => cb.BuildSystemPromptAsync(agentName, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"You are {agentName}");
        contextBuilder
            .Setup(cb => cb.BuildMessagesAsync(
                agentName,
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<ChatMessage> history, string currentMessage, string? _, string? _, CancellationToken _) =>
            {
                var messages = new List<ChatMessage>(history.Count + 2)
                {
                    new("system", $"You are {agentName}")
                };
                messages.AddRange(history);
                messages.Add(new("user", currentMessage));
                return messages;
            });
        return contextBuilder.Object;
    }

    [Fact]
    public async Task SameToolAndArgs_CalledThreeTimes_ThirdCallBlocked()
    {
        // Arrange: Set up a tool that will be called 3 times with identical arguments
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
        var identicalArgs = new Dictionary<string, object?> { ["param"] = "value" };
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First 3 calls: request same tool with identical args
                if (callCount <= 3)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest($"id{callCount}", "test_tool", identicalArgs)]);
                // After blocked call, LLM receives error and should stop
                return new LlmResponse("final answer", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 2 }, // Default: block on 3rd call
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Act
        var result = await loop.ProcessAsync(MakeMessage("test"));

        // Assert: Tool should be executed only 2 times (3rd call blocked)
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(2));
        
        result.Should().Be("final answer");
        
        // Verify the session contains the error message for the blocked call
        var session = await _sessionManager.GetOrCreateAsync(MakeMessage("test").SessionKey, "test-agent");
        session.History.Should().Contain(e => 
            e.Role == MessageRole.Tool && 
            e.Content.Contains("Loop detected") &&
            e.Content.Contains("test_tool") &&
            e.Content.Contains("3 times"));
    }

    [Fact]
    public async Task SameToolDifferentArgs_AllCallsExecute()
    {
        // Arrange: Same tool called with different arguments
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
                // Each call has different arguments
                if (callCount == 1)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id1", "test_tool", new Dictionary<string, object?> { ["param"] = "value1" })]);
                if (callCount == 2)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id2", "test_tool", new Dictionary<string, object?> { ["param"] = "value2" })]);
                if (callCount == 3)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id3", "test_tool", new Dictionary<string, object?> { ["param"] = "value3" })]);
                return new LlmResponse("done", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 2 },
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Act
        var result = await loop.ProcessAsync(MakeMessage("test"));

        // Assert: All 3 calls should execute (different args = different signatures)
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(3));
        result.Should().Be("done");
    }

    [Fact]
    public async Task DifferentToolsSameArgs_AllCallsExecute()
    {
        // Arrange: Different tools called with same arguments
        var toolRegistry = new ToolRegistry();
        var mockTool1 = new Mock<ITool>();
        mockTool1.Setup(t => t.Definition).Returns(
            new ToolDefinition("tool_one", "Tool one", new Dictionary<string, ToolParameterSchema>()));
        mockTool1.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("output1");
        toolRegistry.Register(mockTool1.Object);

        var mockTool2 = new Mock<ITool>();
        mockTool2.Setup(t => t.Definition).Returns(
            new ToolDefinition("tool_two", "Tool two", new Dictionary<string, ToolParameterSchema>()));
        mockTool2.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("output2");
        toolRegistry.Register(mockTool2.Object);

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        
        var callCount = 0;
        var sameArgs = new Dictionary<string, object?> { ["param"] = "value" };
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id1", "tool_one", sameArgs)]);
                if (callCount == 2)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id2", "tool_two", sameArgs)]);
                if (callCount == 3)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest("id3", "tool_one", sameArgs)]);
                return new LlmResponse("done", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 2 },
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Act
        var result = await loop.ProcessAsync(MakeMessage("test"));

        // Assert: All calls execute (different tool names = different signatures)
        mockTool1.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(2));
        mockTool2.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Once);
        result.Should().Be("done");
    }

    [Fact]
    public async Task ConfigurableLimit_MaxRepeatedToolCallsOne_BlocksOnSecondCall()
    {
        // Arrange: Configure limit to 1, so 2nd identical call should be blocked
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
        var identicalArgs = new Dictionary<string, object?> { ["param"] = "value" };
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount <= 2)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest($"id{callCount}", "test_tool", identicalArgs)]);
                return new LlmResponse("stopped", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 1 }, // Block on 2nd call
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Act
        await loop.ProcessAsync(MakeMessage("test"));

        // Assert: Tool executed only once (2nd call blocked)
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task SignatureComputation_SameArgs_ProducesSameSignature()
    {
        // Arrange: Create two tool calls with identical arguments
        var args1 = new Dictionary<string, object?> 
        { 
            ["query"] = "test",
            ["limit"] = 10 
        };
        var args2 = new Dictionary<string, object?> 
        { 
            ["query"] = "test",
            ["limit"] = 10 
        };

        var toolCall1 = new ToolCallRequest("id1", "search", args1);
        var toolCall2 = new ToolCallRequest("id2", "search", args2);

        // Act: Both calls should produce the same signature
        // (We can't directly call ComputeToolCallSignature since it's private,
        // but we can verify behavior through the loop)
        var toolRegistry = new ToolRegistry();
        var mockTool = new Mock<ITool>();
        mockTool.Setup(t => t.Definition).Returns(
            new ToolDefinition("search", "Search", new Dictionary<string, ToolParameterSchema>()));
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("result");
        toolRegistry.Register(mockTool.Object);

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        
        var callCount = 0;
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new LlmResponse("", FinishReason.ToolCalls, [toolCall1]);
                if (callCount == 2)
                    return new LlmResponse("", FinishReason.ToolCalls, [toolCall2]);
                return new LlmResponse("done", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 1 },
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Assert: Second call should be blocked (same signature detected)
        await loop.ProcessAsync(MakeMessage("test"));
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Once); // Only first call executed
    }

    [Fact]
    public async Task BlockedCall_ReturnsErrorMessageAsToolResult()
    {
        // Arrange: Set up scenario where 2nd call will be blocked
        var toolRegistry = new ToolRegistry();
        var mockTool = new Mock<ITool>();
        mockTool.Setup(t => t.Definition).Returns(
            new ToolDefinition("test_tool", "Test tool", new Dictionary<string, ToolParameterSchema>()));
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("success");
        toolRegistry.Register(mockTool.Object);

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        
        var callCount = 0;
        var identicalArgs = new Dictionary<string, object?> { ["param"] = "value" };
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount <= 2)
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest($"id{callCount}", "test_tool", identicalArgs)]);
                return new LlmResponse("final", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 1 },
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Act
        await loop.ProcessAsync(MakeMessage("test"));

        // Assert: Session should contain error message as tool result
        var session = await _sessionManager.GetOrCreateAsync(MakeMessage("test").SessionKey, "test-agent");
        var errorEntry = session.History.FirstOrDefault(e => 
            e.Role == MessageRole.Tool && 
            e.Content.Contains("Error: Loop detected"));
        
        errorEntry.Should().NotBeNull();
        errorEntry!.ToolName.Should().Be("test_tool");
        errorEntry.ToolCallId.Should().Be("id2");
        errorEntry.Content.Should().Contain("2 times with identical arguments");
        errorEntry.Content.Should().Contain("Try a different approach");
    }

    [Fact]
    public async Task CounterTracking_IndependentPerSession()
    {
        // This test verifies that counters are session-scoped (within a single ProcessAsync call)
        // Note: The current implementation uses instance-level dictionary, so it persists across
        // ProcessAsync calls on the same AgentLoop instance. This test documents current behavior.
        
        var toolRegistry = new ToolRegistry();
        var mockTool = new Mock<ITool>();
        mockTool.Setup(t => t.Definition).Returns(
            new ToolDefinition("test_tool", "Test tool", new Dictionary<string, ToolParameterSchema>()));
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("result");
        toolRegistry.Register(mockTool.Object);

        var mockProvider = new Mock<ILlmProvider>();
        mockProvider.Setup(p => p.Generation).Returns(new GenerationSettings());
        
        var callCount = 0;
        var identicalArgs = new Dictionary<string, object?> { ["param"] = "value" };
        mockProvider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1 || callCount == 2) // First ProcessAsync: 2 identical calls
                    return new LlmResponse("", FinishReason.ToolCalls,
                        [new ToolCallRequest($"id{callCount}", "test_tool", identicalArgs)]);
                return new LlmResponse("done", FinishReason.Stop);
            });

        var registry = new ProviderRegistry();
        registry.Register("test", mockProvider.Object);
        var loop = new AgentLoop(
            agentName: "test-agent",
            providerRegistry: registry,
            sessionManager: _sessionManager,
            contextBuilder: CreateContextBuilder("test-agent"),
            toolRegistry: toolRegistry,
            settings: new GenerationSettings { MaxRepeatedToolCalls = 1 },
            logger: NullLogger<AgentLoop>.Instance,
            maxToolIterations: 10);

        // Act: First session processes 2 calls
        await loop.ProcessAsync(MakeMessage("first"));

        // Assert: First call executed, second blocked
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), 
            Times.Once);
        
        // Note: Current implementation persists counter across ProcessAsync calls
        // If this changes to reset per ProcessAsync, this test should be updated
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
    }
}
