using BotNexus.Agent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-AWM-006: Memory consolidation (daily → MEMORY.md)
/// Validates the full consolidation pipeline with real filesystem operations:
/// daily files accumulated → consolidation triggered → MEMORY.md updated → daily files archived.
/// </summary>
public sealed class MemoryConsolidationE2eTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Directory.GetCurrentDirectory(),
        "memory-consolidation-e2e",
        Guid.NewGuid().ToString("N"));

    private readonly string _testHomePath;
    private readonly string? _originalHomeOverride;

    public MemoryConsolidationE2eTests()
    {
        _testHomePath = Path.Combine(_testRoot, ".botnexus");
        _originalHomeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _testHomePath);
        Directory.CreateDirectory(_testHomePath);
    }

    [Fact]
    public async Task Consolidate_WithMultipleDailyFiles_MergesIntoMemoryAndArchives()
    {
        // Arrange: set up agent workspace with real daily files
        var agentName = "consolidation-test-agent";
        BotNexusHome.Initialize();
        BotNexusHome.InitializeAgentWorkspace(agentName);

        var workspacePath = BotNexusHome.GetAgentWorkspacePath(agentName);
        var dailyDir = Path.Combine(workspacePath, "memory", "daily");

        var day1 = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3));
        var day2 = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-2));
        var day3 = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

        await File.WriteAllTextAsync(Path.Combine(dailyDir, $"{day1:yyyy-MM-dd}.md"), "[10:00] Learned about project Phoenix launch dates");
        await File.WriteAllTextAsync(Path.Combine(dailyDir, $"{day2:yyyy-MM-dd}.md"), "[14:00] User prefers dark mode\n[15:30] Team standup notes saved");
        await File.WriteAllTextAsync(Path.Combine(dailyDir, $"{day3:yyyy-MM-dd}.md"), "[09:00] Sprint planning outcomes captured");

        // Seed existing MEMORY.md
        var memoryPath = Path.Combine(workspacePath, "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memoryPath)!);
        await File.WriteAllTextAsync(memoryPath, "# Long-term Memory\n\n- User likes pizza\n");

        // Create real MemoryStore + fake LLM provider
        var store = new MemoryStore(workspacePath, NullLogger<MemoryStore>.Instance);

        var fakeProvider = new FakeConsolidationProvider(
            "# Long-term Memory\n\n- User likes pizza\n- Project Phoenix launches next quarter\n- User prefers dark mode\n- Sprint planning outcomes noted\n");

        var registry = new ProviderRegistry();
        registry.Register("mock", fakeProvider);

        var workspace = new FakeWorkspace(agentName, workspacePath);
        var factory = new FakeWorkspaceFactory(workspace);

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

        var consolidator = new MemoryConsolidator(
            store, factory, registry, Options.Create(config),
            NullLogger<MemoryConsolidator>.Instance);

        // Act
        var result = await consolidator.ConsolidateAsync(agentName);

        // Assert
        result.Success.Should().BeTrue();
        result.DailyFilesProcessed.Should().Be(3);
        result.EntriesConsolidated.Should().BeGreaterThan(0);

        // MEMORY.md should be updated
        var updatedMemory = await store.ReadAsync(agentName, "MEMORY");
        updatedMemory.Should().NotBeNullOrWhiteSpace();
        updatedMemory.Should().Contain("Phoenix");
        updatedMemory.Should().Contain("dark mode");

        // Daily files should be archived (not in daily/ anymore)
        Directory.EnumerateFiles(dailyDir, "*.md").Should().BeEmpty(
            "all processed daily files should be moved to archived/");

        // Archived directory should contain the processed files
        var archivedDir = Path.Combine(dailyDir, "archived");
        Directory.Exists(archivedDir).Should().BeTrue();
        Directory.EnumerateFiles(archivedDir, "*.md").Should().HaveCount(3);
    }

    [Fact]
    public async Task Consolidate_TodaysFile_IsNotProcessed()
    {
        var agentName = "consolidation-today-agent";
        BotNexusHome.Initialize();
        BotNexusHome.InitializeAgentWorkspace(agentName);

        var workspacePath = BotNexusHome.GetAgentWorkspacePath(agentName);
        var dailyDir = Path.Combine(workspacePath, "memory", "daily");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Only today's file — should be skipped
        var todayFile = Path.Combine(dailyDir, $"{today:yyyy-MM-dd}.md");
        await File.WriteAllTextAsync(todayFile, "[12:00] Today's active notes");

        var store = new MemoryStore(workspacePath, NullLogger<MemoryStore>.Instance);
        var fakeProvider = new FakeConsolidationProvider("should not be called");
        var registry = new ProviderRegistry();
        registry.Register("mock", fakeProvider);

        var workspace = new FakeWorkspace(agentName, workspacePath);
        var factory = new FakeWorkspaceFactory(workspace);
        var config = new BotNexusConfig
        {
            Agents = new AgentDefaults { Named = new Dictionary<string, AgentConfig> { [agentName] = new() } }
        };

        var consolidator = new MemoryConsolidator(
            store, factory, registry, Options.Create(config),
            NullLogger<MemoryConsolidator>.Instance);

        var result = await consolidator.ConsolidateAsync(agentName);

        result.Success.Should().BeTrue();
        result.DailyFilesProcessed.Should().Be(0);
        File.Exists(todayFile).Should().BeTrue("today's file must not be touched");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _originalHomeOverride);
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, recursive: true); } catch { }
        }
    }

    private sealed class FakeConsolidationProvider(string consolidatedOutput) : ILlmProvider
    {
        public string DefaultModel => "fake-model";
        public GenerationSettings Generation { get; set; } = new()
        {
            MaxTokens = 4096, Temperature = 0.0, ContextWindowTokens = 32000, MaxToolIterations = 5
        };

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { DefaultModel });
        }

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(consolidatedOutput, FinishReason.Stop));

        public async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return StreamingChatChunk.FromContentDelta((await ChatAsync(request, cancellationToken)).Content);
        }
    }

    private sealed class FakeWorkspace(string agentName, string workspacePath) : IAgentWorkspace
    {
        public string AgentName => agentName;
        public string WorkspacePath => workspacePath;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadFileAsync(string fileName, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendFileAsync(string fileName, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public bool FileExists(string fileName) => false;
    }

    private sealed class FakeWorkspaceFactory(IAgentWorkspace workspace) : IAgentWorkspaceFactory
    {
        public IAgentWorkspace Create(string agentName) => workspace;
    }
}
