using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithQuery_ReturnsFormattedResults()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "searchablememorytext"));
        var agentMemory = CreateAgentMemory(context);
        var tool = new MemorySearchTool(agentMemory, "agent-a");

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["query"] = "searchablememorytext" });

        var text = GetText(result);
        text.ShouldContain("Found 1 memory entry:");
        text.ShouldContain("ID: entry-1");
        text.ShouldContain("Preview: searchablememorytext");
    }

    [Fact]
    public async Task ExecuteAsync_WithNoResults_ReturnsEmptyMessage()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var agentMemory = CreateAgentMemory(context);
        var tool = new MemorySearchTool(agentMemory, "agent-a");

        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["query"] = "nothing-matches-here" });

        GetText(result).ShouldBe("No matching memories found.");
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
