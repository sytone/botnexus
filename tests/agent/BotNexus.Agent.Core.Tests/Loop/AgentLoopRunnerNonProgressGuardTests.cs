using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.ExtensionPoints.ToolExecution;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

[Collection(ApiProviderRegistryCollection.Name)]
public sealed class AgentLoopRunnerNonProgressGuardTests
{
    private const int GuardLimit = 6;

    private sealed class ScriptedTool(string name, Func<string, string> resultFactory) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        private int _executeCount;

        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, "test tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executeCount);
            var variant = arguments.TryGetValue("variant", out var raw) || arguments.TryGetValue("oldText", out raw)
                ? raw?.ToString() ?? "" : "";
            return Task.FromResult(new AgentToolResult([
                new AgentToolContent(AgentToolContentType.Text, resultFactory(variant))]));
        }
    }

    [Fact]
    public async Task RepeatedNoChangeEditsAcrossVariantsAndCallIds_ParksAndRetainsEveryExecutedResult()
    {
        const string api = "non-progress-edit-limit";
        var tool = new ScriptedTool("edit", _ => "No changes needed - replacement text is already present.");
        using var provider = RegisterSequencedToolUseProvider(api, "edit", ["old-a", "old-b", "old-a", "old-b", "old-a", "old-b"]);
        var diagnostics = new List<string>();
        var events = new List<AgentEvent>();

        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("make the requested change")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api), onDiagnostic: diagnostics.Add),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(GuardLimit);
        messages.OfType<ToolResultAgentMessage>().Select(message => message.ToolCallId).Distinct().Count().ShouldBe(GuardLimit);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status
            .ShouldBe(RunCompletionStatus.Parked);
        var stopDetail = events.OfType<AgentEndEvent>().Single().Completion.Detail.ShouldNotBeNull();
        stopDetail.ShouldContain("non-progress");
        diagnostics.ShouldNotBeEmpty();
        diagnostics.ShouldAllBe(message => !message.Contains("replacement text", StringComparison.OrdinalIgnoreCase));
        diagnostics.ShouldAllBe(message => !message.Contains("variant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AlternatingFoundZeroAndNoChangeEdits_AreOneBoundedStrategy()
    {
        const string api = "non-progress-mixed-edits";
        var tool = new ScriptedTool("edit", variant => variant.StartsWith("missing", StringComparison.Ordinal)
            ? throw new InvalidOperationException("Expected exactly one match for edits[].oldText, but found 0.")
            : "No changes needed - replacement text is already present.");
        using var provider = RegisterSequencedToolUseProvider(api, "edit",
            ["missing-a", "noop-a", "missing-b", "noop-b", "missing-a", "noop-a"]);
        var events = new List<AgentEvent>();
        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("edit the file")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(GuardLimit);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
    }

    [Fact]
    public async Task RepeatedFoundZeroEditErrors_AreBoundedAndResultsRemainErrors()
    {
        const string api = "non-progress-edit-errors";
        var tool = new ScriptedTool("edit", _ => throw new InvalidOperationException("must not execute"));
        using var provider = RegisterSequencedToolUseProvider(api, "edit", ["old-a", "old-b", "old-a", "old-b", "old-a", "old-b"]);

        // The tool executor's before-hook returns a deterministic found-0 error without dispatch.
        var calls = 0;
        var config = TestHelpers.CreateTestConfig(
            model: TestHelpers.CreateTestModel(api),
            beforeToolAudit: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<ToolExecutionDecision?>(new ToolExecutionDecision(true, "Expected exactly one match for edits[].oldText, but found 0."));
            });

        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("edit the file")],
            new AgentContext(null, [], [tool]), config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        calls.ShouldBe(GuardLimit);
        tool.ExecuteCount.ShouldBe(0);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(GuardLimit);
        messages.OfType<ToolResultAgentMessage>().ShouldAllBe(result => result.IsError);
    }

    [Fact]
    public async Task RepeatedUnchangedReadResults_AreBoundedAndParked()
    {
        const string api = "non-progress-unchanged-read";
        var tool = new ScriptedTool("get_current_time", _ => "2026-10-03T10:00:00Z");
        using var provider = RegisterRepeatedToolUseProvider(api, "get_current_time", GuardLimit);
        var events = new List<AgentEvent>();

        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("inspect the file")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(GuardLimit);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
    }

    [Fact]
    public async Task AlternatingUnchangedStatusAndClockReads_ParkWithoutTreatingTimeAsProgress()
    {
        const string api = "non-progress-alternating-status-clock";
        var clockTicks = 0;
        var status = new ScriptedTool("shell", _ => " M same-file.txt");
        var clock = new ScriptedTool("get_datetime", _ => "tick " + Interlocked.Increment(ref clockTicks));
        var index = -1;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        {
            var next = Interlocked.Increment(ref index);
            if (next >= GuardLimit) return TestStreamFactory.CreateTextResponse("Done.");
            var shell = next % 2 == 0;
            return TestStreamFactory.CreateToolCallResponse(($"call-{next}", shell ? "shell" : "get_datetime",
                shell ? new Dictionary<string, object?> { ["command"] = "git status --short; git diff --stat" } : new Dictionary<string, object?>()));
        }));
        var events = new List<AgentEvent>();
        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("wait for child")], new AgentContext(null, [], [status, clock]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; }, CancellationToken.None);
        status.ExecuteCount.ShouldBe(3);
        clock.ExecuteCount.ShouldBe(3);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(GuardLimit);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
    }

    [Fact]
    public async Task ChangingClockOutputIsNotEvidenceOfWorkProgress()
    {
        const string api = "non-progress-moving-clock";
        var number = 0;
        var tool = new ScriptedTool("get_datetime", _ => "clock tick " + Interlocked.Increment(ref number));
        using var provider = RegisterRepeatedToolUseProvider(api, "get_datetime", GuardLimit);
        var events = new List<AgentEvent>();
        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("check status")], new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; }, CancellationToken.None);
        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(GuardLimit);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
    }

    [Fact]
    public async Task ChangingReadResultsBreakTheNonProgressSequence()
    {
        const string api = "non-progress-changing-read";
        var tool = new ScriptedTool("read", variant => "contents " + variant);
        using var provider = RegisterSequencedToolUseProvider(api, "read", ["one", "two", "three", "four", "five", "six"]);
        var events = new List<AgentEvent>();

        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("inspect the file")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(6);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Completed);
    }

    [Fact]
    public async Task ChangedReadResultAtSameTargetAndArgumentsResetsTheGuard()
    {
        const string api = "non-progress-changing-same-read";
        var number = 0;
        var tool = new ScriptedTool("read", _ => "contents " + Interlocked.Increment(ref number));
        using var provider = RegisterSequencedToolUseProvider(api, "read", Enumerable.Repeat("same", GuardLimit).ToArray());
        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("inspect")], new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask, CancellationToken.None);
        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<AssistantAgentMessage>().Last().FinishReason.ShouldBe(StopReason.Stop);
    }

    [Fact]
    public async Task UserSteerAtTheStopBoundaryResumesTheLoopInsteadOfParking()
    {
        const string api = "non-progress-steer-resumes";
        var tool = new ScriptedTool("get_current_time", _ => "2026-10-03T10:00:00Z");
        using var provider = RegisterRepeatedToolUseProvider(api, "get_current_time", GuardLimit);
        var steeringPolls = 0;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            SteeringMessageProvider = _ =>
            {
                var poll = Interlocked.Increment(ref steeringPolls);
                IReadOnlyList<AgentMessage> messages = poll == GuardLimit + 1
                    ? [new AgentUserMessage("The child has completed; stop waiting and summarize.")]
                    : [];
                return Task.FromResult(messages);
            }
        };

        var events = new List<AgentEvent>();
        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("check the clock")],
            new AgentContext(null, [], [tool]),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<AgentUserMessage>().ShouldContain(message => message.Content.Contains("child has completed", StringComparison.Ordinal));
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Completed);
        messages.OfType<AssistantAgentMessage>().Last().FinishReason.ShouldBe(StopReason.Stop);
    }

    [Fact]
    public async Task AgentOptions_PassesConfiguredToolProgressPolicyIntoLoopRuntime()
    {
        const string api = "non-progress-agent-options";
        var tool = new ScriptedTool("custom_probe", _ => "still waiting");
        using var provider = RegisterRepeatedToolUseProvider(api, "custom_probe", GuardLimit);
        var policyCalls = 0;
        var initial = new AgentInitialState(
            Model: TestHelpers.CreateTestModel(api),
            Tools: [tool],
            Messages: []);
        var options = TestHelpers.CreateTestOptions(initial, initial.Model) with
        {
            ToolProgressPolicy = (_, _) =>
            {
                Interlocked.Increment(ref policyCalls);
                return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                    "agent-options-probe", "waiting", "custom-wait"));
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        await agent.PromptAsync("wait for the custom operation");

        policyCalls.ShouldBe(GuardLimit);
        agent.State.LastCompletion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Parked);
    }

    [Fact]
    public async Task ConfiguredToolProgressPolicy_ClassifiesCustomToolWithoutOwningLoopState()
    {
        const string api = "non-progress-custom-policy";
        var tool = new ScriptedTool("custom_probe", _ => "still waiting");
        using var provider = RegisterRepeatedToolUseProvider(api, "custom_probe", GuardLimit);
        var policyCalls = 0;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            ToolProgressPolicy = (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref policyCalls);
                context.ToolCall.Name.ShouldBe("custom_probe");
                context.ToolResult.ToolName.ShouldBe("custom_probe");
                return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                    "custom-probe",
                    "waiting",
                    "custom-wait",
                    "Use the custom completion signal instead of probing again."));
            }
        };
        var events = new List<AgentEvent>();

        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("wait for the custom operation")],
            new AgentContext(null, [], [tool]),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        policyCalls.ShouldBe(GuardLimit);
        tool.ExecuteCount.ShouldBe(GuardLimit);
        messages.OfType<AgentUserMessage>().ShouldContain(message =>
            message.Content == "Use the custom completion signal instead of probing again.");
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status
            .ShouldBe(RunCompletionStatus.Parked);
    }

    [Fact]
    public async Task ConfiguredToolProgressPolicy_ProgressDecisionResetsTheSequence()
    {
        const string api = "non-progress-custom-reset";
        var tool = new ScriptedTool("custom_probe", _ => "result");
        using var provider = RegisterRepeatedToolUseProvider(api, "custom_probe", GuardLimit);
        var policyCalls = 0;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            ToolProgressPolicy = (_, _) =>
            {
                var call = Interlocked.Increment(ref policyCalls);
                return Task.FromResult<ToolProgressDecision?>(call == 3
                    ? ToolProgressDecision.Progress
                    : ToolProgressDecision.NoProgress("custom-probe", "waiting", "custom-wait"));
            }
        };
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("wait")],
            new AgentContext(null, [], [tool]),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        policyCalls.ShouldBe(GuardLimit);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status
            .ShouldBe(RunCompletionStatus.Completed);
    }

    [Fact]
    public async Task ChangedToolResultBreaksTheNonProgressSequence()
    {
        const string api = "non-progress-changing-result";
        var tool = new ScriptedTool("edit", variant => variant == "changed"
            ? "File updated successfully."
            : "No changes needed - replacement text is already present.");
        using var provider = RegisterSequencedToolUseProvider(api, "edit", ["a", "b", "changed", "c", "d", "e"]);

        var messages = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("edit the file")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(6);
        messages.OfType<AssistantAgentMessage>().Last().FinishReason.ShouldBe(StopReason.Stop);
    }

    private static IDisposable RegisterRepeatedToolUseProvider(string api, string toolName, int count)
        => RegisterSequencedToolUseProvider(api, toolName, Enumerable.Range(0, count).Select(i => $"variant-{i % 2}").ToArray());

    private static IDisposable RegisterSequencedToolUseProvider(string api, string toolName, IReadOnlyList<string> variants)
    {
        var index = -1;
        var provider = new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        {
            var callNumber = Interlocked.Increment(ref index);
            if (callNumber >= variants.Count)
                return TestStreamFactory.CreateTextResponse("Done.");

            var arguments = string.Equals(toolName, "edit", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, object?>
                {
                    ["path"] = "same-file.txt",
                    ["oldText"] = variants[callNumber],
                    ["newText"] = "replacement",
                }
                : toolName is "get_current_time" or "get_datetime"
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["path"] = "same-file.txt", ["variant"] = variants[callNumber] };
            return TestStreamFactory.CreateToolCallResponse((
                $"call-{callNumber}",
                toolName,
                arguments));
        });
        return TestHelpers.RegisterProvider(provider);
    }
}
