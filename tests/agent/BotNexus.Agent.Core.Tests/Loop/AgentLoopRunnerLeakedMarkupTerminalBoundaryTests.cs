using System.Text.Json;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Recovery of leaked invoke markup must not overwrite an unsuccessful provider terminal (#4621).
/// Exercises RunAsync's real RunLoopAsync path, including capability lookup and tool dispatch.
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public sealed class AgentLoopRunnerLeakedMarkupTerminalBoundaryTests
{
    private const string LeakedText = "Partial answer.\n<invoke name=\"shell\"><parameter name=\"command\">synthetic-command</parameter></invoke>";
    private const string Diagnostic = "Synthetic terminal diagnostic";

    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    [InlineData(StopReason.Length)]
    [InlineData(StopReason.Sensitive)]
    [InlineData(StopReason.Refusal)]
    public async Task RunAsync_UnsuccessfulTerminalWithValidInvoke_PreservesTerminalAndTextWithoutDispatch(
        StopReason reason)
    {
        var api = $"leaked-markup-terminal-4621-{reason}";
        var tool = new RecordingTool();
        var providerCalls = 0;
        using var registration = RegisterProvider(api, reason, () => ++providerCalls);
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api));
        config.LlmClient.GetCapabilities(config.Model).RecoversLeakedToolCallMarkup.ShouldBeTrue();
        var events = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("Inspect the synthetic response")],
            new AgentContext(null, [], [tool]),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            timeout.Token);

        // A setup failure must not satisfy the no-dispatch assertion: the provider and loop started.
        events.OfType<AgentStartEvent>().ShouldHaveSingleItem();
        events.OfType<TurnStartEvent>().ShouldHaveSingleItem();
        providerCalls.ShouldBe(1);
        tool.ExecuteCount.ShouldBe(0);
        result.OfType<ToolResultAgentMessage>().ShouldBeEmpty();
        events.OfType<ToolExecutionStartEvent>().ShouldBeEmpty();
        events.OfType<ToolExecutionEndEvent>().ShouldBeEmpty();

        var assistant = result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
        assistant.FinishReason.ShouldBe(reason);
        assistant.Content.ShouldBe(LeakedText);
        assistant.ErrorMessage.ShouldBe(Diagnostic);
        (assistant.ToolCalls ?? []).ShouldBeEmpty();
        var turn = events.OfType<TurnEndEvent>().ShouldHaveSingleItem();
        turn.Message.ShouldBe(assistant);
        turn.ToolResults.ShouldBeEmpty();
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(end);
        end.Messages.OfType<AssistantAgentMessage>().ShouldHaveSingleItem().ShouldBe(assistant);
        if (reason == StopReason.Error)
            end.Completion.Status.ShouldBe(RunCompletionStatus.Failed);
        if (reason == StopReason.Aborted)
            end.Completion.Status.ShouldBe(RunCompletionStatus.Cancelled);
    }

    [Fact]
    public async Task RunAsync_StopWithSameValidInvoke_RecoversAndDispatchesOnce()
    {
        const string api = "leaked-markup-terminal-4621-stop-control";
        var tool = new RecordingTool();
        var providerCalls = 0;
        using var registration = RegisterProvider(api, StopReason.Stop, () => ++providerCalls);
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api));
        config.LlmClient.GetCapabilities(config.Model).RecoversLeakedToolCallMarkup.ShouldBeTrue();
        var events = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("Inspect the synthetic response")],
            new AgentContext(null, [], [tool]),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            timeout.Token);

        providerCalls.ShouldBe(2);
        tool.ExecuteCount.ShouldBe(1);
        tool.LastArguments.ShouldNotBeNull().ShouldContainKey("command");
        tool.LastArguments.ShouldNotBeNull()["command"].ShouldBe("synthetic-command");
        var assistants = result.OfType<AssistantAgentMessage>().ToList();
        assistants.Count.ShouldBe(2);
        var recovered = assistants[0];
        recovered.FinishReason.ShouldBe(StopReason.ToolUse);
        recovered.Content.ShouldContain("Partial answer.");
        recovered.Content.ShouldNotContain("<invoke");
        recovered.Content.ShouldNotContain("<parameter");
        var call = recovered.ToolCalls.ShouldNotBeNull().ShouldHaveSingleItem();
        call.Name.ShouldBe("shell");
        call.Arguments["command"].ShouldBe("synthetic-command");
        result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().ToolCallId.ShouldBe(call.Id);
        events.OfType<ToolExecutionStartEvent>().ShouldHaveSingleItem();
        events.OfType<ToolExecutionEndEvent>().ShouldHaveSingleItem();
        assistants[1].Content.ShouldBe("Done.");
        assistants[1].FinishReason.ShouldBe(StopReason.Stop);
        events.OfType<TurnEndEvent>().Count().ShouldBe(2);
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(end);
    }

    private static IDisposable RegisterProvider(string api, StopReason reason, Func<int> nextCall)
        => TestHelpers.RegisterProvider(new TestApiProvider(
            api,
            simpleStreamFactory: (_, _, _) => nextCall() == 1
                ? CreateTerminalResponse(reason)
                : TestStreamFactory.CreateTextResponse("Done."),
            capabilities: new ProviderCapabilities(RecoversLeakedToolCallMarkup: true)));

    private static LlmStream CreateTerminalResponse(StopReason reason)
    {
        var stream = new LlmStream();
        var message = new AssistantMessage(
            Content: [new TextContent(LeakedText)],
            Api: "test-api", Provider: "test-provider", ModelId: "test-model",
            Usage: new Usage { Input = 7, Output = 3, TotalTokens = 10 },
            StopReason: reason, ErrorMessage: Diagnostic, ResponseId: "terminal-4621", Timestamp: 1);
        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, LeakedText, message));
        stream.Push(new TextEndEvent(0, LeakedText, message));
        if (reason is StopReason.Error or StopReason.Aborted)
            stream.Push(new ErrorEvent(reason, message));
        else
            stream.Push(new DoneEvent(reason, message));
        stream.End(message);
        return stream;
    }

    private sealed class RecordingTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{"type":"object","properties":{"command":{"type":"string"}}}""").RootElement.Clone();
        public int ExecuteCount { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }
        public string Name => "shell";
        public string Label => "Synthetic shell";
        public Tool Definition => new(Name, "Record arguments without executing a command", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            ExecuteCount++;
            LastArguments = arguments;
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }
}
