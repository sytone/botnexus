using BotNexus.Agent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Unit.Tests;

[Collection("BotNexusHomeEnvVar")]
public sealed class MemoryConsolidatorTests : IDisposable
{
    private const string HomeOverrideEnvVar = "BOTNEXUS_HOME";
    private readonly string? _originalHomeOverride;
    private readonly string _tempHomePath;
    private readonly string _legacyBasePath;

    public MemoryConsolidatorTests()
    {
        _originalHomeOverride = Environment.GetEnvironmentVariable(HomeOverrideEnvVar);
        _tempHomePath = Path.Combine(Path.GetTempPath(), $"botnexus-home-consolidation-test-{Guid.NewGuid():N}");
        _legacyBasePath = Path.Combine(Path.GetTempPath(), $"botnexus-legacy-consolidation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHomePath);
        Directory.CreateDirectory(_legacyBasePath);
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _tempHomePath);
    }

    [Fact]
    public async Task ConsolidateAsync_WithOldDailyFiles_UsesLlmAndArchivesFiles()
    {
        var agentName = $"bender-consolidate-{Guid.NewGuid():N}";
        var store = new MemoryStore(_legacyBasePath, NullLogger<MemoryStore>.Instance);
        var workspace = BotNexusHome.GetAgentWorkspacePath(agentName);
        Directory.CreateDirectory(Path.Combine(workspace, "memory", "daily"));
        await store.WriteAsync(agentName, "MEMORY", "# Memory\n\nexisting");
        var oldDate = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");
        await store.WriteAsync(agentName, $"daily/{oldDate}", "[10:00] useful fact");

        var provider = new FakeLlmProvider("consolidated-memory-output");
        var registry = new ProviderRegistry([provider]);
        var consolidator = CreateConsolidator(store, registry);

        var result = await consolidator.ConsolidateAsync(agentName);

        result.Success.Should().BeTrue();
        result.DailyFilesProcessed.Should().Be(1);
        result.EntriesConsolidated.Should().Be(1);
        (await store.ReadAsync(agentName, "MEMORY")).Should().Be("consolidated-memory-output");
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.SystemPrompt.Should().Be("You are a memory consolidation agent. Review the daily notes and update the long-term memory.");
        provider.LastRequest.Messages.Should().ContainSingle();
        provider.LastRequest.Messages[0].Content.Should().Contain("Current long-term memory:");
        provider.LastRequest.Messages[0].Content.Should().Contain("Daily notes to process:");
        File.Exists(Path.Combine(workspace, "memory", "daily", $"{oldDate}.md")).Should().BeFalse();
        File.Exists(Path.Combine(workspace, "memory", "daily", "archived", $"{oldDate}.md")).Should().BeTrue();
    }

    [Fact]
    public async Task ConsolidateAsync_LlmFailure_FallsBackToAppendingDailyContent()
    {
        var agentName = $"bender-consolidate-fallback-{Guid.NewGuid():N}";
        var store = new MemoryStore(_legacyBasePath, NullLogger<MemoryStore>.Instance);
        var workspace = BotNexusHome.GetAgentWorkspacePath(agentName);
        Directory.CreateDirectory(Path.Combine(workspace, "memory", "daily"));
        await store.WriteAsync(agentName, "MEMORY", "# Memory");
        var oldDate = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd");
        await store.WriteAsync(agentName, $"daily/{oldDate}", "[09:00] keep this data");

        var registry = new ProviderRegistry([new ThrowingLlmProvider()]);
        var consolidator = CreateConsolidator(store, registry);

        var result = await consolidator.ConsolidateAsync(agentName);
        var memory = await store.ReadAsync(agentName, "MEMORY");

        result.Success.Should().BeTrue();
        result.DailyFilesProcessed.Should().Be(1);
        memory.Should().Contain("## Consolidation Fallback");
        memory.Should().Contain("keep this data");
        File.Exists(Path.Combine(workspace, "memory", "daily", "archived", $"{oldDate}.md")).Should().BeTrue();
    }

    [Fact]
    public async Task ConsolidateAsync_NoEligibleDailyFiles_ReturnsNoOp()
    {
        var agentName = $"bender-consolidate-noop-{Guid.NewGuid():N}";
        var store = new MemoryStore(_legacyBasePath, NullLogger<MemoryStore>.Instance);
        var workspace = BotNexusHome.GetAgentWorkspacePath(agentName);
        Directory.CreateDirectory(Path.Combine(workspace, "memory", "daily"));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await store.WriteAsync(agentName, $"daily/{today}", "[11:00] today only");

        var provider = new FakeLlmProvider("unused");
        var consolidator = CreateConsolidator(store, new ProviderRegistry([provider]));

        var result = await consolidator.ConsolidateAsync(agentName);

        result.Success.Should().BeTrue();
        result.DailyFilesProcessed.Should().Be(0);
        result.EntriesConsolidated.Should().Be(0);
        provider.Calls.Should().Be(0);
        File.Exists(Path.Combine(workspace, "memory", "daily", $"{today}.md")).Should().BeTrue();
    }

    private static MemoryConsolidator CreateConsolidator(IMemoryStore memoryStore, ProviderRegistry registry)
    {
        var config = Options.Create(new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                Named = new Dictionary<string, AgentConfig>
                {
                    ["default"] = new()
                }
            }
        });

        return new MemoryConsolidator(
            memoryStore,
            new AgentWorkspaceFactory(),
            registry,
            config,
            NullLogger<MemoryConsolidator>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _originalHomeOverride);
        if (Directory.Exists(_tempHomePath))
            Directory.Delete(_tempHomePath, recursive: true);
        if (Directory.Exists(_legacyBasePath))
            Directory.Delete(_legacyBasePath, recursive: true);
    }

    private sealed class FakeLlmProvider(string response) : ILlmProvider
    {
        public int Calls { get; private set; }
        public ChatRequest? LastRequest { get; private set; }
        public string DefaultModel => "fake-default";
        public GenerationSettings Generation { get; set; } = new();

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new LlmResponse(response, FinishReason.Stop));
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingLlmProvider : ILlmProvider
    {
        public string DefaultModel => "throwing";
        public GenerationSettings Generation { get; set; } = new();

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<LlmResponse>(new InvalidOperationException("provider down"));

        public async IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
