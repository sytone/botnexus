using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemoryToolAdditionalTests
{
    [Fact]
    public async Task MemorySaveTool_PrepareArguments_MissingContent_Throws()
    {
        var tool = new MemorySaveTool(new SpyWorkspaceManager(), "agent-a");

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MemorySaveTool_StoresSpecialCharacters()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "agent-a");
        var payload = "special chars ; | ` \" ' and emoji 😀";

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["content"] = payload });

        workspaceManager.SaveCalls.Count.ShouldBe(1);
        workspaceManager.SaveCalls.Single().Content.ShouldBe(payload);
    }

    [Fact]
    public async Task MemorySaveTool_DuplicateContent_CreatesDistinctAppends()
    {
        var workspaceManager = new SpyWorkspaceManager();
        var tool = new MemorySaveTool(workspaceManager, "agent-a");
        var args = new Dictionary<string, object?> { ["content"] = "duplicate payload" };

        await tool.ExecuteAsync("call-1", args);
        await tool.ExecuteAsync("call-2", args);

        workspaceManager.SaveCalls.Count.ShouldBe(2);
        workspaceManager.SaveCalls[0].Content.ShouldBe("duplicate payload");
        workspaceManager.SaveCalls[1].Content.ShouldBe("duplicate payload");
    }

    [Fact]
    public async Task MemorySaveTool_PrepareArguments_IgnoresLegacyStoreArguments()
    {
        var tool = new MemorySaveTool(new SpyWorkspaceManager(), "agent-a");
        var prepared = await tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["content"] = "x", ["expiresInDays"] = "not-a-number", ["tags"] = new[] { "legacy" } });

        prepared.Count.ShouldBe(1);
        prepared["content"].ShouldBe("x");
    }

    [Fact]
    public async Task MemorySearchTool_PrepareArguments_EmptyQuery_Throws()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemorySearchTool(context.Store);

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "   " });

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MemorySearchTool_ExecuteWithFilters_ReturnsFilteredMatch()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("keep", "agent-a", "tool-filter-token", sourceType: "manual", sessionId: "s1", metadataJson: """{"tags":["release"]}"""));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("drop", "agent-a", "tool-filter-token", sourceType: "conversation", sessionId: "s2", metadataJson: """{"tags":["ops"]}"""));
        var tool = new MemorySearchTool(context.Store);

        var filter = JsonSerializer.Serialize(new
        {
            sourceType = "manual",
            sessionId = "s1",
            tags = new[] { "release" }
        });

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["query"] = "tool filter token", ["filter"] = filter });

        var text = GetText(result);
        text.ShouldContain("Found 1 memory entry:");
        text.ShouldContain("ID: keep");
        text.ShouldNotContain("ID: drop");
    }

    [Fact]
    public async Task MemorySearchTool_ExecuteWithEmptyQuery_ReturnsNoResults()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "hello world"));
        var tool = new MemorySearchTool(context.Store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["query"] = "" });

        GetText(result).ShouldBe("No matching memories found.");
    }

    [Fact]
    public async Task MemoryGetTool_PrepareArguments_WithoutIdOrSession_Throws()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryGetTool(context.Store);

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MemoryGetTool_GetAfterDelete_ReturnsNotFound()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("to-delete", "agent-a", "delete me"));
        await context.Store.DeleteAsync("to-delete");
        var tool = new MemoryGetTool(context.Store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["id"] = "to-delete" });

        GetText(result).ShouldBe("Memory entry not found.");
    }

    [Fact]
    public async Task MemoryGetTool_SessionLimit_StringInput_IsParsed()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "first", sessionId: "sess"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "second", sessionId: "sess"));
        var tool = new MemoryGetTool(context.Store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["sessionId"] = "sess", ["limit"] = "1" });

        var text = GetText(result);
        text.ShouldContain("Session 'sess' memories (1):");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MemorySearchTool_SQLInjectionStyleQuery_DoesNotLeakAllEntries()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "alpha secure token"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "beta secure token"));
        var tool = new MemorySearchTool(context.Store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["query"] = "' OR 1=1 --" });

        GetText(result).ShouldBe("No matching memories found.");
    }

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;

    private sealed class SpyWorkspaceManager : IAgentWorkspaceManager
    {
        public List<SaveMemoryCall> SaveCalls { get; } = [];

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: string.Empty, Identity: string.Empty, User: string.Empty, Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
        {
            SaveCalls.Add(new SaveMemoryCall(agentName, null, content, null));
            return Task.CompletedTask;
        }

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
        {
            SaveCalls.Add(new SaveMemoryCall(agentName, filePath, content, null));
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

        public string GetWorkspacePath(string agentName) => $@"C:\agents\{agentName}\workspace";
    }

    private sealed record SaveMemoryCall(string AgentName, string? FilePath, string Content, string? MemoryPathOverride);
}
