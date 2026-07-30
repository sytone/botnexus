using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// Covers <c>tools/list</c> cursor pagination: accumulation across pages, the exact cursor
/// values sent per request, termination semantics, and loop/cap guards.
/// </summary>
public class McpClientToolsListPaginationTests
{
    private static MockMcpTransport CreateInitializedTransport()
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResult(1, new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpServerCapabilities
            {
                Tools = new McpToolCapability { ListChanged = false },
            },
        });
        return transport;
    }

    private static McpToolDefinition Tool(string name) => new()
    {
        Name = name,
        Description = name + " desc",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
    };

    /// <summary>Cursor values sent on each tools/list request, null when no params were sent.</summary>
    private static List<string?> ToolsListCursors(MockMcpTransport transport) =>
        transport.SentRequests
            .Where(r => r.Method == "tools/list")
            .Select(r =>
            {
                if (r.Params is not JsonElement p)
                    return null;
                return p.TryGetProperty("cursor", out var c) ? c.GetString() : null;
            })
            .ToList();

    [Fact]
    public async Task ListToolsAsync_FollowsNextCursor_AccumulatesAllPagesInOrder()
    {
        var transport = CreateInitializedTransport();

        transport.EnqueueResult(2, new McpToolsListResult
        {
            Tools = [Tool("alpha"), Tool("beta")],
            NextCursor = "cursor-page-2",
        });
        transport.EnqueueResult(3, new McpToolsListResult
        {
            Tools = [Tool("gamma")],
            NextCursor = "cursor-page-3",
        });
        transport.EnqueueResult(4, new McpToolsListResult
        {
            Tools = [Tool("delta"), Tool("epsilon")],
        });

        var client = new McpClient(transport, "paged");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ToList()
            .ShouldBe(["alpha", "beta", "gamma", "delta", "epsilon"]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_SendsExactCursorValues_OnEachContinuationRequest()
    {
        var transport = CreateInitializedTransport();

        transport.EnqueueResult(2, new McpToolsListResult
        {
            Tools = [Tool("alpha")],
            NextCursor = "cursor-page-2",
        });
        transport.EnqueueResult(3, new McpToolsListResult
        {
            Tools = [Tool("beta")],
            NextCursor = "cursor-page-3",
        });
        transport.EnqueueResult(4, new McpToolsListResult
        {
            Tools = [Tool("gamma")],
        });

        var client = new McpClient(transport, "paged");
        await client.InitializeAsync();

        _ = await client.ListToolsAsync();

        ToolsListCursors(transport).ShouldBe([null, "cursor-page-2", "cursor-page-3"]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_ShortPageWithCursor_StillFetchesNextPage()
    {
        var transport = CreateInitializedTransport();

        // A single-tool page is "short" but still carries a cursor: must not terminate.
        transport.EnqueueResult(2, new McpToolsListResult
        {
            Tools = [Tool("only-one")],
            NextCursor = "more",
        });
        transport.EnqueueResult(3, new McpToolsListResult
        {
            Tools = [Tool("second")],
        });

        var client = new McpClient(transport, "short");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ToList().ShouldBe(["only-one", "second"]);
        ToolsListCursors(transport).ShouldBe([null, "more"]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_EmptyStringCursor_TerminatesPagination()
    {
        var transport = CreateInitializedTransport();

        transport.EnqueueResult(2, new McpToolsListResult
        {
            Tools = [Tool("alpha")],
            NextCursor = string.Empty,
        });

        var client = new McpClient(transport, "empty-cursor");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ToList().ShouldBe(["alpha"]);
        ToolsListCursors(transport).ShouldBe([null]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_RepeatedCursor_StopsInsteadOfLoopingForever()
    {
        var transport = CreateInitializedTransport();

        // Server keeps handing back the same cursor. Client must issue the continuation once
        // and then stop when the same cursor comes back a second time.
        transport.EnqueueResult(2, new McpToolsListResult
        {
            Tools = [Tool("alpha")],
            NextCursor = "stuck",
        });
        transport.EnqueueResult(3, new McpToolsListResult
        {
            Tools = [Tool("beta")],
            NextCursor = "stuck",
        });

        var client = new McpClient(transport, "loopy");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ToList().ShouldBe(["alpha", "beta"]);
        ToolsListCursors(transport).ShouldBe([null, "stuck"]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_UnboundedServer_StopsAtMaxPageCap()
    {
        var transport = CreateInitializedTransport();

        // Always a fresh cursor => only the page cap can stop the walk.
        for (var i = 0; i < McpClient.MaxToolListPages + 10; i++)
        {
            transport.EnqueueResult(i + 2, new McpToolsListResult
            {
                Tools = [Tool($"tool-{i}")],
                NextCursor = $"cursor-{i}",
            });
        }

        var client = new McpClient(transport, "unbounded");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Count.ShouldBe(McpClient.MaxToolListPages);
        ToolsListCursors(transport).Count.ShouldBe(McpClient.MaxToolListPages);
        tools[0].Name.ShouldBe("tool-0");
        tools[^1].Name.ShouldBe($"tool-{McpClient.MaxToolListPages - 1}");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_SinglePageWithoutCursor_SendsNoParams()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResult(2, new McpToolsListResult { Tools = [Tool("solo")] });

        var client = new McpClient(transport, "single");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ToList().ShouldBe(["solo"]);
        transport.SentRequests.Single(r => r.Method == "tools/list").Params.ShouldBeNull();

        await client.DisposeAsync();
    }
}
