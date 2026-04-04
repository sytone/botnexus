using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Cron.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public class AgentCronJobTests
{
    [Fact]
    public void Constructor_Throws_WhenAgentMissing()
    {
        var config = new CronJobConfig { Schedule = "* * * * *", Prompt = "hello" };
        var runnerFactory = new Mock<IAgentRunnerFactory>();
        var sessionManager = new Mock<ISessionManager>();

        var act = () => new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            _ => null,
            NullLogger<AgentCronJob>.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Agent must be provided*");
    }

    [Fact]
    public void Constructor_Throws_WhenPromptMissing()
    {
        var config = new CronJobConfig { Schedule = "* * * * *", Agent = "bender" };
        var runnerFactory = new Mock<IAgentRunnerFactory>();
        var sessionManager = new Mock<ISessionManager>();

        var act = () => new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            _ => null,
            NullLogger<AgentCronJob>.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Prompt must be provided*");
    }

    [Fact]
    public async Task ExecuteAsync_NewSessionMode_RunsAgentAndRoutesToChannels()
    {
        var context = CreateContext("daily-summary", new DateTimeOffset(2026, 04, 03, 09, 30, 11, TimeSpan.Zero));
        var config = new CronJobConfig
        {
            Schedule = "0 9 * * *",
            Agent = "bender",
            Prompt = "Summarize overnight activity.",
            Session = "new",
            OutputChannels = ["alpha", "beta"]
        };

        var runner = new Mock<IAgentRunner>();
        runner.SetupGet(x => x.AgentName).Returns("bender");
        InboundMessage? capturedMessage = null;
        runner.Setup(x => x.RunAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => capturedMessage = message)
            .Returns(Task.CompletedTask);

        var runnerFactory = new Mock<IAgentRunnerFactory>();
        runnerFactory.Setup(x => x.Create("bender")).Returns(runner.Object);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), "bender", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string _, CancellationToken _) =>
            {
                var session = new global::BotNexus.Core.Models.Session { Key = key, AgentName = "bender" };
                session.AddEntry(new SessionEntry(MessageRole.Assistant, "Nightly summary response", DateTimeOffset.UtcNow));
                return session;
            });

        var alpha = new TestChannel("alpha");
        var beta = new TestChannel("beta");
        var channels = new Dictionary<string, IChannel>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = alpha,
            ["beta"] = beta
        };

        var job = new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            name => channels.TryGetValue(name, out var channel) ? channel : null,
            NullLogger<AgentCronJob>.Instance);

        var result = await job.ExecuteAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Nightly summary response");

        capturedMessage.Should().NotBeNull();
        capturedMessage!.SessionKey.Should().Be("cron:daily-summary:20260403093011");
        capturedMessage.Channel.Should().Be("cron");
        capturedMessage.SenderId.Should().Be("cron:daily-summary");
        capturedMessage.Content.Should().Be("Summarize overnight activity.");

        alpha.SentMessages.Should().ContainSingle();
        beta.SentMessages.Should().ContainSingle();
        alpha.SentMessages[0].ChatId.Should().Be("cron:daily-summary");
        alpha.SentMessages[0].Content.Should().Be("Nightly summary response");
    }

    [Theory]
    [InlineData("persistent", "cron:ops-health")]
    [InlineData("named:shared-session", "shared-session")]
    public async Task ExecuteAsync_UsesExpectedSessionKeyForConfiguredMode(string mode, string expectedSessionKey)
    {
        var context = CreateContext("ops-health", new DateTimeOffset(2026, 04, 03, 12, 00, 00, TimeSpan.Zero));
        var config = new CronJobConfig
        {
            Schedule = "*/5 * * * *",
            Agent = "bender",
            Prompt = "Run scheduled health check.",
            Session = mode
        };

        var runner = new Mock<IAgentRunner>();
        runner.SetupGet(x => x.AgentName).Returns("bender");
        InboundMessage? capturedMessage = null;
        runner.Setup(x => x.RunAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => capturedMessage = message)
            .Returns(Task.CompletedTask);

        var runnerFactory = new Mock<IAgentRunnerFactory>();
        runnerFactory.Setup(x => x.Create("bender")).Returns(runner.Object);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), "bender", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string _, CancellationToken _) =>
            {
                var session = new global::BotNexus.Core.Models.Session { Key = key, AgentName = "bender" };
                session.AddEntry(new SessionEntry(MessageRole.Assistant, "ok", DateTimeOffset.UtcNow));
                return session;
            });

        var job = new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            _ => null,
            NullLogger<AgentCronJob>.Instance);

        var result = await job.ExecuteAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        capturedMessage.Should().NotBeNull();
        capturedMessage!.SessionKey.Should().Be(expectedSessionKey);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidNamedSession_FallsBackToPersistentKey()
    {
        var context = CreateContext("ops-health", new DateTimeOffset(2026, 04, 03, 12, 00, 00, TimeSpan.Zero));
        var config = new CronJobConfig
        {
            Schedule = "*/5 * * * *",
            Agent = "bender",
            Prompt = "Run scheduled health check.",
            Session = "named:   "
        };

        var runner = new Mock<IAgentRunner>();
        runner.SetupGet(x => x.AgentName).Returns("bender");
        InboundMessage? capturedMessage = null;
        runner.Setup(x => x.RunAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => capturedMessage = message)
            .Returns(Task.CompletedTask);

        var runnerFactory = new Mock<IAgentRunnerFactory>();
        runnerFactory.Setup(x => x.Create("bender")).Returns(runner.Object);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), "bender", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new global::BotNexus.Core.Models.Session { AgentName = "bender" });

        var job = new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            _ => null,
            NullLogger<AgentCronJob>.Instance);

        var result = await job.ExecuteAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        capturedMessage.Should().NotBeNull();
        capturedMessage!.SessionKey.Should().Be("cron:ops-health");
    }

    [Fact]
    public async Task ExecuteAsync_RoutesOnlyDistinctRunningChannels()
    {
        var context = CreateContext("daily-summary", new DateTimeOffset(2026, 04, 03, 09, 30, 11, TimeSpan.Zero));
        var config = new CronJobConfig
        {
            Schedule = "0 9 * * *",
            Agent = "bender",
            Prompt = "Summarize overnight activity.",
            Session = "new",
            OutputChannels = ["alpha", "ALPHA", "beta", "missing", ""]
        };

        var runner = new Mock<IAgentRunner>();
        runner.SetupGet(x => x.AgentName).Returns("bender");
        runner.Setup(x => x.RunAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var runnerFactory = new Mock<IAgentRunnerFactory>();
        runnerFactory.Setup(x => x.Create("bender")).Returns(runner.Object);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), "bender", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string _, CancellationToken _) =>
            {
                var session = new global::BotNexus.Core.Models.Session { Key = key, AgentName = "bender" };
                session.AddEntry(new SessionEntry(MessageRole.Assistant, "Nightly summary response", DateTimeOffset.UtcNow));
                return session;
            });

        var alpha = new TestChannel("alpha", isRunning: true);
        var beta = new TestChannel("beta", isRunning: false);
        var channels = new Dictionary<string, IChannel>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = alpha,
            ["beta"] = beta
        };

        var job = new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            name => channels.TryGetValue(name, out var channel) ? channel : null,
            NullLogger<AgentCronJob>.Instance);

        var result = await job.ExecuteAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        alpha.SentMessages.Should().ContainSingle();
        beta.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RunnerThrows_ReturnsFailure()
    {
        var context = CreateContext("daily-summary", new DateTimeOffset(2026, 04, 03, 09, 30, 11, TimeSpan.Zero));
        var config = new CronJobConfig
        {
            Schedule = "0 9 * * *",
            Agent = "bender",
            Prompt = "Summarize overnight activity."
        };

        var runner = new Mock<IAgentRunner>();
        runner.SetupGet(x => x.AgentName).Returns("bender");
        runner.Setup(x => x.RunAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("runner failed"));

        var runnerFactory = new Mock<IAgentRunnerFactory>();
        runnerFactory.Setup(x => x.Create("bender")).Returns(runner.Object);

        var sessionManager = new Mock<ISessionManager>();
        var job = new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            _ => null,
            NullLogger<AgentCronJob>.Instance);

        var result = await job.ExecuteAsync(context, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("runner failed");
    }

    private static CronJobContext CreateContext(string jobName, DateTimeOffset actualTime)
    {
        return new CronJobContext
        {
            JobName = jobName,
            CorrelationId = "corr-001",
            ScheduledTime = actualTime.AddMinutes(-1),
            ActualTime = actualTime,
            Services = new ServiceCollection().BuildServiceProvider()
        };
    }

    private sealed class TestChannel : IChannel
    {
        public TestChannel(string name, bool isRunning = true)
        {
            Name = name;
            DisplayName = name;
            IsRunning = isRunning;
        }

        public string Name { get; }
        public string DisplayName { get; }
        public bool IsRunning { get; }
        public bool SupportsStreaming => false;
        public List<OutboundMessage> SentMessages { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public bool IsAllowed(string senderId) => true;
    }
}
