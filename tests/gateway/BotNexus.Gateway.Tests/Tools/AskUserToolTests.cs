using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class AskUserToolTests
{
    [Fact]
    public async Task ExecuteAsync_EmitsUserInputRequiredEvent()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Which environment should I deploy to?"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, freeFormText: "staging")).ShouldBeTrue();
        await executionTask;

        updates.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_BlocksUntilResponseReceived()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "What name should I use?"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        await Task.Delay(100);

        executionTask.IsCompleted.ShouldBeFalse();
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, freeFormText: "Hermes")).ShouldBeTrue();
        await executionTask;
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsUserFreeFormText()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "What should the release be called?"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, freeFormText: "Project Hermes")).ShouldBeTrue();
        var result = await executionTask;

        ReadText(result).ShouldContain("Project Hermes");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSelectedValues()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Select deployment targets",
            ["input_type"] = "multiple_choice",
            ["allow_multiple"] = true,
            ["choices"] = new[]
            {
                new Dictionary<string, object?> { ["value"] = "dev", ["label"] = "Development" },
                new Dictionary<string, object?> { ["value"] = "staging", ["label"] = "Staging" },
                new Dictionary<string, object?> { ["value"] = "prod", ["label"] = "Production" }
            }
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(
            request.ConversationId,
            request.RequestId,
            CreateResponse(request.RequestId, freeFormText: null, selectedValues: ["dev", "staging"])).ShouldBeTrue();

        var result = await executionTask;
        var resultText = ReadText(result);
        resultText.ShouldContain("dev");
        resultText.ShouldContain("staging");
    }

    [Fact]
    public async Task ExecuteAsync_OnTimeout_ReturnsTimeoutResult()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Respond soon",
            ["timeout_seconds"] = 1
        });

        var result = await tool.ExecuteAsync("call-ask-user", arguments, onUpdate: _ => { });

        var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("wasTimeout").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_OnCancel_ReturnsCancelledResult()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Should I continue?"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.Cancel(request.RequestId);
        var result = await executionTask;

        var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("wasCancelled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithChoices_IncludesChoicesInRequest()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Choose a target environment",
            ["input_type"] = "single_choice",
            ["choices"] = new[]
            {
                new Dictionary<string, object?> { ["value"] = "dev", ["label"] = "Development" },
                new Dictionary<string, object?> { ["value"] = "prod", ["label"] = "Production" }
            }
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, selectedValues: ["prod"], freeFormText: null)).ShouldBeTrue();
        await executionTask;

        request.Choices.ShouldNotBeNull();
        request.Choices.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresPrompt_ThrowsOnMissing()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesConversationIdFromContext()
    {
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-from-context");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Confirm context resolution"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, freeFormText: "ok")).ShouldBeTrue();
        await executionTask;

        request.ConversationId.ShouldBe(ConversationId.From("conversation-from-context"));
    }

    // ── ask_user durability (#1488): persist while pending, clear on resolve ──

    [Fact]
    public async Task ExecuteAsync_PersistsPendingPrompt_WhileWaiting()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, "conversation-1");
        var tool = CreateTool(registry, "conversation-1", store);
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Pick a deploy target",
            ["input_type"] = "single_choice",
            ["choices"] = new[]
            {
                new Dictionary<string, object?> { ["value"] = "prod", ["label"] = "Production" }
            }
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);

        // While the tool is blocked waiting, the prompt is durably persisted on the conversation row
        // so a reloaded/newly-opened/mobile client can hydrate it on connect.
        var pending = await WaitForPendingJsonAsync(store, request.ConversationId, expectPresent: true);
        pending.ShouldNotBeNull();
        using (var doc = JsonDocument.Parse(pending!))
        {
            doc.RootElement.GetProperty("requestId").GetString().ShouldBe(request.RequestId);
            doc.RootElement.GetProperty("prompt").GetString().ShouldBe("Pick a deploy target");
            doc.RootElement.GetProperty("inputType").GetString().ShouldBe("SingleChoice");
        }

        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, selectedValues: ["prod"], freeFormText: null)).ShouldBeTrue();
        await executionTask;
    }

    [Fact]
    public async Task ExecuteAsync_ClearsPendingPrompt_AfterAnswer()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, "conversation-1");
        var tool = CreateTool(registry, "conversation-1", store);
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "What name should I use?"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, freeFormText: "Hermes")).ShouldBeTrue();
        await executionTask;

        // Once the wait resolves, the durable copy is cleared so a stale prompt never rehydrates.
        var pending = await WaitForPendingJsonAsync(store, request.ConversationId, expectPresent: false);
        pending.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ClearsPendingPrompt_AfterTimeout()
    {
        var registry = new AskUserResponseRegistry();
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, "conversation-1");
        var tool = CreateTool(registry, "conversation-1", store);
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Respond soon",
            ["timeout_seconds"] = 1
        });

        await tool.ExecuteAsync("call-ask-user", arguments, onUpdate: _ => { });

        var loaded = await store.GetAsync(ConversationId.From("conversation-1"));
        loaded.ShouldNotBeNull();
        loaded!.PendingAskUserJson.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WithoutStore_DoesNotThrow()
    {
        // The conversation store is optional; the prompt must still work with no durability wired.
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Still works?"
        });

        var executionTask = tool.ExecuteAsync("call-ask-user", arguments, onUpdate: updates.Add);
        var request = await WaitForRequestAsync(updates);
        registry.TryComplete(request.ConversationId, request.RequestId, CreateResponse(request.RequestId, freeFormText: "yes")).ShouldBeTrue();
        var result = await executionTask;

        ReadText(result).ShouldContain("yes");
    }

    // -- ask_user pending soft-lock (#1916): pending state must never leak on failure --

    [Fact]
    public async Task PrepareArgumentsAsync_WithNonIntegerTimeout_ThrowsWithoutRegisteringPending()
    {
        // A validation failure (e.g. a non-integer timeout_seconds) must abort during argument
        // preparation and NEVER create pending-input state. A call that never validated must not
        // leave a live prompt behind.
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Respond soon",
            ["timeout_seconds"] = "not-a-number"
        });

        await act.ShouldThrowAsync<ArgumentException>();
        registry.TryGetPendingRequestId(ConversationId.From("conversation-1"), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateCallbackThrows_AutoCancelsPendingSoSessionUnblocks()
    {
        // If anything fails after the registry entry is created (e.g. the widget-emit callback
        // throws), the pending request must be auto-cancelled so the conversation does not stay
        // permanently in pending-input state silently swallowing user messages (#1916, criterion 4).
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Which environment?"
        });

        Func<Task> act = () => tool.ExecuteAsync(
            "call-ask-user",
            arguments,
            onUpdate: _ => throw new InvalidOperationException("widget emit failed"));

        await act.ShouldThrowAsync<InvalidOperationException>();

        // The pending registration must not survive the failure.
        registry.TryGetPendingRequestId(ConversationId.From("conversation-1"), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WithNumericStringTimeout_CoercesAndTimesOut()
    {
        // A numeric-string timeout must be coerced (not rejected after pending UI is created).
        var registry = new AskUserResponseRegistry();
        var tool = CreateTool(registry, "conversation-1");
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Respond soon",
            ["timeout_seconds"] = "1"
        });

        var result = await tool.ExecuteAsync("call-ask-user", arguments, onUpdate: _ => { });

        var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("wasTimeout").GetBoolean().ShouldBeTrue();
        registry.TryGetPendingRequestId(ConversationId.From("conversation-1"), out _).ShouldBeFalse();
    }
    private static async Task SeedConversationAsync(InMemoryConversationStore store, string conversationId)
    {
        await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = AgentId.From("agent-a"),
            Title = "Ask convo"
        });
    }

    private static async Task<string?> WaitForPendingJsonAsync(
        InMemoryConversationStore store,
        ConversationId conversationId,
        bool expectPresent)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var conversation = await store.GetAsync(conversationId);
            var pending = conversation?.PendingAskUserJson;
            if (expectPresent ? pending is not null : pending is null)
                return pending;

            await Task.Delay(25);
        }

        return (await store.GetAsync(conversationId))?.PendingAskUserJson;
    }

    private static AskUserTool CreateTool(AskUserResponseRegistry registry, string conversationId)
        => new(
            registry,
            AgentId.From("agent-a"),
            SessionId.From("session-1"),
            ConversationId.From(conversationId));

    private static AskUserTool CreateTool(AskUserResponseRegistry registry, string conversationId, IConversationStore conversationStore)
        => new(
            registry,
            AgentId.From("agent-a"),
            SessionId.From("session-1"),
            ConversationId.From(conversationId),
            conversationStore);

    private static async Task<AskUserRequest> WaitForRequestAsync(IReadOnlyList<AgentToolResult> updates)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (updates.Count > 0)
            {
                return updates[^1].Details as AskUserRequest
                    ?? throw new InvalidOperationException("Ask user update payload was not available in tool result details.");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for ask_user update callback.");
    }

    private static AskUserResponse CreateResponse(
        string requestId,
        string? freeFormText = "answer",
        IReadOnlyList<string>? selectedValues = null,
        bool wasCancelled = false,
        bool wasTimeout = false)
        => new()
        {
            RequestId = requestId,
            FreeFormText = freeFormText,
            SelectedValues = selectedValues,
            WasCancelled = wasCancelled,
            WasTimeout = wasTimeout
        };

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
