using System.Collections.Generic;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using BotNexus.Domain.Primitives;
using AgentCoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for the pure agent-event -> <see cref="AgentStreamEvent"/> mapping extracted from
/// <c>StreamCoreAsync</c> (#1382). Because the mapping is now a static pure function it can be
/// exercised directly without driving a live <c>_agent.Subscribe</c> channel pipeline.
/// </summary>
public sealed class InProcessIsolationStrategyMapAgentEventTests
{
    private const string MessageId = "msg-abc123";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void MapAgentEvent_AssistantMessageStart_MapsToMessageStart()
    {
        var evt = new MessageStartEvent(new AssistantAgentMessage("hi"), Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.MessageStart);
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_NonAssistantMessageStart_MapsToNull()
    {
        // MessageStartEvent for a user message must not produce a stream start marker.
        var evt = new MessageStartEvent(new AgentCoreUserMessage("hello"), Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldBeNull();
    }

    [Fact]
    public void MapAgentEvent_ContentDelta_MapsToContentDelta()
    {
        var evt = new MessageUpdateEvent(
            Message: new AssistantAgentMessage("partial"),
            ContentDelta: "partial",
            IsThinking: false,
            ToolCallId: null,
            ToolName: null,
            ArgumentsDelta: null,
            FinishReason: null,
            InputTokens: null,
            OutputTokens: null,
            Timestamp: Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.ContentDelta);
        result.ContentDelta.ShouldBe("partial");
        result.ThinkingContent.ShouldBeNull();
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_ThinkingDelta_MapsToThinkingDelta()
    {
        var evt = new MessageUpdateEvent(
            Message: new AssistantAgentMessage("reasoning"),
            ContentDelta: "reasoning",
            IsThinking: true,
            ToolCallId: null,
            ToolName: null,
            ArgumentsDelta: null,
            FinishReason: null,
            InputTokens: null,
            OutputTokens: null,
            Timestamp: Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.ThinkingDelta);
        result.ThinkingContent.ShouldBe("reasoning");
        result.ContentDelta.ShouldBeNull();
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_MessageUpdateWithNullContentDelta_MapsToNull()
    {
        // A tool-call-streaming update (no content delta) must not emit a content/thinking delta.
        var evt = new MessageUpdateEvent(
            Message: new AssistantAgentMessage(string.Empty),
            ContentDelta: null,
            IsThinking: false,
            ToolCallId: "call-1",
            ToolName: "read",
            ArgumentsDelta: "{\"path\":",
            FinishReason: null,
            InputTokens: null,
            OutputTokens: null,
            Timestamp: Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldBeNull();
    }

    [Fact]
    public void MapAgentEvent_ToolExecutionStart_MapsToToolStart()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/tmp/x" };
        var evt = new ToolExecutionStartEvent("call-7", "read", args, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.ToolStart);
        result.ToolCallId.ShouldBe("call-7");
        result.ToolName.ShouldBe("read");
        result.ToolArgs.ShouldBe(args);
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_ToolExecutionEnd_MapsToToolEndWithResultAndError()
    {
        var content = new AgentToolContent(AgentToolContentType.Text, "done");
        var result0 = new AgentToolResult(new List<AgentToolContent> { content });
        var evt = new ToolExecutionEndEvent("call-7", "read", result0, IsError: true, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.ToolEnd);
        result.ToolCallId.ShouldBe("call-7");
        result.ToolName.ShouldBe("read");
        result.ToolResult.ShouldBe(content.Value);
        result.ToolIsError.ShouldBe(true);
        result.MessageId.ShouldBe(MessageId);
    }

    [Theory]
    [InlineData("{\"name\":\"alpha\"}")]
    [InlineData("[1,2,3]")]
    public void MapAgentEvent_ToolExecutionEnd_PreservesRawStructuredResult(string rawResult)
    {
        var content = new AgentToolContent(AgentToolContentType.Text, rawResult);
        var toolResult = new AgentToolResult([content]);
        var evt = new ToolExecutionEndEvent("call-json", "structured", toolResult, IsError: false, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result.ToolResult.ShouldBe(rawResult);
    }

    [Fact]
    public void MapAgentEvent_ToolExecutionEnd_WithNoContent_ToolResultIsNull()
    {
        var result0 = new AgentToolResult(new List<AgentToolContent>());
        var evt = new ToolExecutionEndEvent("call-9", "exec", result0, IsError: false, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.ToolEnd);
        result.ToolResult.ShouldBeNull();
        result.ToolIsError.ShouldBe(false);
    }

    [Fact]
    public void MapAgentEvent_AssistantMessageEnd_MapsToMessageEndWithUsage()
    {
        var assistant = new AssistantAgentMessage(
            "final",
            Usage: new AgentUsage(InputTokens: 12, OutputTokens: 34, CacheRead: 5, CacheWrite: 6));
        var evt = new MessageEndEvent(assistant, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.MessageEnd);
        result.MessageId.ShouldBe(MessageId);
        result.Usage.ShouldNotBeNull();
        result.Usage!.InputTokens.ShouldBe(12);
        result.Usage.OutputTokens.ShouldBe(34);
        result.Usage.CacheRead.ShouldBe(5);
        result.Usage.CacheWrite.ShouldBe(6);
    }

    [Fact]
    public void MapAgentEvent_AssistantMessageEnd_WithNullUsage_MapsToMessageEndWithNullUsage()
    {
        var evt = new MessageEndEvent(new AssistantAgentMessage("final", Usage: null), Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.MessageEnd);
        result.Usage.ShouldBeNull();
    }

    [Fact]
    public void MapAgentEvent_NonAssistantMessageEnd_MapsToNull()
    {
        var evt = new MessageEndEvent(new AgentCoreUserMessage("user echo"), Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldBeNull();
    }

    [Fact]
    public void MapAgentEvent_ToolUpdateWithAskUserRequest_MapsToUserInputRequired()
    {
        var ask = new AskUserRequest
        {
            RequestId = "req-1",
            ConversationId = ConversationId.From("conv-1"),
            SessionId = SessionId.From("sess-1"),
            AgentId = AgentId.From("agent-1"),
            Prompt = "Pick one"
        };
        var partial = new AgentToolResult(new List<AgentToolContent>(), Details: ask);
        var evt = new ToolExecutionUpdateEvent("call-x", "ask_user", new Dictionary<string, object?>(), partial, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.UserInputRequired);
        result.UserInputRequest.ShouldBe(ask);
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_ToolUpdateWithoutAskUserRequest_MapsToNull()
    {
        // A tool update whose PartialResult.Details is not an AskUserRequest must not map.
        var partial = new AgentToolResult(new List<AgentToolContent>(), Details: "not-an-ask");
        var evt = new ToolExecutionUpdateEvent("call-x", "read", new Dictionary<string, object?>(), partial, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldBeNull();
    }

    [Fact]
    public void MapAgentEvent_TurnEnd_MapsToTurnEnd()
    {
        var evt = new TurnEndEvent(
            new AssistantAgentMessage("done"),
            new List<ToolResultAgentMessage>(),
            Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.TurnEnd);
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_AgentStart_MapsToRunStarted()
    {
        // AgentStartEvent brackets the whole loop -> RunStarted (the authoritative "busy" signal).
        var evt = new AgentStartEvent(Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.RunStarted);
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapAgentEvent_AgentEnd_MapsToRunEnded()
    {
        // AgentEndEvent fires once when the entire loop settles -> RunEnded (the authoritative idle signal).
        var evt = new AgentEndEvent(new List<AgentMessage>(), null, Now);

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.RunEnded);
        result.MessageId.ShouldBe(MessageId);
    }
}
