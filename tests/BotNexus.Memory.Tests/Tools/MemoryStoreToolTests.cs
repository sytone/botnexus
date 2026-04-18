using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using FluentAssertions;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemoryStoreToolTests
{
    [Fact]
    public async Task ExecuteAsync_StoresEntryWithManualSourceType()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryStoreTool(context.Store, "agent-a");

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["content"] = "persist this fact" });

        var response = GetText(result);
        response.Should().StartWith("Stored memory entry ");
        var id = response.Split(' ')[3];
        var inserted = await context.Store.GetByIdAsync(id);

        inserted.Should().NotBeNull();
        inserted!.AgentId.Should().Be("agent-a");
        inserted.SourceType.Should().Be("manual");
        inserted.Content.Should().Be("persist this fact");
    }

    [Fact]
    public async Task ExecuteAsync_WithTags_StoresTagsInMetadata()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryStoreTool(context.Store, "agent-a");

        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["content"] = "memorywithtags", ["tags"] = new[] { "release", "important" } });
        var id = GetText(result).Split(' ')[3];
        var stored = await context.Store.GetByIdAsync(id);
        stored.Should().NotBeNull();
        stored.MetadataJson.Should().NotBeNull();
        using var metadata = JsonDocument.Parse(stored.MetadataJson!);
        var tags = metadata.RootElement.GetProperty("tags").EnumerateArray().Select(node => node.GetString()).ToArray();
        tags.Should().BeEquivalentTo(["release", "important"]);
    }

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;
}
