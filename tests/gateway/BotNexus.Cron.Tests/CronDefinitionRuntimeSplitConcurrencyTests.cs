using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2133 same-job concurrency seam tests against a real SQLite store. These prove that
/// user-owned definition updates and scheduler-owned runtime/conversation writes no longer
/// share a whole-record read-modify-write, so concurrent same-job writes cannot erase each
/// other. Different-job parallelism is deliberately NOT used here - every test drives two
/// writers at the SAME job id, which is the only interleaving the old whole-record
/// <c>UpdateAsync</c> could corrupt.
/// </summary>
public sealed class CronDefinitionRuntimeSplitConcurrencyTests
{
    // Acceptance criterion: "A paused controller/tool definition update cannot regress
    // concurrent run status, timestamps, next run, or conversation pin."
    [Fact]
    public async Task DefinitionUpdate_RacingRuntimeAndConversationWrites_LosesNoRuntimeState()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1") with { Schedule = "*/5 * * * *" });
        var jobId = JobId.From("job-1");

        var runAt = DateTimeOffset.UtcNow;
        var nextRun = runAt.AddMinutes(5);

        // Fire a definition edit concurrently with the scheduler's three narrow runtime writes.
        var definitionEdit = Task.Run(async () =>
        {
            var edit = CronStoreTestContext.CreateJob("job-1") with
            {
                Name = "Edited By Controller",
                Enabled = false,
                // Stale/empty runtime + conversation fields, as a real record round-trip would carry.
                LastRunStatus = null,
                LastRunError = null,
                LastRunAt = null,
                NextRunAt = null,
                ConversationId = null
            };
            await context.Store.UpdateDefinitionAsync(edit);
        });

        var runtimeWrites = Task.Run(async () =>
        {
            await context.Store.RecordRunFinalizationAsync(jobId, runAt, "ok", null);
            await context.Store.SetNextRunAtAsync(jobId, nextRun);
            await context.Store.TrySetConversationIdAsync(jobId, ConversationId.From("conv-1"));
        });

        await Task.WhenAll(definitionEdit, runtimeWrites);

        var loaded = await context.Store.GetAsync(jobId);
        loaded.ShouldNotBeNull();
        // The definition edit landed...
        loaded!.Name.ShouldBe("Edited By Controller");
        loaded.Enabled.ShouldBeFalse();
        // ...and NONE of the scheduler-owned state was regressed by the racing edit.
        loaded.LastRunStatus.ShouldBe("ok");
        loaded.LastRunAt.ShouldNotBeNull();
        loaded.NextRunAt.ShouldNotBeNull();
        loaded.NextRunAt!.Value.ShouldBe(nextRun, TimeSpan.FromSeconds(1));
        loaded.ConversationId!.Value.Value.ShouldBe("conv-1");
    }

    // Acceptance criterion: "Scheduler finalization cannot overwrite a concurrent definition update."
    [Fact]
    public async Task RunFinalization_RacingDefinitionUpdate_PreservesDefinitionEdit()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1") with { Schedule = "*/5 * * * *" });
        var jobId = JobId.From("job-1");

        var finalize = Task.Run(async () =>
            await context.Store.RecordRunFinalizationAsync(jobId, DateTimeOffset.UtcNow, "ok", null));

        var edit = Task.Run(async () =>
            await context.Store.UpdateDefinitionAsync(
                CronStoreTestContext.CreateJob("job-1") with
                {
                    Name = "Edited",
                    Schedule = "0 0 * * *",
                    Enabled = false
                }));

        await Task.WhenAll(finalize, edit);

        var loaded = await context.Store.GetAsync(jobId);
        loaded.ShouldNotBeNull();
        // Definition edit survives finalization...
        loaded!.Name.ShouldBe("Edited");
        loaded.Schedule.ShouldBe("0 0 * * *");
        loaded.Enabled.ShouldBeFalse();
        // ...and the run outcome is still recorded.
        loaded.LastRunStatus.ShouldBe("ok");
    }

    // Tool path: the CronTool update splits into a narrow definition write plus (only on a
    // schedule/timezone change) a narrow next-run write, so a concurrent finalization is safe.
    [Fact]
    public async Task ToolUpdate_RacingFinalization_KeepsBothWrites()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(
            CronStoreTestContext.CreateJob("job-1", agentId: "agent-a") with { CreatedBy = "agent-a", Schedule = "*/5 * * * *" });
        var jobId = JobId.From("job-1");

        var scheduler = new CronScheduler(
            context.Store,
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
        var tool = new CronTool(context.Store, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: true);

        var toolUpdate = Task.Run(async () => await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Edited By Tool",
            ["message"] = "run"
        }));

        var finalize = Task.Run(async () =>
            await context.Store.RecordRunFinalizationAsync(jobId, DateTimeOffset.UtcNow, "ok", null));

        await Task.WhenAll(toolUpdate, finalize);

        var loaded = await context.Store.GetAsync(jobId);
        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Edited By Tool");
        loaded.LastRunStatus.ShouldBe("ok");
    }

    private sealed class StaticOptionsMonitor(CronOptions currentValue) : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = currentValue;
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
}
