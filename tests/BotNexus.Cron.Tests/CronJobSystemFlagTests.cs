using System.Text.Json;
using BotNexus.AgentCore.Types;
using BotNexus.Cron.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CronJobSystemFlagTests
{
    [Fact]
    public void CronJob_SystemFlag_DefaultsFalse()
    {
        var job = new CronJob
        {
            Id = "job-1",
            Name = "Job 1",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            CreatedBy = "agent-a",
            CreatedAt = DateTimeOffset.UtcNow
        };

        job.System.Should().BeFalse();
    }

    [Fact]
    public void CronJob_WithSystemTrue_PreservesValue()
    {
        var job = new CronJob
        {
            Id = "job-1",
            Name = "Job 1",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            CreatedBy = "agent-a",
            CreatedAt = DateTimeOffset.UtcNow,
            System = true
        };

        job.System.Should().BeTrue();
    }

    [Fact]
    public async Task CronTool_ListJobs_HidesSystemJobs()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        store.Setup(value => value.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateJob("job-1", createdBy: "agent-a", system: false),
                CreateJob("heartbeat:agent-a", createdBy: "system:heartbeat", system: true)
            ]);
        var tool = new CronTool(store.Object, scheduler, "agent-a", allowCrossAgentCron: true);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var jobs = JsonSerializer.Deserialize<List<CronJobDto>>(ReadText(result), JsonOptions);

        jobs.Should().NotBeNull();
        jobs!.Should().ContainSingle(job => job.Id == "job-1");
        jobs.Should().NotContain(job => job.Id == "heartbeat:agent-a");
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

    private static CronJob CreateJob(string id, string createdBy, bool system)
        => new()
        {
            Id = id,
            Name = $"Job {id}",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            AgentId = "agent-a",
            Message = "Hello",
            Enabled = true,
            System = system,
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
