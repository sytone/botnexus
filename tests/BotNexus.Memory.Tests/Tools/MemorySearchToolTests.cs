using BotNexus.Agent.Core.Types;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using FluentAssertions;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithQuery_ReturnsFormattedResults()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "searchablememorytext"));
        var tool = new MemorySearchTool(context.Store);

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["query"] = "searchablememorytext" });

        var text = GetText(result);
        text.Should().Contain("Found 1 memory entry:");
        text.Should().Contain("ID: entry-1");
        text.Should().Contain("Preview: searchablememorytext");
    }

    [Fact]
    public async Task ExecuteAsync_WithNoResults_ReturnsEmptyMessage()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemorySearchTool(context.Store);

        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["query"] = "nothing-matches-here" });

        GetText(result).Should().Be("No matching memories found.");
    }

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;
}
