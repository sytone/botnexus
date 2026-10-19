using System.Reflection;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CronPlannedShutdownAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_CurrentPlannedShutdown_RejectsWithoutRun(bool accept)
    {
        using var fixture = new Fixture();
        fixture.State.Current = true;
        var error = await Should.ThrowAsync<InvalidOperationException>(() => fixture.ManualAsync(accept));
        error.Message.ShouldContain("planned shutdown");
        fixture.AssertNoRun();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_ShutdownDuringLookup_RejectsWithoutRun(bool accept)
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Store.Setup(s => s.GetAsync(fixture.Job.Id, It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await release.Task; return fixture.Job; });
        var request = fixture.ManualAsync(accept);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.State.Current = true;
        release.TrySetResult();
        await Should.ThrowAsync<InvalidOperationException>(() => request);
        fixture.AssertNoRun();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_PreviousOnly_AllowsRun(bool accept)
    {
        using var fixture = new Fixture();
        fixture.State.Previous = true;
        await fixture.ManualAsync(accept);
        await fixture.DrainAsync();
        fixture.Action.Count.ShouldBe(1);
        fixture.Store.Verify(s => s.RecordRunStartAsync(fixture.Job.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_ShutdownDuringAdmittedWrite_DoesNotRejectContinuation(bool accept)
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Store.Setup(s => s.RecordRunStartAsync(fixture.Job.Id, It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await release.Task; return fixture.Run; });
        var request = fixture.ManualAsync(accept);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.State.Current = true;
        release.TrySetResult();
        await request;
        await fixture.DrainAsync();
        fixture.Action.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Tick_CurrentPlannedShutdown_PreservesScheduleWithoutRun()
    {
        using var fixture = new Fixture();
        fixture.State.Current = true;
        await fixture.TickAsync();
        fixture.AssertNoRun();
        fixture.AssertNoScheduleMutation();
    }

    [Fact]
    public async Task Tick_ShutdownDuringList_PreservesScheduleWithoutRun()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Store.Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await release.Task; return new[] { fixture.Job }; });
        var tick = fixture.TickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.State.Current = true;
        release.TrySetResult();
        await tick;
        fixture.AssertNoRun();
        fixture.AssertNoScheduleMutation();
    }

    [Fact]
    public async Task Tick_ShutdownDuringReschedule_DoesNotStartRunOrFabricateFailure()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Store.Setup(s => s.SetNextRunAtAsync(fixture.Job.Id, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await release.Task; });
        var tick = fixture.TickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.State.Current = true;
        release.TrySetResult();
        await tick;
        fixture.AssertNoRun();
    }

    [Fact]
    public async Task Dispatch_ShutdownDuringFreshLookup_DoesNotStartRunOrFabricateFailure()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Store.Setup(s => s.GetAsync(fixture.Job.Id, It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await release.Task; return fixture.Job; });
        await fixture.TickAsync(drain: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.State.Current = true;
        release.TrySetResult();
        await fixture.DrainAsync();
        fixture.AssertNoRun();
    }

    [Fact]
    public async Task Tick_PreviousOnly_AllowsRun()
    {
        using var fixture = new Fixture();
        fixture.State.Previous = true;
        await fixture.TickAsync();
        fixture.Action.Count.ShouldBe(1);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class State : IPlannedShutdownState
    {
        public bool Current { get; set; }
        public bool Previous { get; set; }
        public bool CurrentShutdownIsPlanned => Current;
        public bool PreviousShutdownWasPlanned => Previous;
    }

    private sealed class RecordingAction : ICronAction
    {
        public string ActionType => "admission-test";
        public int Count { get; private set; }
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Mock<ICronStore> Store { get; } = new();
        public State State { get; } = new();
        public RecordingAction Action { get; } = new();
        public CronJob Job { get; } = new()
        {
            Id = JobId.From("admission-test"), Name = "admission test", Schedule = "* * * * *",
            ActionType = "admission-test", NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        public CronRun Run { get; }
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly CronScheduler _scheduler;

        public Fixture()
        {
            Run = new CronRun { Id = RunId.From("admission-run"), JobId = Job.Id, StartedAt = DateTimeOffset.UtcNow, Status = CronRunStatus.Running };
            Store.Setup(s => s.GetAsync(Job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Job);
            Store.Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Job });
            Store.Setup(s => s.ListRunningRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<CronRun>());
            Store.Setup(s => s.RecordRunStartAsync(Job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Run);
            var options = new Mock<IOptionsMonitor<CronOptions>>();
            options.SetupGet(o => o.CurrentValue).Returns(new CronOptions());
            _scheduler = new CronScheduler(Store.Object, [Action], _services.GetRequiredService<IServiceScopeFactory>(), options.Object, NullLogger<CronScheduler>.Instance, plannedShutdownState: State);
        }

        public Task<CronRun> ManualAsync(bool accept) => accept ? _scheduler.AcceptRunNowAsync(Job.Id) : _scheduler.RunNowAsync(Job.Id);

        public async Task TickAsync(bool drain = true)
        {
            var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Tick seam missing.");
            var task = method.Invoke(_scheduler, [CancellationToken.None]) as Task
                ?? throw new InvalidOperationException("Tick did not return a task.");
            await task;
            if (drain) await DrainAsync();
        }

        public async Task DrainAsync()
        {
            var method = typeof(CronScheduler).GetMethod("WaitForInFlightAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Drain seam missing.");
            var task = method.Invoke(_scheduler, [CancellationToken.None]) as Task
                ?? throw new InvalidOperationException("Drain did not return a task.");
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            var field = typeof(CronScheduler).GetField("_ownedManualRuns", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Manual ownership seam missing.");
            var owned = field.GetValue(_scheduler) as System.Collections.Concurrent.ConcurrentDictionary<string, Task>
                ?? throw new InvalidOperationException("Manual ownership registry missing.");
            await Task.WhenAll(owned.Values).WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void AssertNoRun()
        {
            Action.Count.ShouldBe(0);
            Store.Verify(s => s.RecordRunStartAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
            Store.Verify(s => s.RecordRunFinalizationAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        public void AssertNoScheduleMutation()
        {
            Store.Verify(s => s.SetNextRunAtAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
            Store.Verify(s => s.SetBackoffUntilAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        public void Dispose() { _scheduler.Dispose(); _services.Dispose(); }
    }
}
