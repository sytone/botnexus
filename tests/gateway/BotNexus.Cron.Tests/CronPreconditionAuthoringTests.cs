using BotNexus.Cron.Actions;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CronPreconditionAuthoringTests
{
    [Fact]
    public async Task Tool_CreateUpdatePreserveClearAndSwitch_UsesExistingAuthorizer()
    {
        var store = new Mock<ICronStore>();
        CronJob? stored = null;
        store.Setup(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CronJob job, CancellationToken _) => stored = job);
        store.Setup(s => s.GetAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        store.Setup(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CronJobOwnershipExpectation?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CronJob job, CronJobOwnershipExpectation? _, CancellationToken _) => stored = job);
        var auth = new Mock<ICommandCronAuthorizer>();
        auth.Setup(a => a.AuthorizeAuthoring(It.IsAny<CronJob>(), It.IsAny<string>())).Returns(CommandAuthorizationDecision.Allow("test"));
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = Scheduler(store.Object, services);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("owner"), commandAuthorizer: auth.Object);
        tool.Definition.Parameters.GetProperty("properties").TryGetProperty("preconditionCommand", out _).ShouldBeTrue();
        await Execute(tool, new() { ["action"] = "create", ["name"] = "Probe", ["schedule"] = "* * * * *", ["message"] = "Work", ["preconditionCommand"] = "exit 10" });
        stored.ShouldNotBeNull().PreconditionCommand.ShouldBe("exit 10");
        var id = stored.ShouldNotBeNull().Id.Value;
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["name"] = "Renamed" });
        stored.ShouldNotBeNull().PreconditionCommand.ShouldBe("exit 10");
        auth.Verify(a => a.AuthorizeAuthoring(It.IsAny<CronJob>(), "exit 10"), Times.Exactly(2));
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["preconditionCommand"] = "exit 0" });
        stored.ShouldNotBeNull().PreconditionCommand.ShouldBe("exit 0");
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["preconditionCommand"] = "" });
        stored.ShouldNotBeNull().PreconditionCommand.ShouldBeNull();
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["preconditionCommand"] = "exit 10" });
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["preconditionCommand"] = null });
        stored.ShouldNotBeNull().PreconditionCommand.ShouldBeNull();
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["preconditionCommand"] = "exit 10" });
        await Execute(tool, new() { ["action"] = "update", ["jobId"] = id, ["actionType"] = "command", ["shellCommand"] = "exit 0" });
        stored.ShouldNotBeNull().PreconditionCommand.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_AbsentOrDenyingAuthorizer_CannotPersistProbe(bool denying)
    {
        var store = new Mock<ICronStore>(MockBehavior.Strict);
        var auth = new Mock<ICommandCronAuthorizer>();
        auth.Setup(a => a.AuthorizeAuthoring(It.IsAny<CronJob>(), It.IsAny<string>())).Returns(CommandAuthorizationDecision.Deny("test"));
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = Scheduler(store.Object, services);
        var tool = new CronTool(store.Object, scheduler, AgentId.From("owner"), commandAuthorizer: denying ? auth.Object : null);
        await Should.ThrowAsync<UnauthorizedAccessException>(async () => await Execute(tool, new()
        { ["action"] = "create", ["name"] = "Probe", ["schedule"] = "* * * * *", ["message"] = "Work", ["preconditionCommand"] = "exit 10" }));
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task Execute(CronTool tool, Dictionary<string, object?> args)
        => await tool.ExecuteAsync("call", await tool.PrepareArgumentsAsync(args));

    private static CronScheduler Scheduler(ICronStore store, ServiceProvider services)
        => new(store, [], services.GetRequiredService<IServiceScopeFactory>(), new Monitor(), NullLogger<CronScheduler>.Instance);
    private sealed class Monitor : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = new();
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
}
