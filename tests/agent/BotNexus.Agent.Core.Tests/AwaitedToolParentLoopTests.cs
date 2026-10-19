using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using Xunit.Abstractions;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Core half of #4793: an awaited child-shaped result resumes the existing parent tool loop.
/// This fake models only the IAgentTool boundary; Gateway manager receipt/wakeup tests own
/// the separate guarantee that completing a real child does not dispatch another parent run.
/// </summary>
public sealed class AwaitedToolParentLoopTests(ITestOutputHelper output)
{
    private static readonly TimeSpan DiagnosticDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public Task PromptAsync_AwaitedToolResult_ContinuesParentWithExactlyTwoProviderCalls()
        => AssertContinuationAsync(transformAndBudget: false);

    [Fact]
    public Task PromptAsync_AwaitedToolResult_TransformsAndBudgetsBeforeSecondProviderCall()
        => AssertContinuationAsync(transformAndBudget: true);

    private static async Task AssertContinuationAsync(bool transformAndBudget)
    {
        var api = $"awaited-tool-{Guid.NewGuid():N}";
        var tool = new AwaitedResultTool();
        var providerCalls = 0;
        var transformerCalls = 0;
        ToolResultMessage? providerResult = null;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            api,
            simpleStreamFactory: (_, context, _) =>
            {
                if (Interlocked.Increment(ref providerCalls) == 1)
                    return TestStreamFactory.CreateToolCallResponse(("child-call", tool.Name, new Dictionary<string, object?>()));

                providerResult = context.Messages.OfType<ToolResultMessage>().SingleOrDefault();
                return TestStreamFactory.CreateTextResponse("parent used child result");
            }));
        var model = TestHelpers.CreateTestModel(api);
        var initial = new AgentInitialState(Model: model, Tools: [tool], Messages: []);
        var options = TestHelpers.CreateTestOptions(initial, model) with
        {
            MaxToolOutputBytes = 1024,
            ToolResultTransformer = transformAndBudget
                ? (context, _) =>
                {
                    Interlocked.Increment(ref transformerCalls);
                    context.Result.Content.ShouldHaveSingleItem().Value.ShouldBe("child final result");
                    return Task.FromResult<ToolResultTransformResult?>(new ToolResultTransformResult(
                        Content: [new AgentToolContent(AgentToolContentType.Text, "redacted child result " + new string('x', 8192))]));
                }
                : null
        };
        var agent = new BotNexus.Agent.Core.Agent(options);
        var run = agent.PromptAsync("await the child and use its result");
        try
        {
            // Entered is raised before awaiting an unresolved result, a deterministic boundary:
            // no acknowledgement/provider continuation is possible until the tool is released.
            await tool.Entered.Task.WaitAsync(DiagnosticDeadline);
            Volatile.Read(ref providerCalls).ShouldBe(1);
            run.IsCompleted.ShouldBeFalse();
            agent.State.Messages.OfType<ToolResultAgentMessage>().ShouldBeEmpty();

            tool.Complete("child final result");
            var messages = await run.WaitAsync(DiagnosticDeadline);

            providerCalls.ShouldBe(2);
            tool.Executions.ShouldBe(1);
            agent.Status.ShouldBe(AgentStatus.Idle);
            agent.HasQueuedMessages.ShouldBeFalse();
            var result = messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
            result.ToolCallId.ShouldBe("child-call");
            result.ToolName.ShouldBe(tool.Name);
            result.IsError.ShouldBeFalse();
            messages.OfType<AssistantAgentMessage>().Last().Content.ShouldBe("parent used child result");
            providerResult.ShouldNotBeNull();
            providerResult.ToolCallId.ShouldBe("child-call");
            providerResult.IsError.ShouldBeFalse();
            var modelText = string.Join('\n', providerResult.Content.OfType<TextContent>().Select(block => block.Text));
            modelText.ShouldBe(string.Join('\n', result.Result.Content.Select(block => block.Value)));
            if (transformAndBudget)
            {
                transformerCalls.ShouldBe(1);
                modelText.ShouldStartWith("redacted child result ");
                modelText.ShouldContain("tool output truncated");
                modelText.ShouldNotContain("child final result");
                Encoding.UTF8.GetByteCount(result.Result.Content[0].Value).ShouldBeLessThanOrEqualTo(1024);
                modelText.Length.ShouldBeLessThan(8192);
            }
            else
            {
                transformerCalls.ShouldBe(0);
                modelText.ShouldBe("child final result");
            }
        }
        finally
        {
            tool.Complete("cleanup");
            _ = await run.WaitAsync(DiagnosticDeadline);
        }
    }

    [Fact]
    public async Task PromptAsync_CancelledWhileAwaitingTool_StopsWithoutSecondProviderCall()
    {
        var api = $"awaited-tool-cancel-{Guid.NewGuid():N}";
        var tool = new AwaitedResultTool();
        var providerCalls = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            api,
            simpleStreamFactory: (_, _, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return TestStreamFactory.CreateToolCallResponse(("child-call", tool.Name, new Dictionary<string, object?>()));
            }));
        var model = TestHelpers.CreateTestModel(api);
        var initial = new AgentInitialState(Model: model, Tools: [tool], Messages: []);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(initial, model));
        using var cancellation = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var run = agent.PromptAsync("await child", cancellation.Token);
        await WithFailureDiagnosticsAsync(async () =>
        {
            await tool.Entered.Task.WaitAsync(DiagnosticDeadline);
            cancellation.Cancel();
            await tool.Cancelled.Task.WaitAsync(DiagnosticDeadline);
            var messages = await run.WaitAsync(DiagnosticDeadline);

            providerCalls.ShouldBe(1);
            tool.Executions.ShouldBe(1);
            messages.OfType<ToolResultAgentMessage>().ShouldBeEmpty();
            messages.OfType<AssistantAgentMessage>().Last().FinishReason.ShouldBe(StopReason.Aborted);
            agent.Status.ShouldBe(AgentStatus.Idle);
            agent.HasQueuedMessages.ShouldBeFalse();
        },
        () => CaptureBoundarySnapshot(agent, run, Volatile.Read(ref providerCalls), tool, elapsed.Elapsed),
        snapshot => output.WriteLine(snapshot),
        async () =>
        {
            cancellation.Cancel();
            tool.Complete("cleanup");
            _ = await run.WaitAsync(DiagnosticDeadline);
        });
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    public async Task FailureDiagnostics_BoundaryFailure_CapturesBeforeCleanupAndPreservesException(
        int providerCalls, bool argumentsPrepared, bool toolEntered)
    {
        var tool = new AwaitedResultTool();
        var model = TestHelpers.CreateTestModel("synthetic-boundary");
        var initial = new AgentInitialState(Model: model, Tools: [tool], Messages: []);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(initial, model));
        var run = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        if (argumentsPrepared)
            _ = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>());
        var execution = toolEntered
            ? tool.ExecuteAsync("synthetic-call", new Dictionary<string, object?>(), cancellation.Token)
            : null;
        var original = new TimeoutException("private failure text must not be emitted");
        var snapshots = new List<string>();
        var cleanupCalled = false;

        var observed = await Should.ThrowAsync<TimeoutException>(() => WithFailureDiagnosticsAsync(
            () => Task.FromException(original),
            () => CaptureBoundarySnapshot(agent, run.Task, providerCalls, tool, TimeSpan.FromMilliseconds(123)),
            snapshot =>
            {
                cleanupCalled.ShouldBeFalse();
                snapshots.Add(snapshot);
            },
            async () =>
            {
                cleanupCalled = true;
                cancellation.Cancel();
                if (execution is not null)
                    _ = await Should.ThrowAsync<OperationCanceledException>(async () => { _ = await execution; });
                run.SetResult(true);
                throw new TimeoutException("cleanup timeout must not replace original");
            }));

        ReferenceEquals(observed, original).ShouldBeTrue();
        cleanupCalled.ShouldBeTrue();
        var snapshot = snapshots.ShouldHaveSingleItem();
        snapshot.Length.ShouldBeLessThan(512);
        snapshot.ShouldNotContain(original.Message);
        snapshot.ShouldNotContain("synthetic");
        using var document = JsonDocument.Parse(snapshot);
        var fields = document.RootElement;
        fields.EnumerateObject().Count().ShouldBe(12);
        fields.GetProperty("ProviderCalls").GetInt32().ShouldBe(providerCalls);
        fields.GetProperty("ArgumentsPrepared").GetInt32().ShouldBe(argumentsPrepared ? 1 : 0);
        fields.GetProperty("ToolEntered").GetBoolean().ShouldBe(toolEntered);
        fields.GetProperty("ToolExecutions").GetInt32().ShouldBe(toolEntered ? 1 : 0);
        fields.GetProperty("ToolCancelled").GetBoolean().ShouldBeFalse();
        fields.GetProperty("RunStatus").GetString().ShouldBe(nameof(TaskStatus.WaitingForActivation));
        fields.GetProperty("AgentStatus").GetString().ShouldBe(nameof(AgentStatus.Idle));
        fields.GetProperty("FinishReason").ValueKind.ShouldBe(JsonValueKind.Null);
        fields.GetProperty("MessageCount").GetInt32().ShouldBe(0);
        fields.GetProperty("ResultCount").GetInt32().ShouldBe(0);
        fields.GetProperty("HasQueuedMessages").GetBoolean().ShouldBeFalse();
        fields.GetProperty("ElapsedMilliseconds").GetDouble().ShouldBe(123);
        tool.Cancelled.Task.IsCompletedSuccessfully.ShouldBe(toolEntered);
    }

    [Fact]
    public async Task FailureDiagnostics_OutputFailure_DoesNotReplaceOriginalException()
    {
        var original = new InvalidOperationException("original");
        var cleanupCalled = false;

        var observed = await Should.ThrowAsync<InvalidOperationException>(() => WithFailureDiagnosticsAsync(
            () => Task.FromException(original),
            () => "{}",
            _ => throw new IOException("output failed"),
            () =>
            {
                cleanupCalled = true;
                return Task.CompletedTask;
            }));

        ReferenceEquals(observed, original).ShouldBeTrue();
        cleanupCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task FailureDiagnostics_CleanupFailureAfterSuccess_RemainsVisible()
    {
        var cleanupFailure = new TimeoutException("cleanup failed");

        var observed = await Should.ThrowAsync<TimeoutException>(() => WithFailureDiagnosticsAsync(
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("no snapshot on success"),
            _ => throw new InvalidOperationException("no output on success"),
            () => Task.FromException(cleanupFailure)));

        ReferenceEquals(observed, cleanupFailure).ShouldBeTrue();
    }

    // This seam is local to the failing test: diagnostics and cleanup must never mask
    // its original assertion/deadline failure, but cleanup failures after success still fail.
    private static async Task WithFailureDiagnosticsAsync(
        Func<Task> assertion, Func<string> snapshot, Action<string> write, Func<Task> cleanup)
    {
        var failed = false;
        try
        {
            await assertion();
        }
        catch
        {
            failed = true;
            try
            {
                write(snapshot());
            }
            catch
            {
                // A diagnostic sink or concurrent state read is only best-effort evidence.
            }
            throw;
        }
        finally
        {
            try
            {
                await cleanup();
            }
            catch when (failed)
            {
                // Preserve the original failure rather than replacing it with cleanup's deadline.
            }
        }
    }

    private static string CaptureBoundarySnapshot(
        BotNexus.Agent.Core.Agent agent, Task run, int providerCalls, AwaitedResultTool tool, TimeSpan elapsed)
    {
        var messages = agent.State.Messages.ToArray();
        // An allowlist of counts/statuses only: never serialize messages, arguments, IDs,
        // results or exception text. Queue presence is the only public queue observation.
        return JsonSerializer.Serialize(new
        {
            ProviderCalls = providerCalls,
            ArgumentsPrepared = tool.ArgumentsPrepared,
            ToolEntered = tool.Entered.Task.IsCompletedSuccessfully,
            ToolExecutions = tool.Executions,
            ToolCancelled = tool.Cancelled.Task.IsCompletedSuccessfully,
            RunStatus = run.Status.ToString(),
            AgentStatus = agent.Status.ToString(),
            FinishReason = messages.OfType<AssistantAgentMessage>().LastOrDefault()?.FinishReason.ToString(),
            MessageCount = messages.Length,
            ResultCount = messages.OfType<ToolResultAgentMessage>().Count(),
            agent.HasQueuedMessages,
            ElapsedMilliseconds = elapsed.TotalMilliseconds
        });
    }

    private sealed class AwaitedResultTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executions;
        private int _argumentsPrepared;

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions => Volatile.Read(ref _executions);
        public int ArgumentsPrepared => Volatile.Read(ref _argumentsPrepared);
        public string Name => "await_child_result";
        public string Label => "Await child result";
        public Tool Definition => new(Name, "Signal-driven awaited result test seam", Schema);
        public void Complete(string result) => _result.TrySetResult(result);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _argumentsPrepared);
            return Task.FromResult(arguments);
        }

        public async Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _executions);
            Entered.TrySetResult(true);
            try
            {
                var result = await _result.Task.WaitAsync(cancellationToken);
                return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, result)]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
        }
    }
}
