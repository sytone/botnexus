using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class CanvasToolStateTests
{
    private static readonly AgentId TestAgentId = AgentId.From("test-agent");
    private static readonly ConversationId TestConversationId = ConversationId.From("c_test123");

    // ═══════════════════════════════════════════════════════════════════
    // set_state
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SetState_WritesKeyValueToStore()
    {
        var store = Substitute.For<IConversationStore>();
        store.SetCanvasStateKeyAsync(TestConversationId, "counter", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var tool = CreateTool(store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement("counter"),
            ["value"] = JsonElement(42)
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("'counter' set successfully");
        await store.Received(1).SetCanvasStateKeyAsync(
            TestConversationId, "counter", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetState_WhenConversationNotFound_ReportsFailure()
    {
        var store = Substitute.For<IConversationStore>();
        store.SetCanvasStateKeyAsync(
                TestConversationId, "missing", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var tool = CreateTool(store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement("missing"),
            ["value"] = JsonElement("val")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("conversation not found");
    }

    [Fact]
    public async Task SetState_ValueExceedingMaxBytes_IsRejectedAndNotWritten()
    {
        var store = Substitute.For<IConversationStore>();
        var options = new CanvasToolOptions { MaxValueBytes = 32 };
        var tool = new CanvasTool(TestAgentId, TestConversationId, conversationStore: store, options: options);

        // A JSON string whose serialised UTF-8 length comfortably exceeds 32 bytes.
        var bigValue = JsonElement(new string('x', 200));
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement("big"),
            ["value"] = bigValue
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("exceeds");
        store.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IConversationStore.SetCanvasStateKeyAsync))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task SetState_ValueAtMaxBytes_IsAccepted()
    {
        var store = Substitute.For<IConversationStore>();
        store.SetCanvasStateKeyAsync(TestConversationId, "k", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Serialised value is exactly the configured limit ("..." => content + 2 quote chars).
        var content = new string('y', 30);
        var serializedLength = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(content));
        var options = new CanvasToolOptions { MaxValueBytes = serializedLength };
        var tool = new CanvasTool(TestAgentId, TestConversationId, conversationStore: store, options: options);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement("k"),
            ["value"] = JsonElement(content)
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("set successfully");
        await store.Received(1).SetCanvasStateKeyAsync(
            TestConversationId, "k", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetState_KeyExceedingMaxLength_IsRejectedAndNotWritten()
    {
        var store = Substitute.For<IConversationStore>();
        var options = new CanvasToolOptions { MaxKeyLength = 8 };
        var tool = new CanvasTool(TestAgentId, TestConversationId, conversationStore: store, options: options);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement(new string('k', 9)),
            ["value"] = JsonElement("v")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("key");
        result.Content.ShouldHaveSingleItem().Value.ShouldContain("exceeds");
        store.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IConversationStore.SetCanvasStateKeyAsync))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task SetState_KeyAtMaxLength_IsAccepted()
    {
        var store = Substitute.For<IConversationStore>();
        store.SetCanvasStateKeyAsync(TestConversationId, Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var options = new CanvasToolOptions { MaxKeyLength = 8 };
        var tool = new CanvasTool(TestAgentId, TestConversationId, conversationStore: store, options: options);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement(new string('k', 8)),
            ["value"] = JsonElement("v")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("set successfully");
        await store.Received(1).SetCanvasStateKeyAsync(
            TestConversationId, Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetState_WithoutStore_ReturnsUnavailableMessage()
    {
        var tool = new CanvasTool(TestAgentId, TestConversationId, conversationStore: null);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement("k"),
            ["value"] = JsonElement("v")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("not available");
    }

    [Fact]
    public async Task SetState_WithoutConversationId_ReturnsUnavailableMessage()
    {
        var store = Substitute.For<IConversationStore>();
        var tool = new CanvasTool(TestAgentId, conversationId: null, conversationStore: store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("set_state"),
            ["key"] = JsonElement("k"),
            ["value"] = JsonElement("v")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("not available");
    }

    // ═══════════════════════════════════════════════════════════════════
    // get_state (single key)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetState_SingleKey_ReturnsValue()
    {
        var store = Substitute.For<IConversationStore>();
        var state = new Dictionary<string, JsonElement>
        {
            ["color"] = JsonDocument.Parse("\"blue\"").RootElement
        };
        store.GetCanvasStateAsync(TestConversationId, Arg.Any<CancellationToken>())
            .Returns(state);

        var tool = CreateTool(store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("get_state"),
            ["key"] = JsonElement("color")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("blue");
    }

    [Fact]
    public async Task GetState_SingleKey_NotFound_ReportsKeyMissing()
    {
        var store = Substitute.For<IConversationStore>();
        store.GetCanvasStateAsync(TestConversationId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement>());

        var tool = CreateTool(store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("get_state"),
            ["key"] = JsonElement("missing-key")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("'missing-key' not found");
    }

    // ═══════════════════════════════════════════════════════════════════
    // get_state (all keys)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetState_AllKeys_ReturnsJsonDictionary()
    {
        var store = Substitute.For<IConversationStore>();
        var state = new Dictionary<string, JsonElement>
        {
            ["a"] = JsonDocument.Parse("1").RootElement,
            ["b"] = JsonDocument.Parse("\"two\"").RootElement
        };
        store.GetCanvasStateAsync(TestConversationId, Arg.Any<CancellationToken>())
            .Returns(state);

        var tool = CreateTool(store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("get_state")
        });

        var text = result.Content.ShouldHaveSingleItem().Value;
        text.ShouldContain("\"a\"");
        text.ShouldContain("\"b\"");
    }

    [Fact]
    public async Task GetState_AllKeys_EmptyState_ReportsEmpty()
    {
        var store = Substitute.For<IConversationStore>();
        store.GetCanvasStateAsync(TestConversationId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement>());

        var tool = CreateTool(store);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("get_state")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("empty");
    }

    // ═══════════════════════════════════════════════════════════════════
    // clear_state
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClearState_CallsStoreAndReportsSuccess()
    {
        var store = Substitute.For<IConversationStore>();
        var tool = CreateTool(store);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("clear_state")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("cleared");
        await store.Received(1).ClearCanvasStateAsync(TestConversationId, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════════
    // PrepareArguments validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrepareArguments_SetState_MissingKey_Throws()
    {
        var tool = CreateTool(Substitute.For<IConversationStore>());

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["action"] = JsonElement("set_state"),
                ["value"] = JsonElement("v")
            }));
    }

    [Fact]
    public async Task PrepareArguments_SetState_MissingValue_Throws()
    {
        var tool = CreateTool(Substitute.For<IConversationStore>());

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["action"] = JsonElement("set_state"),
                ["key"] = JsonElement("k")
            }));
    }

    [Fact]
    public async Task PrepareArguments_InvalidAction_Throws()
    {
        var tool = CreateTool(Substitute.For<IConversationStore>());

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["action"] = JsonElement("invalid_action")
            }));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Existing render/clear actions still work
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Render_StillNotifiesCanvas()
    {
        var notifier = Substitute.For<IAgentCanvasNotifier>();
        var tool = new CanvasTool(TestAgentId, TestConversationId, canvasNotifiers: [notifier]);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("render"),
            ["html"] = JsonElement("<h1>Hello</h1>")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("rendered");
        await notifier.Received(1).NotifyCanvasUpdatedAsync(
            TestAgentId.Value, TestConversationId.Value, "<h1>Hello</h1>", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_StillNotifiesCanvasWithEmptyHtml()
    {
        var notifier = Substitute.For<IAgentCanvasNotifier>();
        var tool = new CanvasTool(TestAgentId, TestConversationId, canvasNotifiers: [notifier]);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = JsonElement("clear")
        });

        result.Content.ShouldHaveSingleItem().Value.ShouldContain("cleared");
        await notifier.Received(1).NotifyCanvasUpdatedAsync(
            TestAgentId.Value, TestConversationId.Value, string.Empty, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static CanvasTool CreateTool(IConversationStore store)
    {
        return new CanvasTool(TestAgentId, TestConversationId, conversationStore: store);
    }

    private static JsonElement JsonElement(string value)
    {
        return JsonDocument.Parse($"\"{value}\"").RootElement.Clone();
    }

    private static JsonElement JsonElement(int value)
    {
        return JsonDocument.Parse(value.ToString()).RootElement.Clone();
    }
}
