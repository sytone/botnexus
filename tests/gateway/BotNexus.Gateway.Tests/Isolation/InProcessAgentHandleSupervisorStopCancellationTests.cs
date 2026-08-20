using System.Threading.Channels;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Issue #3384 - the gateway's own abandoned-turn recovery (#790) destroys a live handle through
/// <c>IAgentSupervisor.StopAsync</c>, which lands on <see cref="InProcessAgentHandle.DisposeAsync"/>.
/// That teardown originates OUTSIDE the streaming call, so neither of the two cancellation sources
/// the #3230 guard consulted was ever signalled: an orderly recovery surfaced as two <c>[ERR]</c>
/// records and a synthetic <c>Internal streaming error</c> pushed at the client.
/// </summary>
/// <remarks>
/// <para>
/// The two halves of this file are deliberately independent and must stay that way:
/// </para>
/// <list type="bullet">
/// <item><b>AC1/AC2</b> - a deliberate supervisor stop is control flow: Debug, no client error
/// event, unfaulted channel.</item>
/// <item><b>AC3</b> - a cancellation raised while NO source is signalled is still a genuine fault:
/// Error, error event, faulted channel. This is the non-vacuity pin. If the fix were an
/// over-broadened predicate (or a blanket cancellation swallow) this test reddens, which is exactly
/// what makes the AC1 assertion mean something.</item>
/// </list>
/// <para>
/// Mutation evidence (#3384 AC5): deleting the <c>|| IsDeliberatelyStopped</c> term from
/// <c>InProcessAgentHandle.IsDeliberateTeardown</c> reddens
/// <see cref="SupervisorStop_MakesStreamCancellationDeliberate"/> and the two write-path tests here,
/// while <see cref="Cancellation_WithNoSourceSignalled_StillFaults"/> stays green - the two are
/// independently load-bearing.
/// </para>
/// </remarks>
public sealed class InProcessAgentHandleSupervisorStopCancellationTests
{
    private static readonly AgentId TestAgentId = AgentId.From("agent-3384");
    private static readonly SessionId TestSessionId = SessionId.From("session-3384");

    private static AgentStreamEvent? MapToContentDelta(AgentEvent _, string messageId)
        => new() { Type = AgentStreamEventType.ContentDelta, ContentDelta = "x", MessageId = messageId };

    [Fact]
    public async Task SupervisorStop_MakesStreamCancellationDeliberate()
    {
        // AC1 core: before the stop, NOTHING is signalled - a cancellation now would be a genuine
        // fault. After the supervisor destroys the handle, the same predicate reports deliberate
        // teardown, with the caller's own token still untouched throughout.
        var handle = CreateHandle();
        using var promptCancellation = new CancellationTokenSource();
        var callerToken = CancellationToken.None;

        handle.IsDeliberatelyStopped.ShouldBeFalse("nothing has torn the handle down yet");
        handle.IsDeliberateTeardown(promptCancellation, callerToken).ShouldBeFalse(
            "with no source signalled, a cancellation must still classify as a fault");

        // This is precisely what DefaultAgentSupervisor.StopAsync does to destroy a stale handle.
        await handle.DisposeAsync();

        handle.IsDeliberatelyStopped.ShouldBeTrue("a supervisor stop is a deliberate teardown");
        handle.IsDeliberateTeardown(promptCancellation, callerToken).ShouldBeTrue(
            "the supervisor stop is the THIRD cancellation source and must trip the same seam");

        promptCancellation.IsCancellationRequested.ShouldBeFalse(
            "the fix must not work by cancelling the caller's turn - that would change turn semantics");
        callerToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task SupervisorStop_LogsCancellationAtDebugNotError()
    {
        // AC1: the observed 18:02:30.546 [ERR] record must become a Debug record.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var handle = CreateHandle();
        using var promptCancellation = new CancellationTokenSource();
        await handle.DisposeAsync();

        // The WriteAsync token is the AGENT's event token, which the abort trips - reproducing the
        // observed TaskCanceledException from InProcessAgentHandle.WriteAgentEventAsync exactly.
        using var eventCts = new CancellationTokenSource();
        await eventCts.CancelAsync();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-3384-1",
            channel.Writer,
            MapToContentDelta,
            eventCts.Token,
            () => handle.IsDeliberateTeardown(promptCancellation, CancellationToken.None),
            logger,
            TestAgentId,
            TestSessionId);

        logger.Records.ShouldNotContain(
            r => r.Level == LogLevel.Error,
            "a supervisor-initiated teardown is orderly recovery, not a fault; got: "
            + string.Join(" | ", logger.Records.Select(r => $"{r.Level}:{r.Message}")));

        var debug = logger.Records.SingleOrDefault(r => r.Level == LogLevel.Debug);
        debug.ShouldNotBeNull("the cancellation must still be observable at Debug");
        debug!.Message.ShouldContain("cancelled");
        debug.Message.ShouldContain("agent-3384");
    }

    [Fact]
    public async Task SupervisorStop_PushesNoErrorEventAndDoesNotFaultChannel()
    {
        // AC2: no user-visible "Internal streaming error" at the exact moment the platform is
        // successfully recovering, and the channel stays unfaulted.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var handle = CreateHandle();
        using var promptCancellation = new CancellationTokenSource();
        await handle.DisposeAsync();

        using var eventCts = new CancellationTokenSource();
        await eventCts.CancelAsync();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-3384-2",
            channel.Writer,
            MapToContentDelta,
            eventCts.Token,
            () => handle.IsDeliberateTeardown(promptCancellation, CancellationToken.None),
            logger,
            TestAgentId,
            TestSessionId);

        channel.Reader.TryRead(out var written).ShouldBeFalse(
            $"no event may reach the client on a deliberate supervisor stop, but got: {written?.Type} / {written?.ErrorMessage}");

        channel.Writer.TryComplete().ShouldBeTrue("the channel must not have been faulted by the recovery");
        await channel.Reader.Completion.ShouldNotThrowAsync(
            "a faulted channel is what made the abandoned-turn recovery look like an error to the client");
    }

    [Fact]
    public async Task Cancellation_WithNoSourceSignalled_StillFaults()
    {
        // AC3 - NON-VACUITY. The whole point of #3384 is that the fix narrows, never blankets. With
        // no caller token, no prompt cancellation and NO supervisor stop, a cancellation-typed
        // exception is a genuine fault and keeps every part of the Error path.
        //
        // Over-broaden the predicate (e.g. `=> true`, or catching OperationCanceledException by
        // type) and this test reddens while the tests above stay green.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var handle = CreateHandle();
        using var promptCancellation = new CancellationTokenSource();

        handle.IsDeliberateTeardown(promptCancellation, CancellationToken.None).ShouldBeFalse(
            "no teardown source is signalled, so this must classify as a fault");

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-3384-3",
            channel.Writer,
            (_, _) => throw new TaskCanceledException("unsignalled-3384"),
            CancellationToken.None,
            () => handle.IsDeliberateTeardown(promptCancellation, CancellationToken.None),
            logger,
            TestAgentId,
            TestSessionId);

        logger.Records.ShouldContain(
            r => r.Level == LogLevel.Error,
            "a cancellation with NO source signalled is a real fault and must not be downgraded; got: "
            + string.Join(" | ", logger.Records.Select(r => $"{r.Level}:{r.Message}")));

        channel.Reader.TryRead(out var written).ShouldBeTrue("the client must still be told about a genuine fault");
        written!.Type.ShouldBe(AgentStreamEventType.Error);
        written.ErrorMessage!.ShouldContain("Internal streaming error");

        await Should.ThrowAsync<TaskCanceledException>(async () => await channel.Reader.Completion);

        await handle.DisposeAsync();
    }

    [Fact]
    public async Task ArchivePathClassification_IsUnchangedByTheSupervisorTerm()
    {
        // AC4: #3230's archive/disconnect path still classifies as deliberate teardown on the
        // caller's token alone, with no supervisor stop involved.
        var handle = CreateHandle();
        using var promptCancellation = new CancellationTokenSource();
        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        handle.IsDeliberatelyStopped.ShouldBeFalse("no supervisor stop in the archive scenario");
        handle.IsDeliberateTeardown(promptCancellation, callerCts.Token).ShouldBeTrue(
            "#3230 behaviour must be unchanged: the caller's cancelled token alone still means teardown");

        // And the linked prompt source alone, which the stream's own finally cancels.
        await promptCancellation.CancelAsync();
        handle.IsDeliberateTeardown(promptCancellation, CancellationToken.None).ShouldBeTrue();

        await handle.DisposeAsync();
    }

    private static InProcessAgentHandle CreateHandle()
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var providers = new ApiProviderRegistry();
        providers.Register(new StubStreamingProvider());
        var llmClient = new LlmClient(providers, modelRegistry);
        var model = modelRegistry.GetModel("test-provider", "test-model")!;
        var options = new AgentOptions(
            InitialState: new AgentInitialState(SystemPrompt: "test", Model: model),
            Model: model,
            LlmClient: llmClient,
            ConvertToLlm: null,
            TransformContext: null,
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Parallel,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            SteeringMode: QueueMode.All,
            FollowUpMode: QueueMode.All,
            SessionId: "session-3384");

        var agent = new BotNexus.Agent.Core.Agent(options);
        return new InProcessAgentHandle(agent, TestAgentId, TestSessionId, NullLogger.Instance);
    }

    private sealed class StubStreamingProvider : IApiProvider
    {
        public string Api => "test-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context, null);

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var stream = new LlmStream();
            var partial = new AssistantMessage(
                Content: [],
                Api: model.Api,
                Provider: model.Provider,
                ModelId: model.Id,
                Usage: Usage.Empty(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var withText = partial with { Content = [new TextContent("ok")] };
            stream.Push(new StartEvent(partial));
            stream.Push(new TextDeltaEvent(0, "ok", withText));
            stream.Push(new DoneEvent(StopReason.Stop, withText));
            return stream;
        }
    }
}
