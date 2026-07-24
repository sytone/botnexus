using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
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
                CreateJobWithTarget("job-2", createdBy: "agent-b", agentId: "agent-b")
            ]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var jobs = JsonSerializer.Deserialize<List<CronJobDto>>(ReadText(result), JsonOptions);

        jobs.ShouldNotBeNull();
        jobs!.ShouldHaveSingleItem().Id.ShouldBe("job-1");
    }

    [Fact]
    public async Task ExecuteAsync_List_SurfacesModelField()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateJob("job-1", createdBy: "agent-a") with
                {
                    Model = "openai/gpt-4.1"
                }
            ]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var jobs = JsonSerializer.Deserialize<List<CronJobDto>>(ReadText(result), JsonOptions);

        jobs.ShouldNotBeNull();
        jobs!.ShouldHaveSingleItem().Model.ShouldBe("openai/gpt-4.1");
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
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status",
            ["model"] = "openai/gpt-4.1"
        });

        created.ShouldNotBeNull();
        created!.ActionType.ShouldBe("agent-prompt");
        created.CreatedBy.ShouldBe("agent-a");
        created.AgentId!.Value.Value.ShouldBe("agent-a");
        created.Model.ShouldBe("openai/gpt-4.1");
        ReadText(result).ShouldContain("Daily summary");
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithTemplateName_PersistsTemplateFields()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Template job",
            ["schedule"] = "*/5 * * * *",
            ["templateName"] = "daily-status",
            ["templateParameters"] = new Dictionary<string, object?> { ["project"] = "botnexus" }
        });

        created.ShouldNotBeNull();
        created!.TemplateName.ShouldBe("daily-status");
        created.TemplateParameters.ShouldNotBeNull();
        created.TemplateParameters!["project"].ShouldBe("botnexus");
        created.Message.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithoutMessageOrTemplate_Throws()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Invalid job",
            ["schedule"] = "*/5 * * * *"
        });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("Either 'message' or 'templateName' is required.");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_OwnedJob_Succeeds()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.DeleteAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "delete",
            ["jobId"] = "job-1"
        });

        ReadText(result).ShouldContain("Deleted cron job 'job-1'");
        store.Verify(value => value.DeleteAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Delete_OtherAgentJob_Denied()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJobWithTarget("job-1", createdBy: "other-agent", agentId: "other-agent"));
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

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
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

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
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

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
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

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
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

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
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

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
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["schedule"] = "garbage"
        });

        saved.ShouldNotBeNull();
        saved!.NextRunAt.ShouldBeNull("invalid schedule should null out NextRunAt");
    }

    [Fact]
    public async Task ExecuteAsync_Update_UpdatesModel()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJob("job-1", createdBy: "agent-a") with
        {
            Model = "copilot/gpt-4o-mini"
        };
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["model"] = "openai/gpt-4.1"
        });

        saved.ShouldNotBeNull();
        saved!.Model.ShouldBe("openai/gpt-4.1");
    }


    // -- Issue #342: target-agent visibility tests --------------------------

    [Fact]
    public async Task ExecuteAsync_List_IncludesJobsTargetingCallingAgent_EvenWhenCreatedByOther()
    {
        // Agent farnsworth targets a job created by nova -- farnsworth should see it.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(s => s.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateJobWithTarget("job-1", createdBy: "nova", agentId: "farnsworth"),
                CreateJobWithTarget("job-2", createdBy: "other-agent", agentId: "other-agent")
            ]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var jobs = JsonSerializer.Deserialize<List<CronJobDto>>(ReadText(result), JsonOptions);

        jobs.ShouldNotBeNull();
        jobs!.ShouldHaveSingleItem().Id.ShouldBe("job-1");
    }

    [Fact]
    public async Task ExecuteAsync_List_DoesNotIncludeJobsNeitherCreatedByNorTargetingCallingAgent()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(s => s.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateJobWithTarget("job-1", createdBy: "nova", agentId: "nova"),
                CreateJobWithTarget("job-2", createdBy: "other", agentId: "other")
            ]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var jobs = JsonSerializer.Deserialize<List<CronJobDto>>(ReadText(result), JsonOptions);

        jobs.ShouldNotBeNull();
        jobs!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_Delete_JobTargetingCallingAgent_CreatedByOther_Succeeds()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJobWithTarget("job-1", createdBy: "nova", agentId: "farnsworth"));
        store.Setup(s => s.DeleteAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "delete",
            ["jobId"] = "job-1"
        });

        ReadText(result).ShouldContain("Deleted cron job 'job-1'");
        store.Verify(s => s.DeleteAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Delete_JobNeitherCreatedByNorTargetingCallingAgent_Denied()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJobWithTarget("job-1", createdBy: "nova", agentId: "nova"));
        var tool = new CronTool(store.Object, scheduler, AgentId.From("farnsworth"));

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "delete",
            ["jobId"] = "job-1"
        });

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExecuteAsync_Update_JobTargetingCallingAgent_CreatedByOther_Succeeds()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJobWithTarget("job-1", createdBy: "nova", agentId: "farnsworth");
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("farnsworth"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Updated name"
        });

        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Updated name");
    }

    [Fact]
    public async Task ExecuteAsync_Run_JobTargetingCallingAgent_CreatedByOther_Succeeds()
    {
        var store = new Mock<ICronStore>();
        var existingJob = CreateJobWithTarget("job-1", createdBy: "nova", agentId: "farnsworth");
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        store.Setup(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CronJob job, CancellationToken _) => (CronJob?)job);

        var cronStore2 = store.Object;
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var options = new StaticOptionsMonitor<CronOptions>(new CronOptions());
        var scheduler = new CronScheduler(
            cronStore2,
            Array.Empty<ICronAction>(),
            scopeFactory,
            options,
            NullLogger<CronScheduler>.Instance);

        var tool = new CronTool(store.Object, scheduler, AgentId.From("farnsworth"));

        // run is permitted for target agent even when created by another
        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "run",
            ["jobId"] = "job-1"
        });

        // Should not throw UnauthorizedAccessException (may throw other scheduling errors)
        var ex = await Record.ExceptionAsync(act);
        ex.ShouldNotBeOfType<UnauthorizedAccessException>();
    }

    // -- Issue #1667: create/update target-agent scoping (security) --------

    [Fact]
    public async Task ExecuteAsync_Create_WithForeignAgentId_AndCrossAgentDisabled_Denied()
    {
        // Agent A must not be able to create a job that runs AS agent B.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: false);

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Cross-agent job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "run as someone else",
            ["agentId"] = "agent-b"
        });

        var ex = await act.ShouldThrowAsync<UnauthorizedAccessException>();
        ex.Message.ShouldContain("Cron jobs may only target the calling agent.");
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithOwnAgentId_AndCrossAgentDisabled_Succeeds()
    {
        // Explicitly naming yourself is fine even with cross-agent disabled.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: false);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Self job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "hello",
            ["agentId"] = "agent-a"
        });

        created.ShouldNotBeNull();
        created!.AgentId!.Value.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithBlankAgentId_DefaultsToCallingAgent()
    {
        // An omitted/blank agentId keeps defaulting to the caller.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: false);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Default job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "hello"
        });

        created.ShouldNotBeNull();
        created!.AgentId!.Value.Value.ShouldBe("agent-a");
        created.CreatedBy.ShouldBe("agent-a");
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithForeignAgentId_AndCrossAgentEnabled_Succeeds()
    {
        // When cross-agent cron is explicitly enabled, a foreign target is allowed.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: true);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Cross-agent job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "run elsewhere",
            ["agentId"] = "agent-b"
        });

        created.ShouldNotBeNull();
        created!.AgentId!.Value.Value.ShouldBe("agent-b");
    }

    [Fact]
    public async Task ExecuteAsync_Update_RetargetToForeignAgent_AndCrossAgentDisabled_Denied()
    {
        // An agent owning a job must not be able to retarget it to run AS another agent.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJobWithTarget("job-1", createdBy: "agent-a", agentId: "agent-a");
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: false);

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["agentId"] = "agent-b"
        });

        var ex = await act.ShouldThrowAsync<UnauthorizedAccessException>();
        ex.Message.ShouldContain("Cron jobs may only target the calling agent.");
        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Update_RetargetToOwnAgent_AndCrossAgentDisabled_Succeeds()
    {
        // Naming yourself as the target on update is fine with cross-agent disabled.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJobWithTarget("job-1", createdBy: "agent-a", agentId: "agent-a");
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: false);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["agentId"] = "agent-a"
        });

        saved.ShouldNotBeNull();
        saved!.AgentId!.Value.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task ExecuteAsync_Update_WithoutAgentId_AndCrossAgentDisabled_KeepsExistingTarget()
    {
        // Omitting agentId on update keeps the existing target without tripping the guard.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJobWithTarget("job-1", createdBy: "agent-a", agentId: "agent-a");
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: false);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Renamed"
        });

        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Renamed");
        saved.AgentId!.Value.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task ExecuteAsync_Update_RetargetToForeignAgent_AndCrossAgentEnabled_Succeeds()
    {
        // When cross-agent cron is enabled, retargeting to a foreign agent is allowed.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        var existingJob = CreateJobWithTarget("job-1", createdBy: "agent-a", agentId: "agent-a");
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        CronJob? saved = null;
        SetupDefinitionWrites(store, existingJob, job => saved = job);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"), allowCrossAgentCron: true);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["agentId"] = "agent-b"
        });

        saved.ShouldNotBeNull();
        saved!.AgentId!.Value.Value.ShouldBe("agent-b");
    }

    // ── History ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_History_ReturnsRunHistory()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CronRun { Id = RunId.From("run-1"), JobId = JobId.From("job-1"), Status = "completed", StartedAt = DateTimeOffset.UtcNow },
                new CronRun { Id = RunId.From("run-2"), JobId = JobId.From("job-1"), Status = "failed", Error = "timeout", StartedAt = DateTimeOffset.UtcNow }
            ]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1"
        });

        var text = ReadText(result);
        text.ShouldContain("run-1");
        text.ShouldContain("completed");
        text.ShouldContain("run-2");
        text.ShouldContain("failed");
    }

    [Fact]
    public async Task ExecuteAsync_History_JobNotFound_Throws()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("nope"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CronJob?)null);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "nope"
        });

        await act.ShouldThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task ExecuteAsync_History_AccessDenied_Throws()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "other-agent"));
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-b"));

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1"
        });

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExecuteAsync_History_RespectsLimitParameter()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1",
            ["limit"] = 5
        });

        store.Verify(s => s.GetRunHistoryAsync(JobId.From("job-1"), 5, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_History_ClampsLimitToMax100()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1",
            ["limit"] = 500
        });

        store.Verify(s => s.GetRunHistoryAsync(JobId.From("job-1"), 100, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_History_ClampsNegativeLimitToOne()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1",
            ["limit"] = -5
        });

        store.Verify(s => s.GetRunHistoryAsync(JobId.From("job-1"), 1, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_History_OutOfRangeJsonNumber_DoesNotThrow_ClampsToMax()
    {
        // limit greater than Int32.MaxValue used to throw FormatException out of JsonElement.GetInt32().
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        // A JSON number larger than Int32.MaxValue, as the agent would supply it.
        var limitElement = JsonDocument.Parse("9999999999").RootElement;

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1",
            ["limit"] = limitElement
        });

        await act.ShouldNotThrowAsync();
        store.Verify(s => s.GetRunHistoryAsync(JobId.From("job-1"), 100, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_History_NonIntegerJsonNumber_DoesNotThrow_Truncates()
    {
        // limit as a fractional JSON number used to throw InvalidOperationException out of JsonElement.GetInt32().
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        var limitElement = JsonDocument.Parse("7.9").RootElement;

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1",
            ["limit"] = limitElement
        });

        await act.ShouldNotThrowAsync();
        store.Verify(s => s.GetRunHistoryAsync(JobId.From("job-1"), 7, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_History_GarbageLimit_FallsBackToDefault()
    {
        // A non-numeric value should fall back to the default (20), not throw.
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1", createdBy: "agent-a"));
        store.Setup(value => value.GetRunHistoryAsync(JobId.From("job-1"), 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "history",
            ["jobId"] = "job-1",
            ["limit"] = "not-a-number"
        });

        store.Verify(s => s.GetRunHistoryAsync(JobId.From("job-1"), 20, It.IsAny<CancellationToken>()), Times.Once());
    }

    // #2133: the tool now splits its update into a narrow UpdateDefinitionAsync (definition
    // columns only) plus, when the schedule/timezone changed, a narrow SetNextRunAtAsync.
    // This stateful helper mirrors the SQLite store: definition writes preserve scheduler-owned
    // runtime bookkeeping and the conversation pin; next-run writes touch only NextRunAt. The
    // onWrite callback captures the latest job so existing assertions on `saved` still hold.
    private static void SetupDefinitionWrites(Mock<ICronStore> store, CronJob initial, Action<CronJob> onWrite)
    {
        var holder = new[] { initial };
        store.Setup(s => s.GetAsync(initial.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => holder[0]);
        store.Setup(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Returns<CronJob, CancellationToken>((job, _) =>
            {
                var existing = holder[0];
                var merged = job with
                {
                    CreatedAt = existing.CreatedAt,
                    LastRunAt = existing.LastRunAt,
                    NextRunAt = existing.NextRunAt,
                    LastRunStatus = existing.LastRunStatus,
                    LastRunError = existing.LastRunError,
                    ConversationId = existing.ConversationId
                };
                holder[0] = merged;
                onWrite(merged);
                return Task.FromResult<CronJob?>(merged);
            });
        store.Setup(s => s.SetNextRunAtAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns<JobId, DateTimeOffset?, CancellationToken>((_, next, _2) =>
            {
                holder[0] = holder[0] with { NextRunAt = next };
                onWrite(holder[0]);
                return Task.CompletedTask;
            });
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


    private static CronJob CreateJobWithTarget(string id, string createdBy, string agentId)
        => new()
        {
            Id = JobId.From(id),
            Name = $"Job {id}",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            AgentId = AgentId.From(agentId),
            Message = "Hello",
            Enabled = true,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow
        };
    private static CronJob CreateJob(string id, string createdBy)
        => new()
        {
            Id = JobId.From(id),
            Name = $"Job {id}",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            AgentId = AgentId.From("agent-a"),
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
        public string? Model { get; set; }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
