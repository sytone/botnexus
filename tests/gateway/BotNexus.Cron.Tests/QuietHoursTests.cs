using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class QuietHoursTests
{
    [Fact]
    public async Task ExecuteAsync_DuringQuietHours_SkipsExecution()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var action = new HeartbeatAction(clock);
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = CreateDescriptor(new QuietHoursConfig
        {
            Enabled = true,
            Start = "00:00",
            End = "23:59",
            Timezone = "UTC"
        });

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateHeartbeatContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Never);
        context.SessionId.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OutsideQuietHours_ExecutesNormally()
    {
        var action = new HeartbeatAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = CreateDescriptor(new QuietHoursConfig
        {
            Enabled = true,
            Start = "00:00",
            End = "00:00",
            Timezone = "UTC"
        });
        var sessionId = SessionId.From("cron:heartbeat:run-1");

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateHeartbeatContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(value => value.CreateSessionAsync(AgentId.From("agent-a"), "Ping from heartbeat", It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
        context.SessionId!.Value.ShouldBe(sessionId);
    }

    [Theory]
    [InlineData("09:00", "17:00", "08:59:59", false)]
    [InlineData("09:00", "17:00", "09:00:00", true)]
    [InlineData("09:00", "17:00", "16:59:59", true)]
    [InlineData("09:00", "17:00", "17:00:00", false)]
    [InlineData("22:00", "06:00", "21:59:59", false)]
    [InlineData("22:00", "06:00", "22:00:00", true)]
    [InlineData("22:00", "06:00", "23:59:59", true)]
    [InlineData("22:00", "06:00", "00:00:00", true)]
    [InlineData("22:00", "06:00", "05:59:59", true)]
    [InlineData("22:00", "06:00", "06:00:00", false)]
    [InlineData("00:00", "23:59", "00:00:00", true)]
    [InlineData("00:00", "23:59", "23:58:59", true)]
    [InlineData("00:00", "23:59", "23:59:00", false)]
    [InlineData("00:00", "23:59", "23:59:59", false)]
    public Task ExecuteAsync_QuietHoursBoundary_UsesStartInclusiveEndExclusive(
        string start, string end, string utcTime, bool shouldSkip)
        => AssertQuietHoursAsync(new QuietHoursConfig
        {
            Enabled = true,
            Start = start,
            End = end,
            Timezone = "UTC"
        }, utcTime, shouldSkip);

    [Theory]
    [InlineData(true, "12:00", "12:00", "11:59:59")]
    [InlineData(true, "12:00", "12:00", "12:00:00")]
    [InlineData(true, "12:00", "12:00", "12:00:01")]
    [InlineData(false, "09:00", "17:00", "12:00:00")]
    [InlineData(true, "invalid", "17:00", "12:00:00")]
    [InlineData(true, "09:00", "invalid", "12:00:00")]
    public Task ExecuteAsync_EmptyDisabledOrInvalidQuietHours_ExecutesNormally(
        bool enabled, string start, string end, string utcTime)
        => AssertQuietHoursAsync(new QuietHoursConfig
        {
            Enabled = enabled,
            Start = start,
            End = end,
            Timezone = "UTC"
        }, utcTime, shouldSkip: false);

    [Theory]
    [InlineData("Asia/Tokyo", "UTC", "00:00:00", true)]
    [InlineData("Asia/Tokyo", "UTC", "08:00:00", false)]
    [InlineData(null, "Asia/Tokyo", "00:00:00", true)]
    [InlineData(null, "Asia/Tokyo", "08:00:00", false)]
    [InlineData(null, null, "09:00:00", true)]
    [InlineData(null, null, "17:00:00", false)]
    [InlineData("Invalid/Timezone", "Asia/Tokyo", "09:00:00", true)]
    [InlineData("Invalid/Timezone", "Asia/Tokyo", "17:00:00", false)]
    public Task ExecuteAsync_QuietHoursTimezone_PreservesExplicitSoulAndUtcFallback(
        string? timezone, string? soulTimezone, string utcTime, bool shouldSkip)
        => AssertQuietHoursAsync(new QuietHoursConfig
        {
            Enabled = true,
            Start = "09:00",
            End = "17:00",
            Timezone = timezone
        }, utcTime, shouldSkip, soulTimezone);

    private static async Task AssertQuietHoursAsync(
        QuietHoursConfig quietHours, string utcTime, bool shouldSkip, string? soulTimezone = null)
    {
        var instant = DateTimeOffset.Parse($"2026-01-01T{utcTime}Z", System.Globalization.CultureInfo.InvariantCulture);
        var action = new HeartbeatAction(new ManualTimeProvider(instant));
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = CreateDescriptor(quietHours, soulTimezone);
        var sessionId = SessionId.From("cron:heartbeat:fixed-clock");

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns(descriptor);
        using var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateHeartbeatContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(value => value.CreateSessionAsync(
            It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()),
            shouldSkip ? Times.Never() : Times.Once());
        if (shouldSkip)
            context.SessionId.ShouldBeNull();
        else
            context.SessionId.ShouldBe(sessionId);
    }

    private static CronExecutionContext CreateHeartbeatContext(IServiceProvider services)
        => new()
        {
            Job = new CronJob
            {
                Id = JobId.From("heartbeat:agent-a"),
                Name = "Heartbeat",
                Schedule = "*/30 * * * *",
                ActionType = "heartbeat",
                AgentId = AgentId.From("agent-a"),
                Message = "Ping from heartbeat",
                Enabled = true,
                System = true,
                CreatedBy = "system:heartbeat",
                CreatedAt = DateTimeOffset.UtcNow
            },
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

    private static AgentDescriptor CreateDescriptor(QuietHoursConfig quietHours, string? soulTimezone = null)
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Soul = soulTimezone is null ? null : new SoulAgentConfig { Enabled = true, Timezone = soulTimezone },
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                QuietHours = quietHours
            }
        };
}
