using BotNexus.Agent.Core.Types;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2389: the cron tool could not create or maintain <c>command</c> (script) jobs. Creation was
/// hard-coded to <c>agent-prompt</c>, and update demanded a prompt source unconditionally so every
/// edit to a command job failed - even a schedule-only one.
/// </summary>
public sealed class CronToolCommandJobTests
{
    [Fact]
    public async Task Create_WithCommandActionType_PersistsCommandJob()
    {
        var store = new Mock<ICronStore>();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Health check",
            ["schedule"] = "0 * * * *",
            ["actionType"] = "command",
            ["shellCommand"] = "pwsh -NoProfile -File ./check.ps1"
        });

        created.ShouldNotBeNull();
        created!.ActionType.ShouldBe("command");
        created.ShellCommand.ShouldBe("pwsh -NoProfile -File ./check.ps1");
        created.Message.ShouldBeNull();
        created.TemplateName.ShouldBeNull();
    }

    [Fact]
    public async Task Create_WithoutActionType_StillProducesAgentPromptJob()
    {
        var store = new Mock<ICronStore>();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status"
        });

        created.ShouldNotBeNull();
        created!.ActionType.ShouldBe("agent-prompt");
        created.Message.ShouldBe("Summarize status");
        created.ShellCommand.ShouldBeNull();
    }

    [Fact]
    public async Task Create_CommandWithoutShellCommand_Throws()
    {
        var store = new Mock<ICronStore>();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "create",
                ["name"] = "Broken",
                ["schedule"] = "0 * * * *",
                ["actionType"] = "command"
            }));

        ex.Message.ShouldContain("shellCommand");
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Create_AgentPromptWithoutMessageOrTemplate_StillThrows()
    {
        var store = new Mock<ICronStore>();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "create",
                ["name"] = "Broken",
                ["schedule"] = "0 * * * *",
                ["actionType"] = "agent-prompt"
            }));

        ex.Message.ShouldContain("templateName");
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Create_UnknownActionType_Throws()
    {
        var store = new Mock<ICronStore>();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "create",
                ["name"] = "Broken",
                ["schedule"] = "0 * * * *",
                ["actionType"] = "webhook"
            }));

        ex.Message.ShouldContain("webhook");
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // The headline regression: this previously threw "Either 'message' or 'templateName' is required."
    [Fact]
    public async Task Update_ScheduleOnlyOnCommandJob_Succeeds()
    {
        var store = new Mock<ICronStore>();
        var existing = CreateCommandJob();
        CronJob? saved = null;
        SetupDefinitionWrites(store, existing, job => saved = job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-cmd",
            ["schedule"] = "*/30 * * * *"
        });

        saved.ShouldNotBeNull();
        saved!.Schedule.ShouldBe("*/30 * * * *");
        saved.ActionType.ShouldBe("command");
        saved.ShellCommand.ShouldBe("./check.ps1");
    }

    [Fact]
    public async Task Update_EnabledAndNameOnCommandJob_PreservesShellCommand()
    {
        var store = new Mock<ICronStore>();
        var existing = CreateCommandJob();
        CronJob? saved = null;
        SetupDefinitionWrites(store, existing, job => saved = job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-cmd",
            ["name"] = "Renamed check",
            ["enabled"] = false
        });

        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Renamed check");
        saved.Enabled.ShouldBeFalse();
        saved.ShellCommand.ShouldBe("./check.ps1");
        saved.ActionType.ShouldBe("command");
    }

    [Fact]
    public async Task Update_ShellCommandOnCommandJob_ReplacesIt()
    {
        var store = new Mock<ICronStore>();
        var existing = CreateCommandJob();
        CronJob? saved = null;
        SetupDefinitionWrites(store, existing, job => saved = job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-cmd",
            ["shellCommand"] = "./other.ps1"
        });

        saved.ShouldNotBeNull();
        saved!.ShellCommand.ShouldBe("./other.ps1");
    }

    [Fact]
    public async Task Update_ClearingShellCommandOnCommandJob_Throws()
    {
        var store = new Mock<ICronStore>();
        var existing = CreateCommandJob();
        SetupDefinitionWrites(store, existing, _ => { });
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "update",
                ["jobId"] = "job-cmd",
                ["shellCommand"] = "   "
            }));

        ex.Message.ShouldContain("shellCommand");
        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Update_MessageOnAgentPromptJob_StillWorks()
    {
        var store = new Mock<ICronStore>();
        var existing = CreatePromptJob();
        CronJob? saved = null;
        SetupDefinitionWrites(store, existing, job => saved = job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-prompt",
            ["message"] = "Updated prompt",
            ["schedule"] = "*/15 * * * *"
        });

        saved.ShouldNotBeNull();
        saved!.Message.ShouldBe("Updated prompt");
        saved.Schedule.ShouldBe("*/15 * * * *");
        saved.ActionType.ShouldBe("agent-prompt");
        saved.ShellCommand.ShouldBeNull();
    }

    // Switching action type is permitted, but the previous type's fields are dropped so the job
    // is never left as a command job holding a stale prompt (or vice versa).
    [Fact]
    public async Task Update_SwitchingPromptJobToCommand_ClearsPromptFields()
    {
        var store = new Mock<ICronStore>();
        var existing = CreatePromptJob();
        CronJob? saved = null;
        SetupDefinitionWrites(store, existing, job => saved = job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-prompt",
            ["actionType"] = "command",
            ["shellCommand"] = "./switched.ps1"
        });

        saved.ShouldNotBeNull();
        saved!.ActionType.ShouldBe("command");
        saved.ShellCommand.ShouldBe("./switched.ps1");
        saved.Message.ShouldBeNull();
        saved.TemplateName.ShouldBeNull();
        saved.TemplateParameters.ShouldBeNull();
    }

    [Fact]
    public async Task Update_SwitchingCommandJobToPrompt_ClearsShellCommand()
    {
        var store = new Mock<ICronStore>();
        var existing = CreateCommandJob();
        CronJob? saved = null;
        SetupDefinitionWrites(store, existing, job => saved = job);
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-cmd",
            ["actionType"] = "agent-prompt",
            ["message"] = "Now a prompt"
        });

        saved.ShouldNotBeNull();
        saved!.ActionType.ShouldBe("agent-prompt");
        saved.Message.ShouldBe("Now a prompt");
        saved.ShellCommand.ShouldBeNull();
    }

    [Fact]
    public async Task Update_SwitchingCommandJobToPromptWithoutPrompt_Throws()
    {
        var store = new Mock<ICronStore>();
        var existing = CreateCommandJob();
        SetupDefinitionWrites(store, existing, _ => { });
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "update",
                ["jobId"] = "job-cmd",
                ["actionType"] = "agent-prompt"
            }));

        ex.Message.ShouldContain("templateName");
        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void Definition_SchemaDocumentsActionTypeAndShellCommand()
    {
        var store = new Mock<ICronStore>();
        var tool = new CronTool(store.Object, CreateScheduler(), AgentId.From("agent-a"));

        var schema = tool.Definition.Parameters.GetProperty("properties");
        schema.TryGetProperty("actionType", out _).ShouldBeTrue();
        schema.TryGetProperty("shellCommand", out _).ShouldBeTrue();
        tool.Definition.Description.ShouldContain("command");
    }

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

    private static CronJob CreateCommandJob() => new()
    {
        Id = JobId.From("job-cmd"),
        Name = "Health check",
        Schedule = "0 * * * *",
        ActionType = "command",
        AgentId = AgentId.From("agent-a"),
        ShellCommand = "./check.ps1",
        Enabled = true,
        CreatedBy = "agent-a",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CronJob CreatePromptJob() => new()
    {
        Id = JobId.From("job-prompt"),
        Name = "Daily summary",
        Schedule = "0 * * * *",
        ActionType = "agent-prompt",
        AgentId = AgentId.From("agent-a"),
        Message = "Summarize status",
        TemplateParameters = new Dictionary<string, string?> { ["k"] = "v" },
        Enabled = true,
        CreatedBy = "agent-a",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CronScheduler CreateScheduler()
    {
        var store = new Mock<ICronStore>().Object;
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            Array.Empty<ICronAction>(),
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

