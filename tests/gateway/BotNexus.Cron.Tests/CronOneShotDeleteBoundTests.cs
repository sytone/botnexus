using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Concurrency;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3517: a one-shot job whose deletion fails must reach a terminal state instead of retrying the
/// identical failure forever.
/// </summary>
/// <remarks>
/// <para>
/// The reported incident is a two-part interaction, and both parts are covered here because either
/// alone leaves the loop intact:
/// </para>
/// <list type="number">
///   <item><b>Ordering.</b> #3160 made <c>DeleteJobAsync</c> cancel the in-flight run before
///   archiving, but the wait for that cancellation is a GRACE period that can elapse unobserved.
///   When it does, the run is still executing and still holding the conversation's write stripe, so
///   the archive is not merely racy - it cannot succeed. Attempting it anyway and aborting the whole
///   delete on the guaranteed failure is what re-armed the loop.</item>
///   <item><b>Boundedness.</b> <c>MaybeDeleteOneShotJobAsync</c> swallowed every failure and let the
///   next scheduled run try again, with no counter and no ceiling.</item>
/// </list>
/// <para>
/// Every interleaving is forced with <see cref="TaskCompletionSource"/> gates, never a delay: a
/// timing race would make the losing side of this race probabilistic, which is precisely the
/// property that let the defect ship.
/// </para>
/// </remarks>
public sealed class CronOneShotDeleteBoundTests
{
    // ── AC1: the retry is bounded and the job reaches a terminal state ───────────────────

    [Fact]
    public async Task OneShotDeletion_ThatKeepsFailing_StopsRetryingAndDisablesTheJob()
    {
        // AC1 verbatim, and AC4's non-vacuity target. The conversation store fails EVERY time, which
        // is exactly the production shape (a stripe held by a wedged run does not free itself). Under
        // the pre-fix code the job survives every run with `enabled: true` and tries again forever.
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = ArchiveRecorder.AlwaysFailing();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ConversationId = ConversationId.From("conv-wedged")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], conversationStore: conversations.Store);

        // Run it well past the bound. A bounded policy stops attempting; an unbounded one does not.
        for (var i = 0; i < CronScheduler.MaxOneShotDeleteAttempts + 4; i++)
            await scheduler.RunNowAsync(JobId.From("job-1"));

        conversations.ArchiveAttempts.ShouldBe(
            CronScheduler.MaxOneShotDeleteAttempts,
            "deletion must stop being attempted once the bound is reached, not retry on every subsequent run");

        var terminal = await context.Store.GetAsync(JobId.From("job-1"));
        terminal.ShouldNotBeNull("the job is disabled rather than dropped, so the evidence survives for an operator");
        terminal!.Enabled.ShouldBeFalse("an undeletable one-shot job must stop firing");
        terminal.NextRunAt.ShouldBeNull("a disabled terminal job must not be scheduled to fire again");
    }

    [Fact]
    public async Task OneShotDeletion_RetriesUpToTheBound_BeforeGivingUp()
    {
        // Containment on the bound itself: it must not collapse to "one attempt". A single transient
        // failure has to remain recoverable, otherwise the fix trades an unbounded loop for jobs that
        // strand themselves on the first hiccup.
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = ArchiveRecorder.FailNTimes(CronScheduler.MaxOneShotDeleteAttempts - 1);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ConversationId = ConversationId.From("conv-transient")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], conversationStore: conversations.Store);

        for (var i = 0; i < CronScheduler.MaxOneShotDeleteAttempts; i++)
            await scheduler.RunNowAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1")))
            .ShouldBeNull("a transient archive failure inside the bound must still end in a successful delete");
    }

    [Fact]
    public async Task ASuccessfulOneShotDeletion_IsUnaffectedByTheBound()
    {
        // Behaviour parity for the overwhelmingly common path: nothing fails, the job is deleted on
        // the first run exactly as #2634 specified.
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ConversationId = ConversationId.From("conv-ok")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")]);
        await scheduler.RunNowAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    // ── AC3: an unobserved cancellation must NOT proceed into the archive ────────────────

    [Fact]
    public async Task DeleteJob_DoesNotAttemptTheArchive_WhenCancellationWasNeverObserved()
    {
        // AC3. The action ignores its token entirely, so the grace window elapses unobserved and the
        // run is provably still executing when the delete continues. In production that run is what
        // holds the conversation stripe; attempting the archive there is a guaranteed failure whose
        // rethrow aborted the delete. Asserting on the archive call COUNT (an observable on the
        // store) rather than on a log line is the point.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new UncooperativeAction("test-action");
        var conversations = new ArchiveRecorder();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From("conv-held")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(
            context.Store,
            [action],
            conversationStore: conversations.Store,
            // Zero grace: no opportunity to observe, deterministically, with no delay anywhere.
            options: ShortGraceOptions());

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.ArchiveAttempts.ShouldBe(
            0,
            "the run never observed cancellation, so it still holds the conversation - the archive must be skipped, not attempted and rethrown");

        // Fail-open is preserved: the operator's removal still wins.
        (await context.Store.GetAsync(JobId.From("job-1")))
            .ShouldBeNull("skipping the archive must not block the delete itself");

        action.Release();
        await runTask;
    }

    [Fact]
    public async Task DeleteJob_StillArchives_WhenCancellationWasObserved()
    {
        // Non-vacuity for the clause above: the skip must be conditional on the FAILURE to observe.
        // A change that simply stopped archiving would pass the previous test and silently strand
        // every cron conversation the platform has.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        var conversations = new ArchiveRecorder();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From("conv-cooperative")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [action], conversationStore: conversations.Store);

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.ArchiveAttempts.ShouldBe(1, "a run that observed its cancellation releases the conversation, so the archive proceeds");
        conversations.Archived.ShouldBe(["conv-cooperative"]);

        await runTask;
    }

    [Fact]
    public async Task DeleteJob_WithNoActiveRun_StillArchives()
    {
        // The "never observed" branch keys on runs that were SIGNALLED. An idle job signalled none,
        // which must count as observed - otherwise a routine manual delete would stop archiving.
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = new ArchiveRecorder();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From("conv-idle")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], conversationStore: conversations.Store);
        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.Archived.ShouldBe(["conv-idle"]);
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    // ── The exception the archive path now surfaces ──────────────────────────────────────

    [Fact]
    public async Task AStripeLockTimeout_CountsTowardTheBound_LikeAnyOtherArchiveFailure()
    {
        // The production failure is specifically a contended stripe. The bound must treat it as a
        // failure like any other - a policy that only counted "some" failures would leave the exact
        // reported signature unbounded.
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = ArchiveRecorder.AlwaysFailing(
            id => new StripeLockTimeoutException(id, TimeSpan.FromSeconds(30)));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ConversationId = ConversationId.From("conv-wedged")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], conversationStore: conversations.Store);

        for (var i = 0; i < CronScheduler.MaxOneShotDeleteAttempts + 3; i++)
            await scheduler.RunNowAsync(JobId.From("job-1"));

        conversations.ArchiveAttempts.ShouldBe(CronScheduler.MaxOneShotDeleteAttempts);
        (await context.Store.GetAsync(JobId.From("job-1")))!.Enabled.ShouldBeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private static CronOptions ShortGraceOptions() => new()
    {
        Enabled = true,
        TickIntervalSeconds = 1,
        DefaultJobTimeoutSeconds = 600,
        ActiveRunCancellationGraceSeconds = 0
    };

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IReadOnlyList<ICronAction> actions,
        IConversationStore? conversationStore = null,
        CronOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<ISessionStore>());
        services.AddSingleton(conversationStore ?? Mock.Of<IConversationStore>());
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(
                options ?? new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 }),
            NullLogger<CronScheduler>.Instance);
    }

    /// <summary>Counts archive attempts and records the ids that actually archived.</summary>
    /// <remarks>
    /// Backed by a Moq <see cref="IConversationStore"/> rather than a hand-rolled stub: the interface
    /// carries ~25 members the delete path never touches, and a hand-rolled double would have to be
    /// re-edited every time an unrelated member is added.
    /// </remarks>
    private sealed class ArchiveRecorder
    {
        private readonly List<string> _archived = [];
        private readonly Func<string, Exception?>? _failure;
        private int _attempts;

        public ArchiveRecorder(Func<string, Exception?>? failure = null)
        {
            _failure = failure;
            Store = BuildStore();
        }

        public IConversationStore Store { get; }

        public int ArchiveAttempts => Volatile.Read(ref _attempts);

        public IReadOnlyList<string> Archived
        {
            get { lock (_archived) { return _archived.ToList(); } }
        }

        private IConversationStore BuildStore()
        {
            var mock = new Mock<IConversationStore>();
            mock.Setup(store => store.ArchiveAsync(
                    It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((ConversationId id, string _, string? _, string _, CancellationToken _) =>
                {
                    Interlocked.Increment(ref _attempts);
                    if (_failure?.Invoke(id.Value) is { } ex)
                        return Task.FromException(ex);

                    lock (_archived) { _archived.Add(id.Value); }
                    return Task.CompletedTask;
                });
            return mock.Object;
        }

        /// <summary>Archive that never succeeds - the wedged-stripe shape.</summary>
        public static ArchiveRecorder AlwaysFailing(Func<string, Exception>? factory = null)
            => new(id => factory?.Invoke(id) ?? new InvalidOperationException($"archive of '{id}' failed"));

        /// <summary>Archive that fails the first <paramref name="failures"/> times, then succeeds.</summary>
        public static ArchiveRecorder FailNTimes(int failures)
        {
            var remaining = failures;
            return new ArchiveRecorder(_ =>
                Interlocked.Decrement(ref remaining) >= 0
                    ? new InvalidOperationException("transient archive failure")
                    : null);
        }
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Cooperative: observes its token and leaves the action body.</summary>
    private sealed class BlockingAction(string actionType) : ICronAction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ActionType => actionType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await Task.WhenAny(_release.Task, cancelled.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Models the production run: ignores its token entirely and keeps holding resources.</summary>
    private sealed class UncooperativeAction(string actionType) : ICronAction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ActionType => actionType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
