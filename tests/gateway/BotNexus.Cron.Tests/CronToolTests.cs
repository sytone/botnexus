using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Cron.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CronToolTests
{
    [Fact]
    public async Task ExecuteAsync_List_ReturnsJobs()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateJob("job-1", createdBy: "agent-a"),
                CreateJob("job-2", createdBy: "agent-b")
            ]);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var jobs = JsonSerializer.Deserialize<List<CronJobDto>>(ReadText(result), JsonOptions);

        jobs.ShouldNotBeNull();
        jobs!.ShouldHaveSingleItem().Id.ShouldBe("job-1");
    }

    [Fact]
    public async Task ExecuteAsync_Create_CreatesJob()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status"
        });

        created.ShouldNotBeNull();
        created!.ActionType.ShouldBe("agent-prompt");
        created.CreatedBy.ShouldBe("agent-a");
        created.AgentId.ShouldBe("agent-a");
        ReadText(result).ShouldContain("Daily summary");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_OwnedJob_Succeeds()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.DeleteAsync("job-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "delete",
            ["jobId"] = "job-1"
        });

        ReadText(result).ShouldContain("Deleted cron job 'job-1'");
        store.Verify(value => value.DeleteAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Delete_OtherAgentJob_Denied()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "other-agent"));
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "delete",
            ["jobId"] = "job-1"
        });

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExecuteAsync_Update_RecomputesNextRunAt_WhenScheduleChanges()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJob("job-1", createdBy: "agent-a") with
        {
            Schedule = "0 0 1 1 *",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        store.Setup(value => value.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        store.Setup(value => value.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => saved = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["schedule"] = "* * * * *"
        });

        saved.ShouldNotBeNull();
        saved!.Schedule.ShouldBe("* * * * *");
        saved.NextRunAt.ShouldNotBeNull();
        saved.NextRunAt!.Value.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
            "NextRunAt should be recomputed for the new schedule");
    }

    [Fact]
    public async Task ExecuteAsync_Update_PreservesNextRunAt_WhenScheduleUnchanged()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var originalNext = DateTimeOffset.UtcNow.AddHours(1);
        var existingJob = CreateJob("job-1", createdBy: "agent-a") with
        {
            Schedule = "*/5 * * * *",
            NextRunAt = originalNext
        };
        store.Setup(value => value.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        store.Setup(value => value.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => saved = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Updated name"
        });

        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Updated name");
        saved.NextRunAt.ShouldBe(originalNext, "NextRunAt should not change when schedule is unchanged");
    }

    [Fact]
    public async Task ExecuteAsync_Create_SetsTimeZone()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Pacific job",
            ["schedule"] = "0 22 * * *",
            ["timeZone"] = "America/Los_Angeles",
            ["message"] = "Evening check"
        });

        created.ShouldNotBeNull();
        created!.TimeZone.ShouldBe("America/Los_Angeles");
        created.NextRunAt.ShouldNotBeNull();

        // NextRunAt should reflect Pacific interpretation: 22:00 Pacific = 05:00 or 06:00 UTC
        var pacificTz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var localNext = TimeZoneInfo.ConvertTime(created.NextRunAt!.Value, pacificTz);
        localNext.Hour.ShouldBe(22, "schedule should be interpreted in Pacific time");
    }

    [Fact]
    public async Task ExecuteAsync_Update_RecomputesNextRunAt_WhenTimeZoneChanges()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJob("job-1", createdBy: "agent-a") with
        {
            Schedule = "0 12 * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddHours(2)
        };
        store.Setup(value => value.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        store.Setup(value => value.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => saved = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["timeZone"] = "America/New_York"
        });

        saved.ShouldNotBeNull();
        saved!.TimeZone.ShouldBe("America/New_York");
        saved.NextRunAt.ShouldNotBeNull();
        // NextRunAt should be recomputed with the new timezone
        var etTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var localNext = TimeZoneInfo.ConvertTime(saved.NextRunAt!.Value, etTz);
        localNext.Hour.ShouldBe(12, "schedule should be interpreted in Eastern time");
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithInvalidSchedule_SetsNullNextRunAt()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Bad schedule job",
            ["schedule"] = "not valid cron",
            ["message"] = "test"
        });

        created.ShouldNotBeNull();
        created!.NextRunAt.ShouldBeNull("invalid schedule should not compute a NextRunAt");
        created.Schedule.ShouldBe("not valid cron");
    }

    [Fact]
    public async Task ExecuteAsync_Update_WithInvalidSchedule_SetsNullNextRunAt()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJob("job-1", createdBy: "agent-a") with
        {
            Schedule = "*/5 * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(3)
        };
        store.Setup(value => value.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        store.Setup(value => value.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => saved = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["schedule"] = "garbage"
        });

        saved.ShouldNotBeNull();
        saved!.NextRunAt.ShouldBeNull("invalid schedule should null out NextRunAt");
    }

    private static CronScheduler CreateScheduler()
    {
        var store = new Mock<ICronStore>().Object;
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var options = new StaticOptionsMonitor<CronOptions>(new CronOptions());
        return new CronScheduler(
            store,
            Array.Empty<ICronAction>(),
            scopeFactory,
            options,
            NullLogger<CronScheduler>.Instance);
    }

    private static CronJob CreateJob(string id, string createdBy)
        => new()
        {
            Id = id,
            Name = $"Job {id}",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            AgentId = "agent-a",
            Message = "Hello",
            Enabled = true,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class CronJobDto
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
