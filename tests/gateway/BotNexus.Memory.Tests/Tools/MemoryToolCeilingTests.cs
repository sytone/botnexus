using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests.Tools;

/// <summary>
/// Verifies that <see cref="MemoryGetTool"/> and <see cref="MemorySearchTool"/> clamp the
/// caller-supplied <c>limit</c> / <c>topK</c> to a configured upper bound. Without a ceiling an
/// agent (or a poisoned cron prompt) could request an enormous value and drive an unbounded
/// fetch plus serialization of the entire store into a single tool result.
/// </summary>
public sealed class MemoryToolCeilingTests
{
    // -------------------- MemoryGetTool.limit --------------------

    [Fact]
    public async Task MemoryGet_LimitAboveCeiling_IsClampedToMaxLimit()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        // Insert more entries than the (deliberately tiny) ceiling so the clamp is observable.
        for (var i = 0; i < 5; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"entry-{i}", "agent-a", $"content-{i}", sessionId: "session-1",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var tool = new MemoryGetTool(context.Store, maxLimit: 2);

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["sessionId"] = "session-1", ["limit"] = 1000 });

        // Clamped to 2 entries despite requesting 1000.
        GetText(result).ShouldContain("Session 'session-1' memories (2):");
    }

    [Fact]
    public async Task MemoryGet_LimitWithinCeiling_IsHonoured()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"entry-{i}", "agent-a", $"content-{i}", sessionId: "session-1",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var tool = new MemoryGetTool(context.Store, maxLimit: 100);

        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["sessionId"] = "session-1", ["limit"] = 3 });

        GetText(result).ShouldContain("Session 'session-1' memories (3):");
    }

    [Fact]
    public async Task MemoryGet_LimitBelowOne_IsClampedToOne()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"entry-{i}", "agent-a", $"content-{i}", sessionId: "session-1",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var tool = new MemoryGetTool(context.Store, maxLimit: 100);

        var result = await tool.ExecuteAsync(
            "call-3",
            new Dictionary<string, object?> { ["sessionId"] = "session-1", ["limit"] = 0 });

        GetText(result).ShouldContain("Session 'session-1' memories (1):");
    }

    [Fact]
    public void MemoryGet_DefaultMaxLimit_IsOneHundred()
        => MemoryGetTool.DefaultMaxLimit.ShouldBe(100);

    // -------------------- MemorySearchTool.topK --------------------

    [Fact]
    public async Task MemorySearch_TopKAboveCeiling_IsClampedToMaxTopK()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        // Insert more matching entries than the tiny MaxTopK so the clamp limits the result set.
        for (var i = 0; i < 6; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"entry-{i}", "agent-a", "searchablememorytext",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var config = new MemoryAgentConfig { Search = new MemorySearchAgentConfig { DefaultTopK = 1, MaxTopK = 2 } };
        var agentMemory = CreateAgentMemory(context);
        var tool = new MemorySearchTool(agentMemory, "agent-a", config);

        var result = await tool.ExecuteAsync(
            "call-4",
            new Dictionary<string, object?> { ["query"] = "searchablememorytext", ["topK"] = 1000 });

        // Clamped to 2 results despite requesting 1000.
        GetText(result).ShouldContain("Found 2 memory entries:");
    }

    [Fact]
    public async Task MemorySearch_TopKWithinCeiling_IsHonoured()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"entry-{i}", "agent-a", "searchablememorytext",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var config = new MemoryAgentConfig { Search = new MemorySearchAgentConfig { MaxTopK = 100 } };
        var agentMemory = CreateAgentMemory(context);
        var tool = new MemorySearchTool(agentMemory, "agent-a", config);

        var result = await tool.ExecuteAsync(
            "call-5",
            new Dictionary<string, object?> { ["query"] = "searchablememorytext", ["topK"] = 3 });

        GetText(result).ShouldContain("Found 3 memory entries:");
    }

    [Fact]
    public async Task MemorySearch_MaxTopKNeverBelowDefaultTopK()
    {
        // A misconfigured MaxTopK lower than DefaultTopK must not throttle the default below itself.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        for (var i = 0; i < 8; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"entry-{i}", "agent-a", "searchablememorytext",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        // DefaultTopK 5 but MaxTopK misconfigured to 1 -> effective ceiling floors at DefaultTopK (5).
        var config = new MemoryAgentConfig
        {
            Search = new MemorySearchAgentConfig { DefaultTopK = 5, MaxTopK = 1 }
        };
        var agentMemory = CreateAgentMemory(context);
        var tool = new MemorySearchTool(agentMemory, "agent-a", config);

        var result = await tool.ExecuteAsync(
            "call-6",
            new Dictionary<string, object?> { ["query"] = "searchablememorytext", ["topK"] = 1000 });

        GetText(result).ShouldContain("Found 5 memory entries:");
    }

    private static MarkdownAgentMemory CreateAgentMemory(MemoryStoreTestContext context)
        => new("agent-a", new StubWorkspaceManager(), context.Store, new FileSystem());

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: "", Identity: "", User: "", Memory: ""));
        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;
        public string GetWorkspacePath(string agentName) => $@"C:\agents\{agentName}\workspace";
    }
}
