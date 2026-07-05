using System.Text.Json;

using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Direct unit coverage for <see cref="AskUserPromptFactory"/>, the pure <c>ask_user</c> prompt-parsing
/// cluster extracted from <c>GatewayEventHandler</c> (#1753). Before the extraction these functions were
/// stranded inside a stateful class and only exercised indirectly (via the live event handler or the REST
/// hydration path), so the metadata-vs-payload precedence, the stringified-JSON choices branch, and the
/// ISO-duration expiry parsing had no direct tests. This pins that behaviour in the factory's new home.
/// </summary>
public sealed class AskUserPromptFactoryTests
{
    private static IReadOnlyDictionary<string, JsonElement> Metadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return result;
    }

    // ── TryBuildFromStreamEvent: live-event path ──────────────────────────

    [Fact]
    public void TryBuildFromStreamEvent_BuildsPromptFromMetadata()
    {
        var evt = new AgentStreamEvent
        {
            Metadata = Metadata(
                "{\"requestId\":\"req-1\",\"conversationId\":\"conv-1\",\"prompt\":\"Pick one\"," +
                "\"inputType\":\"SingleChoice\",\"allowMultiple\":true,\"allowFreeForm\":false," +
                "\"timeout\":\"00:02:00\"," +
                "\"choices\":\"[{\\\"value\\\":\\\"a\\\",\\\"label\\\":\\\"Apple\\\",\\\"description\\\":\\\"fruit\\\"}]\"}")
        };

        var ok = AskUserPromptFactory.TryBuildFromStreamEvent(evt, out var prompt);

        Assert.True(ok);
        Assert.NotNull(prompt);
        Assert.Equal("req-1", prompt!.RequestId);
        Assert.Equal("conv-1", prompt.ConversationId);
        Assert.Equal("Pick one", prompt.Prompt);
        Assert.Equal("SingleChoice", prompt.InputType);
        Assert.True(prompt.AllowMultiple);
        Assert.False(prompt.AllowFreeForm);
        // The stringified-JSON "choices" metadata value is parsed back into structured choices.
        Assert.NotNull(prompt.Choices);
        Assert.Single(prompt.Choices!);
        Assert.Equal("a", prompt.Choices![0].Value);
        Assert.Equal("Apple", prompt.Choices[0].Label);
        Assert.Equal("fruit", prompt.Choices[0].Description);
        Assert.NotNull(prompt.ExpiresAt);
        Assert.True(prompt.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryBuildFromStreamEvent_FallsBackToStructuredPayload_WhenMetadataAbsent()
    {
        // No metadata dictionary at all -> the structured UserInputRequest payload supplies every field,
        // including the structured Choices list (exercising the ParseChoices fallback branch).
        var evt = new AgentStreamEvent
        {
            UserInputRequest = new AskUserRequestPayload
            {
                RequestId = "req-2",
                ConversationId = "conv-2",
                Prompt = "Choose a target",
                InputType = "SingleChoice",
                AllowFreeForm = true,
                Choices =
                [
                    new AskUserChoicePayload { Value = "prod", Label = "Production", Description = "live" },
                    new AskUserChoicePayload { Value = "staging" } // no label -> value used as label
                ]
            }
        };

        var ok = AskUserPromptFactory.TryBuildFromStreamEvent(evt, out var prompt);

        Assert.True(ok);
        Assert.Equal("req-2", prompt!.RequestId);
        Assert.Equal("conv-2", prompt.ConversationId);
        Assert.True(prompt.AllowFreeForm);
        Assert.NotNull(prompt.Choices);
        Assert.Equal(2, prompt.Choices!.Count);
        Assert.Equal("Production", prompt.Choices[0].Label);
        // Missing label falls back to the value.
        Assert.Equal("staging", prompt.Choices[1].Label);
        // No timeout anywhere -> no expiry.
        Assert.Null(prompt.ExpiresAt);
    }

    [Fact]
    public void TryBuildFromStreamEvent_ReturnsFalse_WhenRequiredFieldsMissing()
    {
        // Missing inputType -> not enough to render a prompt.
        var evt = new AgentStreamEvent
        {
            Metadata = Metadata("{\"requestId\":\"req-3\",\"prompt\":\"Hi\"}")
        };

        var ok = AskUserPromptFactory.TryBuildFromStreamEvent(evt, out var prompt);

        Assert.False(ok);
        Assert.Null(prompt);
    }

    [Fact]
    public void TryBuildFromStreamEvent_ReturnsFalse_WhenEventIsEmpty()
    {
        var ok = AskUserPromptFactory.TryBuildFromStreamEvent(new AgentStreamEvent(), out var prompt);

        Assert.False(ok);
        Assert.Null(prompt);
    }

    [Fact]
    public void TryBuildFromStreamEvent_MalformedStringifiedChoices_YieldsNoChoicesButStillBuilds()
    {
        // A "choices" metadata value that is a string but not valid JSON must degrade to no choices
        // rather than throwing out of the parser.
        var evt = new AgentStreamEvent
        {
            Metadata = Metadata(
                "{\"requestId\":\"req-4\",\"prompt\":\"Pick\",\"inputType\":\"SingleChoice\"," +
                "\"choices\":\"not-json\"}")
        };

        var ok = AskUserPromptFactory.TryBuildFromStreamEvent(evt, out var prompt);

        Assert.True(ok);
        Assert.Equal("req-4", prompt!.RequestId);
        Assert.Null(prompt.Choices);
    }

    // ── TryBuildFromPersistedJson: parity between the two entry points ────

    [Fact]
    public void TryBuildFromPersistedJson_And_StreamEvent_ProduceEquivalentPrompt()
    {
        const string persisted =
            "{\"requestId\":\"req-9\",\"conversationId\":\"conv-9\",\"prompt\":\"Deploy?\"," +
            "\"inputType\":\"SingleChoice\",\"allowMultiple\":false,\"allowFreeForm\":false," +
            "\"choices\":[{\"value\":\"yes\",\"label\":\"Yes\",\"description\":null}]}";

        var okPersisted = AskUserPromptFactory.TryBuildFromPersistedJson(persisted, "conv-fallback", out var fromJson);

        var evt = new AgentStreamEvent
        {
            Metadata = Metadata(
                "{\"requestId\":\"req-9\",\"conversationId\":\"conv-9\",\"prompt\":\"Deploy?\"," +
                "\"inputType\":\"SingleChoice\"," +
                "\"choices\":\"[{\\\"value\\\":\\\"yes\\\",\\\"label\\\":\\\"Yes\\\"}]\"}")
        };
        var okStream = AskUserPromptFactory.TryBuildFromStreamEvent(evt, out var fromStream);

        Assert.True(okPersisted);
        Assert.True(okStream);
        Assert.Equal(fromStream!.RequestId, fromJson!.RequestId);
        Assert.Equal(fromStream.ConversationId, fromJson.ConversationId);
        Assert.Equal(fromStream.Prompt, fromJson.Prompt);
        Assert.Equal(fromStream.InputType, fromJson.InputType);
        Assert.Equal(fromStream.Choices![0].Value, fromJson.Choices![0].Value);
        Assert.Equal(fromStream.Choices[0].Label, fromJson.Choices[0].Label);
    }
}
