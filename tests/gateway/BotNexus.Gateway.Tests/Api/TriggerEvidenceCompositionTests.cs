using BotNexus.Cron;
using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Isolation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using CoreFixture = BotNexus.Gateway.Tests.Isolation.InProcessAgentHandleInterruptedRunEvidenceTests.Fixture;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// #4103: no host, credentials or profile stores. The supervisor only constructs the real
/// core/handle after the real trigger's write-ahead save; all cron/session reads reload SQLite.
/// Soul uses the production heartbeat fallback, NOT a fabricated AgentPromptAction Soul route.
/// </summary>
public sealed class TriggerEvidenceCompositionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scheduler_ToolInterrupted_PersistsCurrentRunEvidence(bool soul, bool operatorCancel)
    {
        await using var fixture = new Composition(soul);
        await fixture.Store.CreateAsync(fixture.Job);
        using var host = new CancellationTokenSource();
        var run = fixture.Scheduler.RunNowAsync(fixture.Job.Id, host.Token);
        var core = await fixture.Started.Task.WaitAsync(Deadline);
        await core.Tool.Entered.Task.WaitAsync(Deadline);
        if (operatorCancel)
        {
            (await fixture.Scheduler.CancelActiveRunAsync(fixture.Job.Id).WaitAsync(Deadline)).ShouldBe(1);
            (await run.WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.Aborted);
            host.IsCancellationRequested.ShouldBeFalse();
        }
        else
        {
            host.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(Deadline));
        }

        fixture.Action.Completion.ShouldNotBeNull().Status.ShouldBe("Cancelled");
        var persisted = await fixture.ReloadRunAsync();
        persisted.Status.ShouldBe(operatorCancel ? CronRunStatus.Aborted : CronRunStatus.Error);
        persisted.Status.ShouldNotBe(CronRunStatus.Ok);
        persisted.Status.ShouldNotBe(CronRunStatus.NoToolCalls);
        persisted.Error.ShouldNotBeNullOrWhiteSpace();
        persisted.CompletedAt.ShouldNotBeNull();
        persisted.Cost.ToolCallCount.ShouldBe(2, "historical-4101 must not leak into this run");
        persisted.Cost.TurnCount.ShouldBe(2, "the synthetic abort is not a model turn");
        persisted.Cost.PromptTokens.ShouldBe(28);
        persisted.Cost.CompletionTokens.ShouldBe(20);
        var session = await fixture.ReloadSessionAsync(persisted);
        var history = session.GetHistorySnapshot();
        history.Where(row => row.Role == MessageRole.User).ShouldHaveSingleItem().Trigger
            .ShouldBe(soul ? TriggerType.Soul : TriggerType.Cron);
        var results = history.Where(row => row.Kind == MessageKind.ToolResult).ToArray();
        // Starts are write-ahead execution order. Result insertion order is not execution order:
        // AgentEnd durably closes the in-flight call before the trigger projects completed results.
        history.Where(row => row.Kind == MessageKind.ToolStart).Select(row => row.ToolCallId)
            .ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        results.Length.ShouldBe(2);
        var completed = results.Where(row => row.ToolCallId == "completed-4101").ShouldHaveSingleItem();
        var interrupted = results.Where(row => row.ToolCallId == "interrupted-4101").ShouldHaveSingleItem();
        completed.Content.ShouldContain("harmless content");
        completed.ToolIsIncomplete.ShouldBeFalse();
        interrupted.ToolIsIncomplete.ShouldBeTrue();
        interrupted.ToolIsError.ShouldBeTrue();
        interrupted.ToolArgs.ShouldNotBeNull().ShouldContain("interrupted-4101.txt");
        results.ShouldAllBe(row => row.AgentRunId != null);
        results.Select(row => row.AgentRunId).Distinct().ShouldHaveSingleItem();
        history.ShouldNotContain(row => row.Role == MessageRole.Assistant);
        session.Status.ShouldBe(soul ? SessionStatus.Active : SessionStatus.Sealed);
        if (!soul)
            (await fixture.Conversations.GetAsync(session.ConversationId)).ShouldNotBeNull()
                .Status.ShouldBe(ConversationStatus.Active, "interruption cannot enter disposable no-op cleanup");
        fixture.Alerts.Verify(a => a.SendAsync(It.IsAny<ConversationId>(), It.IsAny<CronFailureAlert>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Inspect physical storage as well: normalized reads must not hide duplicate audit rows.
        await using var database = new SqliteConnection(fixture.SessionConnection);
        await database.OpenAsync();
        await using var query = database.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id=$session AND message_kind='tool-result'";
        query.Parameters.AddWithValue("$session", session.SessionId.Value);
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scheduler_CompletedZeroToolRun_PreservesReportingAndExecutionControls(bool soul, bool execution)
    {
        await using var fixture = new Composition(soul, textOnly: true);
        await fixture.Store.CreateAsync(fixture.Job with { ExecutionClass = execution });

        var returned = await fixture.Scheduler.RunNowAsync(fixture.Job.Id).WaitAsync(Deadline);

        var persisted = await fixture.ReloadRunAsync();
        var expected = execution ? CronRunStatus.NoToolCalls : CronRunStatus.Ok;
        returned.Status.ShouldBe(expected);
        persisted.Status.ShouldBe(expected);
        persisted.Cost.ToolCallCount.ShouldBe(0);
        persisted.Cost.TurnCount.ShouldBe(1);
        persisted.Cost.PromptTokens.ShouldBe(11);
        persisted.Cost.CompletionTokens.ShouldBe(7);
        var session = await fixture.ReloadSessionAsync(persisted);
        session.GetHistorySnapshot().Where(row => row.Role == MessageRole.Assistant)
            .ShouldHaveSingleItem().Content.ShouldBe("finished");
        fixture.Alerts.Verify(a => a.SendAsync(It.IsAny<ConversationId>(), It.IsAny<CronFailureAlert>(),
            It.IsAny<CancellationToken>()), execution ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scheduler_CancelAfterRealActionReturns_DoesNotRecordSuccess(bool soul, bool operatorCancel)
    {
        await using var fixture = new Composition(soul, textOnly: true, holdReturn: true);
        await fixture.Store.CreateAsync(fixture.Job);
        using var host = new CancellationTokenSource();
        var run = fixture.Scheduler.RunNowAsync(fixture.Job.Id, host.Token);
        await fixture.Action.Returned.Task.WaitAsync(Deadline);
        if (operatorCancel)
        {
            // Cancellation is signalled synchronously; the sweep then waits for our action to exit.
            var sweep = fixture.Scheduler.CancelActiveRunAsync(fixture.Job.Id);
            fixture.Action.Release.TrySetResult();
            (await sweep.WaitAsync(Deadline)).ShouldBe(1);
            (await run.WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.Aborted);
        }
        else
        {
            host.Cancel();
            fixture.Action.Release.TrySetResult();
            await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(Deadline));
        }

        var persisted = await fixture.ReloadRunAsync();
        persisted.Status.ShouldBe(operatorCancel ? CronRunStatus.Aborted : CronRunStatus.Error);
        persisted.Cost.ToolCallCount.ShouldBe(0);
        persisted.Cost.TurnCount.ShouldBe(1);
        persisted.Cost.PromptTokens.ShouldBe(11);
        (await fixture.ReloadSessionAsync(persisted)).GetHistorySnapshot()
            .ShouldContain(row => row.Role == MessageRole.Assistant && row.Content == "finished");
        fixture.Alerts.Verify(a => a.SendAsync(It.IsAny<ConversationId>(), It.IsAny<CronFailureAlert>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_CancelBeforeCoreAdmission_PersistsUnknownUsageNotZero(bool soul)
    {
        await using var fixture = new Composition(soul);
        await fixture.Store.CreateAsync(fixture.Job);
        using var host = new CancellationTokenSource();
        fixture.BeforePrompt = host.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            fixture.Scheduler.RunNowAsync(fixture.Job.Id, host.Token).WaitAsync(Deadline));

        var persisted = await fixture.ReloadRunAsync();
        persisted.Status.ShouldBe(CronRunStatus.Error);
        persisted.Cost.ToolCallCount.ShouldBe(0);
        persisted.Cost.TurnCount.ShouldBe(0);
        persisted.Cost.PromptTokens.ShouldBeNull();
        persisted.Cost.CompletionTokens.ShouldBeNull();
        var active = await fixture.Started.Task.WaitAsync(Deadline);
        active.Provider.Calls.ShouldBe(0);
        (await fixture.ReloadSessionAsync(persisted)).GetHistorySnapshot()
            .ShouldNotContain(row => row.Role == MessageRole.Tool || row.Role == MessageRole.Assistant);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Trigger_PartialUnmeasuredResponse_DoesNotInventUsage(bool soul, bool measured)
    {
        var partial = new AgentResponse
        {
            Content = string.Empty, TurnCount = 1,
            Completion = new RunCompletionSignal("Cancelled", [], "Cancellation", null, null, null, null, 0),
            RunUsage = measured ? new AgentResponseUsage(InputTokens: 9) : null,
            ToolCalls = [new("pending", "read", true, "{}", null, IsIncomplete: true)]
        };
        using var cancellation = new CancellationTokenSource();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
            {
                cancellation.Cancel();
                throw new AgentPromptInterruptedException(partial, ct);
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        IInternalTrigger trigger = soul
            ? new SoulTrigger(supervisor.Object, Mock.Of<IAgentRegistry>(), sessions, NullLogger<SoulTrigger>.Instance)
            : new CronTrigger(supervisor.Object, new InMemoryConversationStore(), sessions, NullLogger<CronTrigger>.Instance);
        var request = new InternalTriggerRequest { CronJobId = JobId.From("partial") };

        await Should.ThrowAsync<OperationCanceledException>(() => trigger.CreateSessionAsync(
            AgentId.From("partial"), "work", cancellation.Token, request));

        request.Completion.ShouldBe(partial.Completion);
        request.ToolInvocationCount.ShouldBe(1);
        request.TurnCount.ShouldBe(1);
        request.PromptTokens.ShouldBe(measured ? 9 : null);
        request.CompletionTokens.ShouldBeNull();
        (await sessions.ListAsync(AgentId.From("partial"))).ShouldHaveSingleItem().GetHistorySnapshot()
            .Where(row => row.ToolCallId == "pending").ShouldHaveSingleItem().ToolIsIncomplete.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    [InlineData("IncompleteWithoutStopReason")]
    [InlineData("Parked")]
    public async Task CronTrigger_NonCompletedEmptyReturn_RetainsTransientSession(string status)
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "NO_REPLY",
                Completion = new RunCompletionSignal(status, [], null, null, null, null, null, 0) });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        var trigger = new CronTrigger(supervisor.Object, conversations, sessions, NullLogger<CronTrigger>.Instance);
        var request = new InternalTriggerRequest { CronJobId = JobId.From("empty") };

        var id = await trigger.CreateSessionAsync(AgentId.From("empty"), "work", request: request);

        var retained = (await sessions.GetAsync(id)).ShouldNotBeNull();
        retained.Status.ShouldBe(SessionStatus.Sealed);
        retained.GetHistorySnapshot().ShouldContain(row => row.Role == MessageRole.User);
        (await conversations.GetAsync(retained.ConversationId)).ShouldNotBeNull().Status.ShouldBe(ConversationStatus.Active);
        request.Completion.ShouldNotBeNull().Status.ShouldBe(status);
    }

    [Fact]
    public async Task CronTrigger_RealCompletedSilentRun_StillDeletesDisposableSession()
    {
        await using var fixture = new Composition(soul: false, textOnly: true, silent: true);
        await fixture.Store.CreateAsync(fixture.Job with { ExecutionClass = false });

        (await fixture.Scheduler.RunNowAsync(fixture.Job.Id).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.Ok);

        var run = await fixture.ReloadRunAsync();
        run.Cost.ToolCallCount.ShouldBe(0);
        var id = run.SessionId.ShouldNotBeNull();
        (await fixture.Sessions.GetAsync(id)).ShouldBeNull();
        var core = await fixture.Started.Task.WaitAsync(Deadline);
        (await fixture.Conversations.GetAsync(core.Session.ConversationId)).ShouldNotBeNull()
            .Status.ShouldBe(ConversationStatus.Archived);
    }

    private sealed class Composition : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "trigger-evidence-4103", Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider services;
        private CoreFixture? core;
        public TaskCompletionSource<CoreFixture> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InMemoryConversationStore Conversations { get; } = new();
        public Mock<ICronFailureAlertSink> Alerts { get; } = new();
        public SqliteSessionStore Sessions { get; }
        public string SessionConnection { get; }
        public SqliteCronStore Store { get; }
        public CronScheduler Scheduler { get; }
        public ReturnBarrier Action { get; }
        public CronJob Job { get; }
        public Action? BeforePrompt { get; set; }

        public Composition(bool soul, bool textOnly = false, bool holdReturn = false, bool silent = false)
        {
            Directory.CreateDirectory(directory);
            SessionConnection = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            Sessions = new SqliteSessionStore(SessionConnection, NullLogger<SqliteSessionStore>.Instance, Conversations);
            Store = new SqliteCronStore(Path.Combine(directory, "cron.db"));
            var agent = AgentId.From("composition-4103");
            var registry = new Mock<IAgentRegistry>();
            registry.Setup(r => r.Get(agent)).Returns(new AgentDescriptor
            {
                AgentId = agent, DisplayName = "Composition", ApiProvider = "test-provider", ModelId = "test-model",
                Soul = new SoulAgentConfig { Enabled = soul }
            });
            var supervisor = new Mock<IAgentSupervisor>();
            supervisor.Setup(s => s.GetOrCreateAsync(agent, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns(async (AgentId _, SessionId id, CancellationToken ct) =>
                {
                    core = new CoreFixture(session: (await Sessions.GetAsync(id, ct)).ShouldNotBeNull(), sessionStore: Sessions);
                    core.Provider.TextOnly = textOnly;
                    if (silent) core.Provider.Text = "NO_REPLY";
                    Started.TrySetResult(core);
                    BeforePrompt?.Invoke();
                    return core.Handle;
                });
            var collection = new ServiceCollection();
            collection.AddSingleton(registry.Object).AddSingleton(supervisor.Object);
            collection.AddSingleton<ISessionStore>(Sessions).AddSingleton<IConversationStore>(Conversations);
            collection.AddSingleton(Alerts.Object);
            collection.AddSingleton<IInternalTrigger>(new CronTrigger(supervisor.Object, Conversations, Sessions, NullLogger<CronTrigger>.Instance));
            collection.AddSingleton<IInternalTrigger>(new SoulTrigger(supervisor.Object, registry.Object, Sessions, NullLogger<SoulTrigger>.Instance));
            services = collection.BuildServiceProvider();
            Action = new ReturnBarrier(soul ? new HeartbeatAction() : new AgentPromptAction(), holdReturn);
            Scheduler = new CronScheduler(Store, [Action], services.GetRequiredService<IServiceScopeFactory>(),
                new Monitor(), NullLogger<CronScheduler>.Instance);
            Job = new CronJob
            {
                Id = JobId.From("composition-4103"), Name = "Composition", Schedule = "* * * * *",
                AgentId = agent, ActionType = Action.ActionType, Message = "current run", ExecutionClass = true,
                FailureAlertsEnabled = true, FailureAlertConversationId = ConversationId.From("alerts-4103")
            };
        }

        public async Task<CronRun> ReloadRunAsync()
            => (await new SqliteCronStore(Path.Combine(directory, "cron.db")).GetRunHistoryAsync(Job.Id)).ShouldHaveSingleItem();
        public async Task<GatewaySession> ReloadSessionAsync(CronRun run)
            => (await new SqliteSessionStore(SessionConnection, NullLogger<SqliteSessionStore>.Instance, Conversations)
                .GetAsync(run.SessionId.ShouldNotBeNull())).ShouldNotBeNull();
        public async ValueTask DisposeAsync()
        {
            Action.Release.TrySetResult();
            if (core is not null) await core.DisposeAsync();
            Scheduler.Dispose();
            await services.DisposeAsync();
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "cron.db")}"))
                SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Monitor : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = new();
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }

    // A deterministic seam AFTER the real action/core returned. It returns normally even when
    // cancelled, exercising reconciliation of cancellation intent with an ordinary action return.
    private sealed class ReturnBarrier(ICronAction inner, bool hold) : ICronAction
    {
        public string ActionType => inner.ActionType;
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RunCompletionSignal? Completion { get; private set; }
        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.ExecuteAsync(context, cancellationToken);
            }
            finally
            {
                Completion = context.RunCompletion;
            }
            Returned.TrySetResult();
            if (hold) await Release.Task;
        }
    }
}
