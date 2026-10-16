using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Abstractions;

namespace BotNexus.Cron.Tests;

public sealed class CronDefaultModelDispatchTests
{
    [Fact]
    public async Task Dispatch_ResolvedOnce_PersistsAuditWithoutChangingJob()
        => await VerifyDispatchAsync();

    private static async Task VerifyDispatchAsync(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetEnvironmentVariable("BOTNEXUS_4604_TEST_ROOT") ?? Path.GetTempPath(), "cron-routing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = Path.Combine(directory, "cron.db");
        var store = new SqliteCronStore(db, new FileSystem());
        var job = new CronJob { Id = JobId.From("routing"), Name = "Routing", Schedule = "* * * * *", ActionType = "agent-prompt", AgentId = AgentId.From("owner"), Message = "test" };
        await store.CreateAsync(job);
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("owner"))).Returns(new AgentDescriptor { AgentId = AgentId.From("owner"), DisplayName = "Owner", ApiProvider = "openai", ModelId = "gpt-4.1" });
        var options = new MutableMonitor(new CronOptions());
        typeof(CronOptions).GetProperty("DefaultModel").ShouldNotBeNull().SetValue(options.CurrentValue, "openai/gpt-4.1-mini");
        var action = new RecordingAction(options);
        var services = new ServiceCollection();
        services.AddSingleton(registry.Object);
        services.AddSingleton(CronModelPreflightTests.BuildRegistry());
        services.AddSingleton<ISecretRedactor>(new Redactor());
        using var provider = services.BuildServiceProvider();
        var scheduler = new CronScheduler(store, [action], provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<CronScheduler>.Instance);
        var run = await scheduler.RunNowAsync(job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        var recorded = await store.GetRunAsync(run.Id);
        recorded.ShouldNotBeNull();
        var audit = recorded.GetType().GetProperty("EffectiveModel")?.GetValue(recorded);
        audit.ShouldNotBeNull();
        audit.GetType().GetProperty("ModelId")?.GetValue(audit).ShouldBe("gpt-4.1-mini");
        action.Model.ShouldBe("openai/gpt-4.1-mini");
        (await store.GetAsync(job.Id)).ShouldNotBeNull().Model.ShouldBeNull();
        var historic = await store.RecordRunStartAsync(job.Id);
        var historicRead = await store.GetRunAsync(historic.Id);
        historicRead.ShouldNotBeNull().GetType().GetProperty("EffectiveModel")?.GetValue(historicRead).ShouldBeNull();
    }

    private sealed class RecordingAction(MutableMonitor options) : ICronAction
    {
        public string ActionType => "agent-prompt";
        public string? Model { get; private set; }
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Model = context.Job.Model;
            typeof(CronOptions).GetProperty("DefaultModel").ShouldNotBeNull().SetValue(options.CurrentValue, "invalid/decommissioned");
            context.Job.Model.ShouldBe(Model);
            return Task.CompletedTask;
        }
    }
    private sealed class MutableMonitor(CronOptions value) : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue => value;
        public CronOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
    private sealed class Redactor : ISecretRedactor
    {
        public string Redact(string input) => input;
        public string RedactForExternalDelivery(string input) => input;
    }
}
