using System.Text.Json;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// AC4 of #3162: an MCP-provided tool result exceeding the shared budget is bounded by the central
/// backstop.
/// </summary>
/// <remarks>
/// MCP is the motivating case for the whole issue: <c>grep -n 'Max|truncat|Bytes|Length'</c> over
/// the MCP contributor returns zero hits, so an MCP server returning a huge payload had nothing
/// between it and the context window. These tests exercise the REAL <see cref="McpBridgedTool"/>
/// over the mock transport and then apply the same shared budget
/// (<see cref="ToolOutputBudget.Apply"/>) that <c>ToolExecutor</c> applies to every result — the
/// executor's own seam is covered by <c>ToolOutputBudgetTests</c> in the agent-core suite, where
/// the internal executor is visible.
/// </remarks>
public class McpToolOutputBudgetTests
{
    private static (McpClient Client, MockMcpTransport Transport) CreateInitializedClient(string serverId = "bulk")
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResult(1, new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpServerCapabilities(),
        });
        var client = new McpClient(transport, serverId);
        client.InitializeAsync().GetAwaiter().GetResult();
        return (client, transport);
    }

    private static McpBridgedTool CreateTool(McpClient client)
        => new(client, new McpToolDefinition
        {
            Name = "dump",
            Description = "returns a large payload",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        });

    /// <summary>
    /// The bridged tool itself applies no cap — this is the defect the backstop exists for. Pinning
    /// it makes the next assertion meaningful rather than vacuous.
    /// </summary>
    [Fact]
    public async Task McpBridgedTool_AppliesNoCapOfItsOwn()
    {
        var (client, transport) = CreateInitializedClient();
        var payload = new string('x', 300_000);
        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = payload }],
        });

        var result = await CreateTool(client).ExecuteAsync("call-1", new Dictionary<string, object?>());

        JoinText(result).Length.ShouldBe(payload.Length);
    }

    /// <summary>
    /// AC4: the same oversize MCP result, put through the shared budget, comes back bounded, as a
    /// success, with the marker, the omitted byte count and the one shared guidance line.
    /// </summary>
    [Fact]
    public async Task McpProvidedResult_ExceedingBudget_IsBoundedBySharedBackstop()
    {
        var (client, transport) = CreateInitializedClient();
        var payload = new string('x', 300_000);
        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = payload }],
        });

        var raw = await CreateTool(client).ExecuteAsync("call-1", new Dictionary<string, object?>());
        var bounded = ToolOutputBudget.Apply(raw, ToolOutputBudget.DefaultMaxBytes);

        var text = JoinText(bounded);
        text.Length.ShouldBeLessThan(payload.Length);
        text.ShouldContain("[tool output truncated:");
        text.ShouldContain($"{300_000 - ToolOutputBudget.DefaultMaxBytes} bytes omitted");
        text.ShouldContain(ToolOutputBudget.NarrowingGuidance);
        // Bounded projection, not a drop: the retained prefix survives.
        text.ShouldStartWith(new string('x', 1000));
    }

    /// <summary>
    /// An MCP result carrying multi-byte content is cut on a rune boundary, so no replacement
    /// characters are introduced (AC3 across the MCP path specifically).
    /// </summary>
    [Fact]
    public async Task McpProvidedMultiByteResult_IsCutOnARuneBoundary()
    {
        var (client, transport) = CreateInitializedClient();
        // 4-byte emoji interleaved with 3-byte CJK: no naive byte cut lands on a boundary.
        var payload = string.Concat(Enumerable.Repeat("\U0001F52C\u4f60\u597d", 40_000));
        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = payload }],
        });

        var raw = await CreateTool(client).ExecuteAsync("call-1", new Dictionary<string, object?>());
        var bounded = ToolOutputBudget.Apply(raw, ToolOutputBudget.DefaultMaxBytes);

        var text = JoinText(bounded);
        text.ShouldNotContain("\uFFFD");
        IsWellFormedUtf16(text).ShouldBeTrue("a lone surrogate means a 4-byte rune was split");
    }

    /// <summary>
    /// True when every surrogate in <paramref name="value"/> is part of a complete pair. Asserting
    /// "contains no low surrogate" would be wrong — a correctly retained emoji IS a surrogate pair.
    /// The defect guarded against is a <em>lone</em> surrogate: a pair sliced in half.
    /// </summary>
    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string JoinText(AgentToolResult result)
        => string.Concat(result.Content
            .Where(block => block.Type == AgentToolContentType.Text)
            .Select(block => block.Value));
}
