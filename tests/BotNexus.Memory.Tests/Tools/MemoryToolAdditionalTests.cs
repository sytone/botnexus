using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using FluentAssertions;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemoryToolAdditionalTests
{
    [Fact]
    public async Task MemoryStoreTool_PrepareArguments_MissingContent_Throws()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryStoreTool(context.Store, "agent-a");

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MemoryStoreTool_StoresSpecialCharacters()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryStoreTool(context.Store, "agent-a");
        var payload = "special chars ; | ` \" ' and emoji 😀";

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["content"] = payload });
        var id = GetText(result).Split(' ')[3];
        var stored = await context.Store.GetByIdAsync(id);

        stored.Should().NotBeNull();
        stored!.Content.Should().Be(payload);
    }

    [Fact]
    public async Task MemoryStoreTool_DuplicateContent_CreatesDistinctEntries()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryStoreTool(context.Store, "agent-a");
        var args = new Dictionary<string, object?> { ["content"] = "duplicate payload" };

        var first = await tool.ExecuteAsync("call-1", args);
        var second = await tool.ExecuteAsync("call-2", args);

        var firstId = GetText(first).Split(' ')[3];
        var secondId = GetText(second).Split(' ')[3];
        firstId.Should().NotBe(secondId);
    }

    [Fact]
    public async Task MemoryStoreTool_PrepareArguments_InvalidExpiresInDays_Throws()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryStoreTool(context.Store, "agent-a");

        var act = () => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["content"] = "x", ["expiresInDays"] = "not-a-number" });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MemorySearchTool_PrepareArguments_EmptyQuery_Throws()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemorySearchTool(context.Store);

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "   " });

        await act.Should().ThrowAsync<ArgumentException>();
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
        text.Should().Contain("Found 1 memory entry:");
        text.Should().Contain("ID: keep");
        text.Should().NotContain("ID: drop");
    }

    [Fact]
    public async Task MemorySearchTool_ExecuteWithEmptyQuery_ReturnsNoResults()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "hello world"));
        var tool = new MemorySearchTool(context.Store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["query"] = "" });

        GetText(result).Should().Be("No matching memories found.");
    }

    [Fact]
    public async Task MemoryGetTool_PrepareArguments_WithoutIdOrSession_Throws()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var tool = new MemoryGetTool(context.Store);

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MemoryGetTool_GetAfterDelete_ReturnsNotFound()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("to-delete", "agent-a", "delete me"));
        await context.Store.DeleteAsync("to-delete");
        var tool = new MemoryGetTool(context.Store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["id"] = "to-delete" });

        GetText(result).Should().Be("Memory entry not found.");
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
        text.Should().Contain("Session 'sess' memories (1):");
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

        GetText(result).Should().Be("No matching memories found.");
    }

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;
}
