using System.Collections;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.IntegrationMock.Tests;

public class IntegrationMockCancellationTests
{
    private const string Key = "CANCELLATION_REGRESSION";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Stream_PreCanceled_DoesNotPrepareOrEnumerateScript()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var script = new BoundaryScript([new("text_delta", Delta: "must not emit"), new("done")]);
        var model = Model();

        var (events, result) = await CollectAsync(Provider(script).Stream(model, Context(),
            new StreamOptions { CancellationToken = cancellation.Token }));

        // Drain proves quiescence: no provider work can later cross this boundary.
        events.Count.ShouldBe(1);
        script.EnumerationCount.ShouldBe(0);
        script.MoveNextCount.ShouldBe(0);
        AssertTerminalError(events, result, model, StopReason.Aborted);
    }

    [Fact]
    public async Task Stream_PreCanceledMalformedCatalog_AbortsBeforeCatalogResolution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mock-cancel-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ this is deliberately invalid JSON");
            var model = Model(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var (events, result) = await CollectAsync(new IntegrationMockProvider().Stream(model, Context(),
                new StreamOptions { CancellationToken = cancellation.Token }));

            // Independently establish that resolving these same bytes really fails. A new
            // loader avoids cache effects; Aborted must therefore win BEFORE file parsing.
            var (faultEvents, faultResult) = await CollectAsync(new IntegrationMockProvider().Stream(model, Context()));
            faultEvents.Count.ShouldBe(1);
            AssertTerminalError(faultEvents, faultResult, model, StopReason.Error);
            faultResult.ErrorMessage.ShouldNotBeNullOrWhiteSpace();

            events.Count.ShouldBe(1);
            AssertTerminalError(events, result, model, StopReason.Aborted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Stream_CanceledAtFirstScriptBoundary_CompletesWithSingleAbortedError()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new BoundaryScript([new("text_delta", Delta: "must not emit"), new("done")],
            boundaryIndex: 0, onBoundary: cancellation.Cancel);
        var model = Model();

        var (events, result) = await CollectAsync(Provider(script).Stream(model, Context(),
            new StreamOptions { CancellationToken = cancellation.Token }));

        script.BoundaryReached.ShouldBeTrue();
        script.Disposed.ShouldBeTrue();
        events.Select(e => e.Type).ShouldBe(new[] { "start", "error" });
        AssertTerminalError(events, result, model, StopReason.Aborted);
    }

    [Fact]
    public async Task Stream_CanceledAfterTextDelta_PreservesAlreadyEmittedPartialEvents()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new BoundaryScript([
            new("text_delta", Delta: "partial"),
            new("text_delta", Delta: "must not emit"),
            new("done")], boundaryIndex: 1, onBoundary: cancellation.Cancel);
        var model = Model();

        var (events, result) = await CollectAsync(Provider(script).Stream(model, Context(),
            new StreamOptions { CancellationToken = cancellation.Token }));

        script.BoundaryReached.ShouldBeTrue();
        script.Disposed.ShouldBeTrue();
        events.Select(e => e.Type).ShouldBe(new[] { "start", "text_start", "text_delta", "error" });
        var delta = events.OfType<TextDeltaEvent>().ShouldHaveSingleItem();
        delta.Delta.ShouldBe("partial");
        delta.Partial.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("partial");
        delta.Partial.ModelId.ShouldBe(model.Id);
        delta.Partial.Api.ShouldBe(model.Api);
        delta.Partial.Provider.ShouldBe(model.Provider);
        AssertTerminalError(events, result, model, StopReason.Aborted);
    }

    [Theory]
    [InlineData("unknown_step", "Unknown scripted step type")]
    [InlineData("tool_call", "tool_call step requires toolName")]
    [InlineData("error", "scripted failure")]
    public async Task Stream_GenuineScriptFailure_RemainsError(string type, string expectedError)
    {
        var model = Model();
        var script = new BoundaryScript([new(type, ErrorMessage: "scripted failure")]);

        var (events, result) = await CollectAsync(Provider(script).Stream(model, Context()));

        script.Disposed.ShouldBeTrue();
        AssertTerminalError(events, result, model, StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain(expectedError);
    }

    [Fact]
    public async Task Stream_EnumeratorCancellationExceptionWithoutRequestedToken_RemainsError()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new BoundaryScript([new("done")], boundaryIndex: 0,
            onBoundary: () => throw new OperationCanceledException("not requested", cancellation.Token));
        var model = Model();

        var (events, result) = await CollectAsync(Provider(script).Stream(model, Context(),
            new StreamOptions { CancellationToken = cancellation.Token }));

        cancellation.IsCancellationRequested.ShouldBeFalse();
        script.BoundaryReached.ShouldBeTrue();
        AssertTerminalError(events, result, model, StopReason.Error);
        result.ErrorMessage.ShouldBe("not requested");
    }

    [Fact]
    public async Task Stream_UncanceledControlledScript_NormalSuccessIsUnchanged()
    {
        var script = new BoundaryScript([new("text_delta", Delta: "success"), new("text_end"), new("done")]);
        var model = Model();

        var (events, result) = await CollectAsync(Provider(script).Stream(model, Context()));

        script.Disposed.ShouldBeTrue();
        events.Select(e => e.Type).ShouldBe(new[] { "start", "text_start", "text_delta", "text_end", "done" });
        events.OfType<ErrorEvent>().ShouldBeEmpty();
        var done = events.OfType<DoneEvent>().ShouldHaveSingleItem();
        done.Reason.ShouldBe(StopReason.Stop);
        result.ShouldBeSameAs(done.Message);
        result.StopReason.ShouldBe(StopReason.Stop);
        result.ModelId.ShouldBe(model.Id);
        result.Api.ShouldBe(model.Api);
        result.Provider.ShouldBe(model.Provider);
        result.ErrorMessage.ShouldBeNull();
        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("success");
    }

    private static IntegrationMockProvider Provider(IReadOnlyList<ScriptedResponseStep> script)
        => new(new MockCatalogLoader(new MockCatalog(new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            [Key] = script
        })));

    private static Context Context() => new(null, [new UserMessage(new UserMessageContent(Key), 0L)]);

    private static LlmModel Model(string baseUrl = "") => new(
        Id: "cancellation-regression-model", Name: "Cancellation regression", Api: IntegrationMockModels.ApiName,
        Provider: "cancellation-regression-provider", BaseUrl: baseUrl, Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 128000, MaxTokens: 32000);

    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Result)> CollectAsync(LlmStream stream)
    {
        // Consumer uses no request token: it must observe the provider's terminal event,
        // not cancel its own enumeration and conceal an incomplete provider stream.
        var events = await DrainAsync(stream).WaitAsync(Deadline);
        var result = await stream.GetResultAsync().WaitAsync(Deadline);
        stream.GetResultAsync().IsCompletedSuccessfully.ShouldBeTrue();
        return (events, result);
    }

    private static async Task<List<AssistantMessageEvent>> DrainAsync(LlmStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        return events;
    }

    private static void AssertTerminalError(List<AssistantMessageEvent> events, AssistantMessage result,
        LlmModel model, StopReason reason)
    {
        var error = events.OfType<ErrorEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(error);
        events.OfType<DoneEvent>().ShouldBeEmpty();
        result.ShouldBeSameAs(error.Error);
        result.ModelId.ShouldBe(model.Id);
        result.Api.ShouldBe(model.Api);
        result.Provider.ShouldBe(model.Provider);
        error.Reason.ShouldBe(reason);
        result.StopReason.ShouldBe(reason);
    }

    // The actual provider enumerates the injected IReadOnlyList. MoveNext runs the
    // boundary action BEFORE yielding the selected step, so cancellation is ordered
    // precisely before the provider's ct.ThrowIfCancellationRequested(), without sleeps.
    private sealed class BoundaryScript(IReadOnlyList<ScriptedResponseStep> steps,
        int boundaryIndex = -1, Action? onBoundary = null) : IReadOnlyList<ScriptedResponseStep>
    {
        public int Count => steps.Count;
        public ScriptedResponseStep this[int index] => steps[index];
        public int EnumerationCount { get; private set; }
        public int MoveNextCount { get; private set; }
        public bool BoundaryReached { get; private set; }
        public bool Disposed { get; private set; }

        public IEnumerator<ScriptedResponseStep> GetEnumerator()
        {
            EnumerationCount++;
            return Enumerate().GetEnumerator();
        }

        private IEnumerable<ScriptedResponseStep> Enumerate()
        {
            try
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    MoveNextCount++;
                    if (i == boundaryIndex)
                    {
                        BoundaryReached = true;
                        onBoundary?.Invoke();
                    }
                    yield return steps[i];
                }
            }
            finally
            {
                Disposed = true;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
