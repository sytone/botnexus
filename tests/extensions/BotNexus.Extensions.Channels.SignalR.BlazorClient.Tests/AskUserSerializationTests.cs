using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AskUserSerializationTests
{
    /// <summary>
    /// Verifies that HandleUserInputRequired sets pending ask-user state when the event
    /// has a properly serialized UserInputRequest payload (the post-fix behavior).
    /// </summary>
    [Fact]
    public void HandleUserInputRequired_sets_pending_when_payload_present()
    {
        var store = new ClientStateStore();
        var handler = new GatewayEventHandler(store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

        store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true,
            SessionId = "sess-1",
            ActiveConversationId = "conv-1"
        });

        var agent = store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test Conversation",
            ActiveSessionId = "sess-1"
        };
        store.RegisterSession("agent-1", "sess-1");

        var evt = new AgentStreamEvent
        {
            SessionId = "sess-1",
            ConversationId = "conv-1",
            UserInputRequest = new AskUserRequestPayload
            {
                RequestId = "req-456",
                ConversationId = "conv-1",
                Prompt = "Choose wisely",
                InputType = "SingleChoice",
                Choices = new[]
                {
                    new AskUserChoicePayload { Value = "a", Label = "Option A" },
                    new AskUserChoicePayload { Value = "b", Label = "Option B" }
                },
                AllowMultiple = false,
                AllowFreeForm = false
            }
        };

        handler.HandleUserInputRequired(evt);

        var pending = store.GetPendingAskUser("conv-1");
        Assert.NotNull(pending);
        Assert.Equal("req-456", pending.RequestId);
        Assert.Equal("Choose wisely", pending.Prompt);
        Assert.Equal("SingleChoice", pending.InputType);
    }

    /// <summary>
    /// Verifies that HandleUserInputRequired does NOT set pending state when InputType is null
    /// (the pre-fix behavior where integer serialization caused null InputType).
    /// Regression test for #967.
    /// </summary>
    [Fact]
    public void HandleUserInputRequired_skips_when_inputType_null()
    {
        var store = new ClientStateStore();
        var handler = new GatewayEventHandler(store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

        store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true,
            SessionId = "sess-1",
            ActiveConversationId = "conv-1"
        });

        var agent = store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test Conversation",
            ActiveSessionId = "sess-1"
        };
        store.RegisterSession("agent-1", "sess-1");

        // Simulate pre-fix state: InputType is null (what happened when the enum
        // serialized as integer 0 and couldn't deserialize into string)
        var evt = new AgentStreamEvent
        {
            SessionId = "sess-1",
            ConversationId = "conv-1",
            UserInputRequest = new AskUserRequestPayload
            {
                RequestId = "req-789",
                ConversationId = "conv-1",
                Prompt = "Test prompt",
                InputType = null, // This was the bug — null from integer deserialization
                AllowMultiple = false,
                AllowFreeForm = false
            }
        };

        handler.HandleUserInputRequired(evt);

        // With null InputType, TryBuildAskUserPrompt returns false — no pending state set
        var pending = store.GetPendingAskUser("conv-1");
        Assert.Null(pending);
    }

    /// <summary>
    /// Verifies that AskUserInputType values (from BotNexus.Domain) serialize as strings
    /// when serialized with camelCase options (matching SignalR's JsonHubProtocol defaults).
    /// This is the critical serialization test that proves the enum won't serialize as an integer.
    /// We test by deserializing a JSON object containing the enum into the client payload type.
    /// </summary>
    [Theory]
    [InlineData("FreeForm")]
    [InlineData("SingleChoice")]
    [InlineData("MultipleChoice")]
    [InlineData("ChoiceOrFreeForm")]
    public void AskUserRequestPayload_deserializes_inputType_string(string inputType)
    {
        // Simulate what SignalR sends: a JSON object with inputType as a string
        var json = $$"""{"requestId":"r1","conversationId":"c1","prompt":"test","inputType":"{{inputType}}","allowMultiple":false,"allowFreeForm":true}""";
        var payload = JsonSerializer.Deserialize<AskUserRequestPayload>(json);

        Assert.NotNull(payload);
        Assert.Equal(inputType, payload.InputType);
    }

    /// <summary>
    /// Confirms the pre-fix failure mode: if inputType were an integer (the bug),
    /// deserialization throws a JsonException because STJ cannot convert a number token
    /// to a string property — causing the event handler to silently drop the prompt.
    /// </summary>
    [Fact]
    public void AskUserRequestPayload_inputType_integer_throws_on_deserialize()
    {
        // Pre-fix: enum without [JsonStringEnumConverter] serialized as integer 0
        var json = "{\"requestId\":\"r1\",\"conversationId\":\"c1\",\"prompt\":\"test\",\"inputType\":0,\"allowMultiple\":false,\"allowFreeForm\":true}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AskUserRequestPayload>(json));
    }
}
