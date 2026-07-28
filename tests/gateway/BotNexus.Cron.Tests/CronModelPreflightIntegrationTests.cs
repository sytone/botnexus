using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2373: the cron tool must preflight a model override at create/update time and the cron
/// actions must fail fast with a classified reason rather than dispatching a doomed run.
/// </summary>
public sealed class CronModelPreflightIntegrationTests
{
    [Fact]
    public async Task Create_WithUnknownModel_IsRejectedAndNoJobIsWritten()
    {
        var store = new Mock<ICronStore>(MockBehavior.Strict);
        var tool = CreateTool(store, CronModelPreflightTests.BuildRegistry());

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status",
            ["model"] = "openai/gpt-4.1-typo"
        });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("openai/gpt-4.1-typo");
        // Strict mock: any CreateAsync call would already have thrown, but assert explicitly so
        // this cannot pass merely because the tool happened to fail somewhere else.
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithKnownModel_Succeeds()
    {
        var store = new Mock<ICronStore>();
        CronJob? created = null;
        store.Setup(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = CreateTool(store, CronModelPreflightTests.BuildRegistry());

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status",
            ["model"] = "openai/gpt-4.1"
        });

        created.ShouldNotBeNull();
        created!.Model.ShouldBe("openai/gpt-4.1");
    }

    [Fact]
    public async Task Create_WithoutRegistry_StillSucceeds()
    {
        // A host with no populated registry must not start refusing valid cron jobs.
        var store = new Mock<ICronStore>();
        CronJob? created = null;
        store.Setup(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = CreateTool(store, modelRegistry: null);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status",
            ["model"] = "openai/gpt-4.1-typo"
        });

        created.ShouldNotBeNull();
        created!.Model.ShouldBe("openai/gpt-4.1-typo");
    }

    [Fact]
    public async Task Update_WithUnknownModel_IsRejectedAndNoDefinitionIsWritten()
    {
        var store = new Mock<ICronStore>();
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("job-1"));
        var tool = CreateTool(store, CronModelPreflightTests.BuildRegistry());

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["model"] = "acme/whatever"
        });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("acme");
        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_KeepingExistingUnknownModel_IsNotRejected()
    {
        // Only a caller-supplied override is preflighted; an update that does not touch the model
        // must not be blocked by a pre-existing bad value (that would wedge the job permanently).
        var store = new Mock<ICronStore>();
        var existing = CreateJob("job-1") with { Model = "legacy/decommissioned" };
        store.Setup(s => s.GetAsync(JobId.From("job-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        store.Setup(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = CreateTool(store, CronModelPreflightTests.BuildRegistry());

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Renamed"
        });

        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentPromptAction_WithUnknownModel_FailsBeforeDispatch()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        trigger.SetupGet(t => t.Type).Returns(TriggerType.Cron);
        trigger.Setup(t => t.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.From("cron:job-1:run-1"));

        var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(CronModelPreflightTests.BuildRegistry())
            .BuildServiceProvider();

        var context = CreateContext(services, model: "openai/gpt-4.1-typo");

        var act = () => action.ExecuteAsync(context);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("openai/gpt-4.1-typo");
        trigger.Verify(
            t => t.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()),
            Times.Never);
        context.SessionId.ShouldBeNull();
    }

    [Fact]
    public async Task AgentPromptAction_WithKnownModel_Dispatches()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        trigger.SetupGet(t => t.Type).Returns(TriggerType.Cron);
        trigger.Setup(t => t.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.From("cron:job-1:run-1"));

        var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(CronModelPreflightTests.BuildRegistry())
            .BuildServiceProvider();

        var context = CreateContext(services, model: "openai/gpt-4.1");

        await action.ExecuteAsync(context);

        context.SessionId.ShouldNotBeNull();
        trigger.Verify(
            t => t.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()),
            Times.Once);
    }

    private static CronExecutionContext CreateContext(IServiceProvider services, string? model) => new()
    {
        Job = CreateJob("job-1") with { Model = model },
        RunId = RunId.From("run-1"),
        TriggeredAt = DateTimeOffset.UtcNow,
        TriggerType = CronTriggerType.Scheduled,
        Services = services
    };

    private static CronTool CreateTool(Mock<ICronStore> store, ModelRegistry? modelRegistry)
        => new(store.Object, CreateScheduler(), AgentId.From("agent-a"), allowCrossAgentCron: false, modelRegistry: modelRegistry);

    private static CronScheduler CreateScheduler()
    {
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            new Mock<ICronStore>().Object,
            Array.Empty<ICronAction>(),
            scopeFactory,
            new PreflightOptionsMonitor(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
    }

    private static CronJob CreateJob(string id) => new()
    {
        Id = JobId.From(id),
        Name = $"Job {id}",
        Schedule = "*/1 * * * *",
        ActionType = "agent-prompt",
        AgentId = AgentId.From("agent-a"),
        Message = "Ping from cron",
        Enabled = true,
        CreatedBy = "agent-a",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class PreflightOptionsMonitor(CronOptions value) : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = value;

        public CronOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
}
