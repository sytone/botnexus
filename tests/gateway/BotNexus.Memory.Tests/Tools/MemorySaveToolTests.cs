using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Tools;
using System.Text.Json;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySaveToolTests
{
    [Fact]
    public async Task PrepareArgumentsAsync_WithContentOnly_KeepsLegacyContract()
    {
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        var prepared = await tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["content"] = "legacy content-only payload" });

        prepared.Count.ShouldBe(1);
        prepared.ShouldContainKey("content");
        prepared["content"].ShouldBe("legacy content-only payload");
        prepared.ShouldNotContainKey("file_path");
    }

    [Fact]
    public void Definition_UsesCanonicalMemorySaveNamingWithoutMemoryStoreTerminology()
    {
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        tool.Name.ShouldBe("memory_save");
        tool.Definition.Name.ShouldBe("memory_save");
        tool.Definition.Description.ShouldNotContain("memory store", Case.Insensitive);
    }

    [Fact]
    public void Definition_RequiresContentForLegacyContentOnlyCalls()
    {
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        var required = tool.Definition.Parameters.GetProperty("required")
            .EnumerateArray()
            .Select(static node => node.GetString())
            .ToArray();

        required.ShouldContain("content");
    }

    [Fact]
    public async Task ExecuteAsync_WithContentOnly_DelegatesToAgentMemorySaveAsync()
    {
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["content"] = "daily memory entry" });

        memory.SaveCalls.Count.ShouldBe(1);
        var call = memory.SaveCalls.Single();
        call.AgentId.ShouldBe("farnsworth");
        call.Content.ShouldBe("daily memory entry");
        call.SourceType.ShouldBe("tool");
    }

    [Fact]
    public async Task ExecuteAsync_WithFilePath_FallsBackToSaveAsync_WhenNotMarkdownMemory()
    {
        // When IAgentMemory is not a MarkdownAgentMemory, file_path falls back to SaveAsync
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?>
            {
                ["content"] = "handoff note",
                ["file_path"] = @"memory\handoff.md"
            });

        // Falls through to SaveAsync since SpyAgentMemory is not MarkdownAgentMemory
        memory.SaveCalls.Count.ShouldBe(1);
        memory.SaveCalls.Single().Content.ShouldBe("handoff note");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccessMessageWithFilePath()
    {
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        var result = await tool.ExecuteAsync(
            "call-3",
            new Dictionary<string, object?>
            {
                ["content"] = "note",
                ["file_path"] = "handoff.md"
            });

        result.Content.Count.ShouldBe(1);
        result.Content[0].Value.ShouldContain("handoff.md");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccessMessageForDefaultTarget()
    {
        var memory = new SpyAgentMemory();
        var tool = new MemorySaveTool(memory, "farnsworth");

        var result = await tool.ExecuteAsync(
            "call-4",
            new Dictionary<string, object?> { ["content"] = "note" });

        result.Content[0].Value.ShouldContain("default memory target");
    }

    [Fact]
    public void Constructor_ThrowsOnNullAgentMemory()
    {
        Should.Throw<ArgumentNullException>(() => new MemorySaveTool(null!, "farnsworth"));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyAgentId()
    {
        var memory = new SpyAgentMemory();
        Should.Throw<ArgumentException>(() => new MemorySaveTool(memory, ""));
    }

    private sealed class SpyAgentMemory : IAgentMemory
    {
        public List<AgentMemorySaveRequest> SaveCalls { get; } = [];

        public Task<AgentMemoryContext> GetPromptContextAsync(AgentMemoryPromptRequest request, CancellationToken ct = default)
            => Task.FromResult(AgentMemoryContext.Empty);

        public Task SaveAsync(AgentMemorySaveRequest request, CancellationToken ct = default)
        {
            SaveCalls.Add(request);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(AgentMemorySearchRequest request, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([]);

        public Task<AgentMemorySearchResult?> GetAsync(string entryId, CancellationToken ct = default)
            => Task.FromResult<AgentMemorySearchResult?>(null);

        public Task OnSessionCompleteAsync(AgentMemorySessionEvent sessionEvent, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ConsolidateAsync(AgentMemoryConsolidateRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
