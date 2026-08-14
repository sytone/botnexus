using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using Shouldly;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests.Tools;

/// <summary>
/// Recall-time surfacing of provenance (#2480). The store may record where a memory came from,
/// but if the recall path drops it the model still reads laundered third-party text as its own
/// knowledge - so the rendering is asserted here, not just the persistence.
/// </summary>
public sealed class MemoryToolProvenanceTests
{
    [Fact]
    public async Task MemorySearchTool_RendersProvenanceForUntrustedEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("entry-untrusted", "agent-a", "laundereduntrustedtext") with
            {
                Provenance = MemoryProvenance.ExternalUntrusted
            });

        var tool = new MemorySearchTool(CreateAgentMemory(context), "agent-a");
        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["query"] = "laundereduntrustedtext" });

        GetText(result).ShouldContain($"Provenance: {MemoryProvenance.ExternalUntrusted}");
    }

    [Fact]
    public async Task MemorySearchTool_RendersUnknownForPreProvenanceEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("entry-legacy", "agent-a", "legacyprovenancetext"));

        var tool = new MemorySearchTool(CreateAgentMemory(context), "agent-a");
        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["query"] = "legacyprovenancetext" });

        // Never blank: an absent provenance must read as `unknown`, not as no concern at all.
        GetText(result).ShouldContain($"Provenance: {MemoryProvenance.Unknown}");
    }

    [Fact]
    public async Task MemoryGetTool_RendersProvenanceById()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("entry-get", "agent-a", "gettabletext") with
            {
                Provenance = MemoryProvenance.Tool
            });

        var tool = new MemoryGetTool(context.Store);
        var result = await tool.ExecuteAsync(
            "call-3",
            new Dictionary<string, object?> { ["id"] = "entry-get" });

        GetText(result).ShouldContain($"Provenance: {MemoryProvenance.Tool}");
    }

    [Fact]
    public async Task MarkdownAgentMemory_SearchResult_CarriesNormalizedProvenance()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("entry-map", "agent-a", "mappableprovenancetext") with
            {
                Provenance = MemoryProvenance.ExternalUntrusted,
                OriginConversationId = "conv-9"
            });

        var results = await CreateAgentMemory(context).SearchAsync(
            new Gateway.Contracts.Memory.AgentMemorySearchRequest("agent-a", "mappableprovenancetext"));

        results.ShouldHaveSingleItem();
        results[0].Provenance.ShouldBe(MemoryProvenance.ExternalUntrusted);
        results[0].OriginConversationId.ShouldBe("conv-9");
    }

    private static MarkdownAgentMemory CreateAgentMemory(MemoryStoreTestContext context)
        => new("agent-a", new ProvenanceStubWorkspaceManager(), context.Store, new FileSystem());

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;

    private sealed class ProvenanceStubWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: "", Identity: "", User: "", Memory: ""));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => $@"C:\agents\{agentName}\workspace";
    }
}
