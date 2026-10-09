using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Core half of #4793: an awaited child-shaped result resumes the existing parent tool loop.
/// This fake models only the IAgentTool boundary; Gateway manager receipt/wakeup tests own
/// the separate guarantee that completing a real child does not dispatch another parent run.
/// </summary>
public sealed class AwaitedToolParentLoopTests
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
        var run = agent.PromptAsync("await child", cancellation.Token);
        try
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
        }
        finally
        {
            cancellation.Cancel();
            tool.Complete("cleanup");
            _ = await run.WaitAsync(DiagnosticDeadline);
        }
    }

    private sealed class AwaitedResultTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executions;

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions => Volatile.Read(ref _executions);
        public string Name => "await_child_result";
        public string Label => "Await child result";
        public Tool Definition => new(Name, "Signal-driven awaited result test seam", Schema);
        public void Complete(string result) => _result.TrySetResult(result);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(arguments);

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
