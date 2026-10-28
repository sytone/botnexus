using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests;

public sealed class SessionToolBoundedReadTests
{
    [Fact]
    public async Task List_AppliesLimitOffsetAndReportsExhaustion()
    {
        var store = new InMemorySessionStore();
        for (var i = 0; i < 150; i++)
            await store.SaveAsync(new GatewaySession
            {
                SessionId = SessionId.From($"s{i:D3}"), AgentId = AgentId.From("owner"),
                UpdatedAt = DateTimeOffset.UnixEpoch
            });
        var tool = new SessionTool(store, AgentId.From("owner"));
        var result = await tool.ExecuteAsync("test", new Dictionary<string, object?>
            { ["action"] = "list", ["limit"] = 2, ["offset"] = 1 });
        using var json = JsonDocument.Parse(result.Content[0].Value);
        var root = json.RootElement;
        root.GetProperty("items").GetArrayLength().ShouldBe(2);
        root.GetProperty("items")[0].GetProperty("sessionId").GetString().ShouldBe("s001");
        root.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        root.GetProperty("nextOffset").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Search_RejectsUnauthorizedExplicitTargetBeforeReading()
    {
        var tool = new SessionTool(new InMemorySessionStore(), AgentId.From("owner"));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync("test",
            new Dictionary<string, object?> { ["action"] = "search", ["query"] = "needle", ["agentId"] = "peer" }));
    }

    [Fact]
    public async Task Search_HugeEntryReportsIncompleteInsteadOfHydratingSession()
    {
        var store = new InMemorySessionStore();
        var session = new GatewaySession { SessionId = SessionId.From("huge"), AgentId = AgentId.From("owner") };
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = new string('x', 2_000_000) });
        await store.SaveAsync(session);
        var result = await new SessionTool(store, AgentId.From("owner")).ExecuteAsync("test",
            new Dictionary<string, object?> { ["action"] = "search", ["query"] = "needle" });
        using var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("complete").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("scannedCharacters").GetInt32().ShouldBeLessThanOrEqualTo(1_048_576);
    }

    [Fact]
    public async Task List_ClampsLargeLimitAndRejectsMalformedOffsets()
    {
        var store = new InMemorySessionStore();
        for (var i = 0; i < 105; i++)
            await store.SaveAsync(new GatewaySession { SessionId = SessionId.From($"s{i:D3}"), AgentId = AgentId.From("owner") });
        var tool = new SessionTool(store, AgentId.From("owner"));
        using var page = JsonDocument.Parse((await tool.ExecuteAsync("test", new Dictionary<string, object?>
            { ["action"] = "list", ["limit"] = int.MaxValue })).Content[0].Value);
        page.RootElement.GetProperty("limit").GetInt32().ShouldBe(100);
        page.RootElement.GetProperty("items").GetArrayLength().ShouldBe(100);
        foreach (var value in new object[] { -1, double.NaN, double.PositiveInfinity, 1.5, "2147483648" })
            await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync("test",
                new Dictionary<string, object?> { ["action"] = "search", ["query"] = "needle", ["offset"] = value }));
        using var empty = JsonDocument.Parse((await tool.ExecuteAsync("test", new Dictionary<string, object?>
            { ["action"] = "list", ["offset"] = int.MaxValue })).Content[0].Value);
        empty.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Reads_ObservePreCanceledTokenAndRejectInvalidWindows()
    {
        var tool = new SessionTool(new InMemorySessionStore(), AgentId.From("owner"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync("test",
            new Dictionary<string, object?> { ["action"] = "list" }, cts.Token));
        foreach (var value in new object[] { -1, 0, double.NaN, double.PositiveInfinity, "999999999999999999999999" })
            await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync("test",
                new Dictionary<string, object?> { ["action"] = "list", ["limit"] = value }));
    }
}
