using System.Text.Json;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// AC5 for #2634: the lifecycle fields must be reachable from the MODEL-FACING cron tool. Before
/// this change <c>CronTool.cs</c> had zero references to the pre-existing <c>DeleteAfterRun</c>
/// flag - a capability an agent cannot reach is a capability that rots exactly the way the issue
/// describes. These tests pin create, update, and read-back through the tool surface.
/// </summary>
public sealed class CronToolLifecycleTests
{
    // ── Reachable on CREATE ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsDeleteJobAfterRun()
    {
        var (store, captured) = CreateCapturingStore();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "One-shot check",
            ["schedule"] = "17 15 17 7 *",
            ["message"] = "Check the stuck session once.",
            ["deleteJobAfterRun"] = true
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.DeleteJobAfterRun.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_PersistsExpiresAt()
    {
        var (store, captured) = CreateCapturingStore();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Reminder",
            ["schedule"] = "0 9 * * 1",
            ["message"] = "Nudge about the roster design.",
            ["expiresAt"] = "2026-12-31T00:00:00Z"
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Create_WithoutLifecycleFields_LeavesThemInert()
    {
        // AC4 at the tool boundary: a create that says nothing about lifecycle produces a job
        // identical to one created before this change.
        var (store, captured) = CreateCapturingStore();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Ordinary job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Do the usual."
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.DeleteJobAfterRun.ShouldBeFalse();
        captured.Value.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task Create_RejectsAnUnparseableExpiresAt()
    {
        // Silently dropping a bad expiry would leave the agent believing the job will stop firing
        // when it never will - the precise failure mode #2634 is about.
        var (store, _) = CreateCapturingStore();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var arguments = await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Bad expiry",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "hi",
            ["expiresAt"] = "next tuesday"
        });

        await Should.ThrowAsync<ArgumentException>(async () => await tool.ExecuteAsync("call-1", arguments));
    }

    // ── Reachable on UPDATE ───────────────────────────────────────────────────────

    [Fact]
    public async Task Update_SetsLifecycleFields()
    {
        var existing = CreateJob("job-1");
        var (store, captured) = CreateUpdatingStore(existing);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["deleteJobAfterRun"] = true,
            ["expiresAt"] = "2027-01-01T00:00:00Z"
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.DeleteJobAfterRun.ShouldBeTrue();
        captured.Value.ExpiresAt.ShouldBe(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Update_WithoutLifecycleArgs_PreservesExistingValues()
    {
        // An unrelated edit (renaming the job) must not silently clear a one-shot or an expiry.
        var expiry = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var existing = CreateJob("job-1") with { DeleteJobAfterRun = true, ExpiresAt = expiry };
        var (store, captured) = CreateUpdatingStore(existing);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Renamed"
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.Name.ShouldBe("Renamed");
        captured.Value.DeleteJobAfterRun.ShouldBeTrue();
        captured.Value.ExpiresAt.ShouldBe(expiry);
    }

    [Fact]
    public async Task Update_CanClearAnExpiryWithAnEmptyString()
    {
        var existing = CreateJob("job-1") with { ExpiresAt = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero) };
        var (store, captured) = CreateUpdatingStore(existing);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["expiresAt"] = ""
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task Update_CanTurnOffAOneShot()
    {
        var existing = CreateJob("job-1") with { DeleteJobAfterRun = true };
        var (store, captured) = CreateUpdatingStore(existing);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", await PrepareAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["deleteJobAfterRun"] = false
        }));

        captured.Value.ShouldNotBeNull();
        captured.Value!.DeleteJobAfterRun.ShouldBeFalse();
    }

    // ── Readable via LIST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_SurfacesLifecycleFields()
    {
        var expiry = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new Mock<ICronStore>();
        store.Setup(value => value.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateJob("job-1") with { DeleteJobAfterRun = true, ExpiresAt = expiry }]);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });

        // An agent must be able to SEE the disposition, not just set it.
        using var document = JsonDocument.Parse(ReadText(result));
        var job = document.RootElement.EnumerateArray().ShouldHaveSingleItem();
        job.GetProperty("deleteJobAfterRun").GetBoolean().ShouldBeTrue();
        job.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(expiry);
    }

    // ── The tool's advertised schema ──────────────────────────────────────────────

    [Fact]
    public void Definition_AdvertisesTheLifecycleFields()
    {
        var tool = new CronTool(new Mock<ICronStore>().Object, CreateScheduler(), AgentId.From("agent-a"));

        var properties = tool.Definition.Parameters.GetProperty("properties");

        properties.TryGetProperty("deleteJobAfterRun", out _).ShouldBeTrue();
        properties.TryGetProperty("expiresAt", out _).ShouldBeTrue();
        // #1561's session-scoped flag is now reachable too, and is described distinctly so the two
        // cannot be confused for one another.
        properties.TryGetProperty("deleteAfterRun", out _).ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyDictionary<string, object?>> PrepareAsync(
        CronTool tool,
        Dictionary<string, object?> arguments)
        => await tool.PrepareArgumentsAsync(arguments);

    private sealed class Box<T> { public T? Value { get; set; } }

    private static (Mock<ICronStore> Store, Box<CronJob> Captured) CreateCapturingStore()
    {
        var store = new Mock<ICronStore>();
        var captured = new Box<CronJob>();
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback((CronJob job, CancellationToken _) => captured.Value = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        return (store, captured);
    }

    private static (Mock<ICronStore> Store, Box<CronJob> Captured) CreateUpdatingStore(CronJob existing)
    {
        var store = new Mock<ICronStore>();
        var captured = new Box<CronJob>();
        store.Setup(value => value.GetAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        store.Setup(value => value.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CronJobOwnershipExpectation?>(), It.IsAny<CancellationToken>()))
            .Callback((CronJob job, CronJobOwnershipExpectation? _, CancellationToken _) => captured.Value = job)
            .ReturnsAsync((CronJob job, CronJobOwnershipExpectation? _, CancellationToken _) => job);
        return (store, captured);
    }

    private static CronScheduler CreateScheduler()
    {
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            new Mock<ICronStore>().Object,
            Array.Empty<ICronAction>(),
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
    }

    private static CronJob CreateJob(string id)
        => new()
        {
            Id = JobId.From(id),
            Name = $"Job {id}",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            AgentId = AgentId.From("agent-a"),
            Message = "Hello",
            Enabled = true,
            CreatedBy = "agent-a",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static string ReadText(BotNexus.Agent.Core.Types.AgentToolResult result)
        => result.Content
            .Single(content => content.Type == BotNexus.Agent.Core.Types.AgentToolContentType.Text)
            .Value;

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
