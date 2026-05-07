using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory.Tools;
using System.Text.Json;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySaveToolTests
{
    [Fact]
    public async Task PrepareArgumentsAsync_WithContentOnly_KeepsLegacyContract()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "farnsworth");

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
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "farnsworth");

        tool.Name.ShouldBe("memory_save");
        tool.Definition.Name.ShouldBe("memory_save");
        tool.Definition.Description.ShouldNotContain("memory store", Case.Insensitive);
    }

    [Fact]
    public void Definition_RequiresContentForLegacyContentOnlyCalls()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "farnsworth");

        var required = tool.Definition.Parameters.GetProperty("required")
            .EnumerateArray()
            .Select(static node => node.GetString())
            .ToArray();

        required.ShouldContain("content");
    }

    [Fact]
    public async Task ExecuteAsync_WithContentOnly_DelegatesToWorkspaceManagerWithNullFilePath()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "farnsworth", memoryPathOverride: "journals");

        await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["content"] = "daily memory entry" });

        workspaceManager.SaveCalls.Count.ShouldBe(1);
        var call = workspaceManager.SaveCalls.Single();
        call.AgentName.ShouldBe("farnsworth");
        call.FilePath.ShouldBeNull();
        call.Content.ShouldBe("daily memory entry");
        call.MemoryPathOverride.ShouldBe("journals");
    }

    [Fact]
    public async Task ExecuteAsync_WithFilePath_DelegatesToWorkspaceManagerWithoutRewritingPath()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "farnsworth");

        await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?>
            {
                ["content"] = "handoff note",
                ["file_path"] = @"memory\handoff.md"
            });

        workspaceManager.SaveCalls.Count.ShouldBe(1);
        var call = workspaceManager.SaveCalls.Single();
        call.AgentName.ShouldBe("farnsworth");
        call.FilePath.ShouldBe(@"memory\handoff.md");
        call.Content.ShouldBe("handoff note");
    }

    [Fact]
    public async Task ExecuteAsync_WithFilePathAndOverride_DelegatesBothValuesToWorkspaceManager()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "farnsworth", memoryPathOverride: "journals");

        await tool.ExecuteAsync(
            "call-override",
            new Dictionary<string, object?>
            {
                ["content"] = "explicit path note",
                ["file_path"] = "handoff.md"
            });

        workspaceManager.SaveCalls.Count.ShouldBe(1);
        var call = workspaceManager.SaveCalls.Single();
        call.AgentName.ShouldBe("farnsworth");
        call.FilePath.ShouldBe("handoff.md");
        call.Content.ShouldBe("explicit path note");
        call.MemoryPathOverride.ShouldBe("journals");
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotUseWorkspacePathWhenSavingMemory()
    {
        var workspaceManager = new SpyWorkspaceManager { ThrowOnGetWorkspacePath = true };
        var tool = new MemorySaveTool(workspaceManager, "farnsworth");

        await tool.ExecuteAsync(
            "call-3",
            new Dictionary<string, object?> { ["content"] = "delegation only" });

        workspaceManager.SaveCalls.Count.ShouldBe(1);
        workspaceManager.GetWorkspacePathCallCount.ShouldBe(0);
    }

    private sealed class SpyWorkspaceManager : IAgentWorkspaceManager
    {
        public List<SaveMemoryCall> SaveCalls { get; } = [];

        public bool ThrowOnGetWorkspacePath { get; init; }

        public int GetWorkspacePathCallCount { get; private set; }

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: string.Empty, Identity: string.Empty, User: string.Empty, Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
        {
            SaveCalls.Add(new SaveMemoryCall(agentName, null, content, MemoryPathOverride: null));
            return Task.CompletedTask;
        }

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
        {
            SaveCalls.Add(new SaveMemoryCall(agentName, filePath, content, MemoryPathOverride: null));
            return Task.CompletedTask;
        }

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken ct = default)
        {
            SaveCalls.Add(new SaveMemoryCall(agentName, filePath, content, memoryPathOverride));
            return Task.CompletedTask;
        }

        public string GetWorkspacePath(string agentName)
        {
            GetWorkspacePathCallCount++;
            if (ThrowOnGetWorkspacePath)
                throw new InvalidOperationException("MemorySaveTool must delegate through IAgentWorkspaceManager.SaveMemoryAsync.");

            return $@"C:\agents\{agentName}\workspace";
        }
    }

    private sealed record SaveMemoryCall(string AgentName, string? FilePath, string Content, string? MemoryPathOverride);
}
