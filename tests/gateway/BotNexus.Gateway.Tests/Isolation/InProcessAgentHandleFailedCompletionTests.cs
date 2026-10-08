using System.Threading.Channels;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Isolation;

public sealed class InProcessAgentHandleFailedCompletionTests
{
    private const string Detail = "Required proactive compaction did not apply (reason=NoSummarizableTurns).";

    [Fact]
    public void MapRunError_FailedCompletion_PreservesPreciseDetailAndMessageId()
    {
        var error = InProcessAgentHandle.MapRunError(FailedEnd(), "message-4736", errorAlreadyEmitted: false);

        error.ShouldNotBeNull();
        error.Type.ShouldBe(AgentStreamEventType.Error);
        error.ErrorMessage.ShouldBe(Detail);
        error.MessageId.ShouldBe("message-4736");
    }

    [Fact]
    public void MapRunError_FailedCompletionWithoutDetail_StillExplainsFailure()
    {
        var end = new AgentEndEvent([], null, DateTimeOffset.UtcNow,
            new RunCompletionResult(RunCompletionStatus.Failed, []));

        var error = InProcessAgentHandle.MapRunError(end, "message-4736", errorAlreadyEmitted: false);

        error.ShouldNotBeNull();
        error.ErrorMessage.ShouldBe("The agent run failed but supplied no detail.");
    }

    [Theory]
    [InlineData(RunCompletionStatus.Cancelled)]
    [InlineData(RunCompletionStatus.Completed)]
    public async Task WriteAgentEventAsync_NonFailedCompletion_EmitsOnlyRunEnded(RunCompletionStatus status)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var end = new AgentEndEvent([], null, DateTimeOffset.UtcNow,
            new RunCompletionResult(status, [], Detail: "not a failure"));

        await WriteAsync(end, channel.Writer);
        channel.Writer.TryComplete();
        var events = await DrainAsync(channel.Reader);

        events.ShouldHaveSingleItem().Type.ShouldBe(AgentStreamEventType.RunEnded);
        var completion = events[0].Completion.ShouldNotBeNull();
        completion.Status.ShouldBe(status.ToString());
    }

    [Fact]
    public async Task WriteAgentEventAsync_PriorProviderError_DoesNotDuplicateErrorAtRunEnd()
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var message = new AssistantAgentMessage("partial", FinishReason: StopReason.Error,
            ErrorMessage: "provider rejected model");
        var emitted = await WriteAsync(new TurnEndEvent(message, [], DateTimeOffset.UtcNow), channel.Writer);
        emitted.ShouldBeTrue();
        await WriteAsync(new AgentEndEvent([message], null, DateTimeOffset.UtcNow,
            new RunCompletionResult(RunCompletionStatus.Failed, [], Detail: message.ErrorMessage)),
            channel.Writer, emitted);
        channel.Writer.TryComplete();
        var events = await DrainAsync(channel.Reader);

        events.Select(value => value.Type).ShouldBe([
            AgentStreamEventType.TurnEnd, AgentStreamEventType.Error, AgentStreamEventType.RunEnded]);
        events.Single(value => value.Type == AgentStreamEventType.Error).ErrorMessage.ShouldBe(message.ErrorMessage);
    }

    [Fact]
    public async Task StreamAsync_RequiredCompactionFailure_PersistsAndPublishesPreciseFailedOutcome()
    {
        var provider = new CountingProvider();
        var model = new LlmModel("failed-compaction", "Failed compaction", provider.Api, "test-provider",
            "https://example.com", false, ["text"], new ModelCost(0, 0, 0, 0), 128_000, 1024);
        var models = new ModelRegistry();
        models.Register(model.Provider, model);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var agent = new BotNexus.Agent.Core.Agent(new AgentOptions(
            InitialState: new AgentInitialState(SystemPrompt: "test", Model: model),
            Model: model,
            LlmClient: new LlmClient(providers, models),
            ProviderMessageTransformer: null,
            AgentContextTransformer: null,
            ProviderExecutionOptionsProvider: (_, _) => Task.FromResult<ProviderExecutionOptions?>(null),
            SteeringMessageProvider: null,
            FollowUpMessageProvider: null,
            ToolExecutionMode: ToolExecutionMode.Parallel,
            ToolExecutionPolicy: null,
            ToolResultTransformer: null,
            GenerationSettings: new GenerationOptions(),
            SteeringMode: QueueMode.All,
            FollowUpMode: QueueMode.All,
            ContextCompactionService: _ => Task.FromException<AgentContext?>(
                new ProactiveCompactionException(Detail, retryable: false))));
        await using var handle = new InProcessAgentHandle(agent, AgentId.From("agent-4736"),
            SessionId.From("session-4736"), NullLogger.Instance);
        var session = new GatewaySession { AgentId = handle.AgentId, SessionId = handle.SessionId };
        var store = new Mock<ISessionStore>();
        IReadOnlyList<SessionEntry> savedHistory = [];
        RunCompletionSignal? savedCompletion = null;
        store.Setup(value => value.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((saved, _) =>
            {
                savedHistory = saved.GetHistorySnapshot();
                savedCompletion = saved.RunCompletion;
            })
            .Returns(Task.CompletedTask);
        var published = new List<AgentStreamEvent>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(handle.StreamAsync("oversized context"),
            session, store.Object, new StreamingSessionOptions(IncludeErrorsInHistory: true,
                OnEventAsync: (evt, _) =>
                {
                    published.Add(evt);
                    return ValueTask.CompletedTask;
                }));

        provider.Calls.ShouldBe(0, "required compaction failure must remain fail-closed");
        var error = published.Single(value => value.Type == AgentStreamEventType.Error);
        error.ErrorMessage.ShouldBe(Detail);
        var terminal = published.Single(value => value.Type == AgentStreamEventType.RunEnded);
        published.IndexOf(error).ShouldBeLessThan(published.IndexOf(terminal));
        error.MessageId.ShouldBe(terminal.MessageId);
        result.Completion.ShouldNotBeNull();
        result.Completion.Status.ShouldBe("Failed");
        result.Completion.Detail.ShouldBe(Detail);
        terminal.Completion.ShouldBe(result.Completion);
        savedCompletion.ShouldBe(result.Completion);
        var row = savedHistory.ShouldHaveSingleItem();
        row.Role.ShouldBe(MessageRole.System);
        row.Content.ShouldBe($"Agent stream error: {Detail}");
        session.GetHistorySnapshot().ShouldBe(savedHistory);
        result.HistoryEntries.ShouldHaveSingleItem().Content.ShouldBe(row.Content);
        result.AssistantContent.ShouldBeEmpty();
        result.FinalSaveOutcome.ShouldBe(SessionSaveOutcome.Persisted);
        store.Verify(value => value.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AgentEndEvent FailedEnd() => new([], null, DateTimeOffset.UtcNow,
        new RunCompletionResult(RunCompletionStatus.Failed, [], Detail: Detail));

    private static Task<bool> WriteAsync(AgentEvent evt, ChannelWriter<AgentStreamEvent> writer,
        bool errorAlreadyEmitted = false) => InProcessAgentHandle.WriteAgentEventAsync(evt, "message-4736", writer,
        InProcessAgentHandle.MapAgentEvent, CancellationToken.None, () => false, NullLogger.Instance,
        AgentId.From("agent-4736"), SessionId.From("session-4736"), errorAlreadyEmitted);

    private static async Task<List<AgentStreamEvent>> DrainAsync(ChannelReader<AgentStreamEvent> reader)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);
        return events;
    }

    private sealed class CountingProvider : IApiProvider
    {
        public string Api => "failed-compaction-api";
        public int Calls { get; private set; }
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context);
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            Calls++;
            throw new InvalidOperationException("Provider must not be invoked after required compaction failure.");
        }
    }
}
