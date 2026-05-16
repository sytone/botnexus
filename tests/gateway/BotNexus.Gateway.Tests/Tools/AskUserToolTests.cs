using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
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

    private static AskUserTool CreateTool(AskUserResponseRegistry registry, string conversationId)
        => new(
            registry,
            AgentId.From("agent-a"),
            SessionId.From("session-1"),
            ConversationId.From(conversationId));

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
