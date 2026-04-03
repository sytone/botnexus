using BotNexus.Agent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class MemoryConsolidatorTests : IDisposable
{
    private readonly string _artifactRoot = Path.Combine(
        Directory.GetCurrentDirectory(),
        "memory-consolidator-test-artifacts",
        Guid.NewGuid().ToString("N"));

    public MemoryConsolidatorTests()
    {
        Directory.CreateDirectory(_artifactRoot);
    }

    [Fact]
    public async Task ConsolidateAsync_HappyPath_UsesLlmUpdatesMemoryAndArchivesDailies()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        var oldDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var oldFilePath = WriteDailyFile(workspacePath, oldDate, "[10:00] fact one");

        var store = CreateMemoryStoreMock("# Existing memory");
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("consolidated-memory", FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        var result = await sut.ConsolidateAsync(agentName);

        result.Should().Be(new MemoryConsolidationResult(true, 1, 1));
        provider.Verify(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.WriteAsync(agentName, "MEMORY", "consolidated-memory", It.IsAny<CancellationToken>()), Times.Once);
        File.Exists(oldFilePath).Should().BeFalse();
        File.Exists(Path.Combine(workspacePath, "memory", "daily", "archived", $"{oldDate:yyyy-MM-dd}.md")).Should().BeTrue();
    }

    [Fact]
    public async Task ConsolidateAsync_NoDailyFiles_ReturnsSuccessWithZeroProcessed()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        var store = CreateMemoryStoreMock();
        var provider = CreateProviderMock("default-model");
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        var result = await sut.ConsolidateAsync(agentName);

        result.Should().Be(new MemoryConsolidationResult(true, 0, 0));
        provider.Verify(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsolidateAsync_LlmFailure_FallsBackToRawAppendWithoutDataLoss()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-2)), "[09:00] keep this");

        var store = CreateMemoryStoreMock("# Existing memory");
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        var result = await sut.ConsolidateAsync(agentName);

        result.Success.Should().BeTrue();
        store.Verify(
            s => s.WriteAsync(
                agentName,
                "MEMORY",
                It.Is<string>(content => content.Contains("# Existing memory") &&
                                         content.Contains("## Consolidation Fallback") &&
                                         content.Contains("keep this")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConsolidateAsync_EmptyLlmContent_TriggersFallback(string llmOutput)
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-2)), "[09:00] fallback me");

        var store = CreateMemoryStoreMock("# Existing memory");
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse(llmOutput, FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        await sut.ConsolidateAsync(agentName);

        store.Verify(
            s => s.WriteAsync(
                agentName,
                "MEMORY",
                It.Is<string>(content => content.Contains("## Consolidation Fallback") && content.Contains("fallback me")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_MultipleDailyFiles_AreProcessedInDateOrder()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        var oldest = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3));
        var middle = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-2));
        var newest = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        WriteDailyFile(workspacePath, newest, "newest");
        WriteDailyFile(workspacePath, oldest, "oldest");
        WriteDailyFile(workspacePath, middle, "middle");

        var store = CreateMemoryStoreMock("# Existing memory");
        var provider = CreateProviderMock("default-model");
        ChatRequest? capturedRequest = null;
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new LlmResponse("ok", FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        var result = await sut.ConsolidateAsync(agentName);

        result.Should().Be(new MemoryConsolidationResult(true, 3, 3));
        capturedRequest.Should().NotBeNull();
        var prompt = capturedRequest!.Messages.Single().Content;
        prompt.IndexOf($"{oldest:yyyy-MM-dd}.md", StringComparison.Ordinal).Should()
            .BeLessThan(prompt.IndexOf($"{middle:yyyy-MM-dd}.md", StringComparison.Ordinal));
        prompt.IndexOf($"{middle:yyyy-MM-dd}.md", StringComparison.Ordinal).Should()
            .BeLessThan(prompt.IndexOf($"{newest:yyyy-MM-dd}.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsolidateAsync_ProcessesOnlyFilesOlderThanToday()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        WriteDailyFile(workspacePath, yesterday, "process me");
        var todayPath = WriteDailyFile(workspacePath, today, "preserve me");

        var store = CreateMemoryStoreMock("# Existing memory");
        var provider = CreateProviderMock("default-model");
        ChatRequest? capturedRequest = null;
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new LlmResponse("ok", FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        var result = await sut.ConsolidateAsync(agentName);

        result.DailyFilesProcessed.Should().Be(1);
        File.Exists(todayPath).Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Messages[0].Content.Should().Contain($"{yesterday:yyyy-MM-dd}.md");
        capturedRequest.Messages[0].Content.Should().NotContain($"{today:yyyy-MM-dd}.md");
    }

    [Fact]
    public async Task ConsolidateAsync_MemoryMissing_CreatesMemoryViaWrite()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)), "new fact");

        var store = CreateMemoryStoreMock(null);
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("new-memory", FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        await sut.ConsolidateAsync(agentName);

        store.Verify(s => s.WriteAsync(agentName, "MEMORY", "new-memory", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_ExistingMemoryContent_IsPreservedAndExtendedOnFallback()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)), "fresh detail");

        var store = CreateMemoryStoreMock("## Existing memory");
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse(string.Empty, FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        await sut.ConsolidateAsync(agentName);

        store.Verify(
            s => s.WriteAsync(
                agentName,
                "MEMORY",
                It.Is<string>(content => content.Contains("## Existing memory") &&
                                         content.Contains("## Consolidation Fallback") &&
                                         content.Contains("fresh detail")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_CreatesArchiveDirectoryWhenMissing()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)), "archive me");
        var archiveDirectory = Path.Combine(workspacePath, "memory", "daily", "archived");
        Directory.Exists(archiveDirectory).Should().BeFalse();

        var store = CreateMemoryStoreMock("# Existing");
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("done", FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        await sut.ConsolidateAsync(agentName);

        Directory.Exists(archiveDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task ConsolidateAsync_ConsolidationModelConfig_UsesSpecifiedModel()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)), "model test");

        var store = CreateMemoryStoreMock("# Existing");
        var provider = CreateProviderMock("provider-default");
        ChatRequest? capturedRequest = null;
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new LlmResponse("updated", FinishReason.Stop));
        var sut = CreateSut(
            agentName,
            workspacePath,
            store,
            [("mock", provider.Object)],
            config =>
            {
                config.Agents.Named[agentName] = new AgentConfig { ConsolidationModel = "custom-model" };
            });

        await sut.ConsolidateAsync(agentName);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Settings.Model.Should().Be("custom-model");
    }

    [Fact]
    public async Task ConsolidateAsync_Result_IsPopulatedWithCountsAndSuccessFlag()
    {
        var agentName = $"agent-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspace(agentName);
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-2)), "line 1\nline 2");
        WriteDailyFile(workspacePath, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)), "line 3\nline 4\nline 5");

        var store = CreateMemoryStoreMock("# Existing");
        var provider = CreateProviderMock("default-model");
        provider.Setup(p => p.ChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("updated", FinishReason.Stop));
        var sut = CreateSut(agentName, workspacePath, store, [("mock", provider.Object)]);

        var result = await sut.ConsolidateAsync(agentName);

        result.Success.Should().BeTrue();
        result.DailyFilesProcessed.Should().Be(2);
        result.EntriesConsolidated.Should().Be(5);
        result.Error.Should().BeNull();
    }

    private static Mock<IMemoryStore> CreateMemoryStoreMock(string? existingMemory = null)
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.ReadAsync(It.IsAny<string>(), "MEMORY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingMemory);
        store.Setup(s => s.WriteAsync(It.IsAny<string>(), "MEMORY", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        store.Setup(s => s.AppendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        store.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        store.Setup(s => s.ListKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        return store;
    }

    private static Mock<ILlmProvider> CreateProviderMock(string defaultModel)
    {
        var provider = new Mock<ILlmProvider>(MockBehavior.Strict);
        provider.SetupGet(p => p.DefaultModel).Returns(defaultModel);
        provider.SetupGet(p => p.Generation).Returns(new GenerationSettings
        {
            MaxTokens = 4096,
            Temperature = 0.25,
            ContextWindowTokens = 32000,
            MaxToolIterations = 12
        });
        provider.Setup(p => p.ChatStreamAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<StreamingChatChunk>());
        return provider;
    }

    private MemoryConsolidator CreateSut(
        string agentName,
        string workspacePath,
        Mock<IMemoryStore> store,
        IReadOnlyList<(string Name, ILlmProvider Provider)> providers,
        Action<BotNexusConfig>? configure = null)
    {
        var workspace = new Mock<IAgentWorkspace>(MockBehavior.Strict);
        workspace.SetupGet(w => w.AgentName).Returns(agentName);
        workspace.SetupGet(w => w.WorkspacePath).Returns(workspacePath);
        workspace.Setup(w => w.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workspaceFactory = new Mock<IAgentWorkspaceFactory>(MockBehavior.Strict);
        workspaceFactory.Setup(f => f.Create(agentName)).Returns(workspace.Object);

        var registry = new ProviderRegistry();
        foreach (var (name, provider) in providers)
            registry.Register(name, provider);

        var config = new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                Named = new Dictionary<string, AgentConfig>
                {
                    [agentName] = new()
                }
            }
        };
        configure?.Invoke(config);

        return new MemoryConsolidator(
            store.Object,
            workspaceFactory.Object,
            registry,
            Options.Create(config),
            NullLogger<MemoryConsolidator>.Instance);
    }

    private string CreateWorkspace(string agentName)
    {
        var workspacePath = Path.Combine(_artifactRoot, agentName);
        Directory.CreateDirectory(Path.Combine(workspacePath, "memory", "daily"));
        return workspacePath;
    }

    private static string WriteDailyFile(string workspacePath, DateOnly date, string content)
    {
        var filePath = Path.Combine(workspacePath, "memory", "daily", $"{date:yyyy-MM-dd}.md");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_artifactRoot))
            Directory.Delete(_artifactRoot, recursive: true);
    }
}
