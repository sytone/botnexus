using BotNexus.Agent.Core.Types;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using FluentAssertions;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemoryGetToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithId_ReturnsEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "stored content", sessionId: "session-1"));
        var tool = new MemoryGetTool(context.Store);

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["id"] = "entry-1" });

        var text = GetText(result);
        text.Should().Contain("ID: entry-1");
        text.Should().Contain("Content: stored content");
    }

    [Fact]
    public async Task ExecuteAsync_WithSessionId_ReturnsSessionEntries()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "first", sessionId: "session-1", createdAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "second", sessionId: "session-1"));
        var tool = new MemoryGetTool(context.Store);

        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["sessionId"] = "session-1" });

        var text = GetText(result);
        text.Should().Contain("Session 'session-1' memories (2):");
        text.Should().Contain("ID: entry-1");
        text.Should().Contain("ID: entry-2");
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownId_ReturnsNotFound()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryGetTool(context.Store);

        var result = await tool.ExecuteAsync(
            "call-3",
            new Dictionary<string, object?> { ["id"] = "missing-id" });

        GetText(result).Should().Be("Memory entry not found.");
    }

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;
}
