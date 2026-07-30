using System.Reflection;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Tests for the firing-time authorization gate on command cron jobs (#2462).
///
/// These tests assert OBSERVABLES, not authorizer return values: for a denied command the
/// assertion is that the sentinel file the command would have created does NOT exist (i.e. no
/// process was started) and that the scheduler recorded a failed run carrying the denial reason.
/// </summary>
public sealed class CommandCronAuthorizationTests : IDisposable
{
    private readonly string _sentinelDir =
        Path.Combine(Path.GetTempPath(), "botnexus-cron-auth-tests", Guid.NewGuid().ToString("N"));

    public CommandCronAuthorizationTests() => Directory.CreateDirectory(_sentinelDir);

    public void Dispose()
    {
        try { Directory.Delete(_sentinelDir, recursive: true); } catch (IOException) { }
    }

    private string SentinelPath(string name) => Path.Combine(_sentinelDir, name + ".txt");

    private string SentinelCommand(string name) =>
        $"Set-Content -LiteralPath '{SentinelPath(name)}' -Value 'started'";

    // ---------------------------------------------------------------------
    // Criterion 5a: an allowed command actually runs.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AllowedCommand_StartsProcessAndSucceeds()
    {
        var sentinel = SentinelPath("allowed");
        var context = BuildContext(SentinelCommand("allowed"), new FakeToolPolicyProvider(
            requiresApproval: false, fallback: ToolApprovalFallback.Allow));

        await new CommandCronAction().ExecuteAsync(context);

        Assert.True(File.Exists(sentinel), "The allowed command must actually have been executed.");
    }

    [Fact]
    public async Task ApprovalRequiredWithAllowFallback_StillRuns_PreservingHistoricalBehaviour()
    {
        var sentinel = SentinelPath("fallback-allow");
        var context = BuildContext(SentinelCommand("fallback-allow"), new FakeToolPolicyProvider(
            requiresApproval: true, fallback: ToolApprovalFallback.Allow));

        await new CommandCronAction().ExecuteAsync(context);

        Assert.True(File.Exists(sentinel));
    }

    // ---------------------------------------------------------------------
    // Criterion 3 + 5b: a denied command is blocked, logged, and never starts a process.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeniedCommand_DoesNotStartAnyProcess()
    {
        var sentinel = SentinelPath("denied");
        var context = BuildContext(SentinelCommand("denied"), new FakeToolPolicyProvider(
            requiresApproval: true, fallback: ToolApprovalFallback.Deny));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new CommandCronAction().ExecuteAsync(context));

        Assert.False(File.Exists(sentinel),
            "A denied command must never reach Process.Start() - the sentinel file proves it did.");
    }

    [Fact]
    public async Task DeniedCommand_LogsAnErrorWithReason()
    {
        var logger = new CapturingLoggerProvider();
        var context = BuildContext(
            SentinelCommand("denied-log"),
            new FakeToolPolicyProvider(requiresApproval: true, fallback: ToolApprovalFallback.Deny),
            logger);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new CommandCronAction().ExecuteAsync(context));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("DENIED", StringComparison.Ordinal));
        Assert.False(File.Exists(SentinelPath("denied-log")));
    }

    [Fact]
    public async Task DeniedCommand_SchedulerRecordsFailedRunWithReason()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var sentinel = SentinelPath("denied-run");
        var job = CronStoreTestContext.CreateJob("denied-job", actionType: "command") with
        {
            ShellCommand = SentinelCommand("denied-run")
        };
        await store.Store.CreateAsync(job);

        var scheduler = CreateScheduler(
            store.Store,
            [new CommandCronAction()],
            new FakeToolPolicyProvider(requiresApproval: true, fallback: ToolApprovalFallback.Deny));

        var run = await scheduler.RunNowAsync(JobId.From("denied-job"));

        Assert.Equal("error", run.Status);
        var history = await store.Store.GetRunHistoryAsync(JobId.From("denied-job"));
        var entry = Assert.Single(history);
        Assert.Equal("error", entry.Status);
        Assert.Contains("denied by the command authorization policy", entry.Error ?? string.Empty,
            StringComparison.Ordinal);
        Assert.False(File.Exists(sentinel), "No process may be started for a denied command.");
    }

    // ---------------------------------------------------------------------
    // Criterion 4: fail closed on unclassifiable command / missing policy provider.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("| Set-Content -LiteralPath '{0}' -Value x")]
    [InlineData("&& Set-Content -LiteralPath '{0}' -Value x")]
    [InlineData("$(Set-Content -LiteralPath '{0}' -Value x)")]
    [InlineData("`Set-Content -LiteralPath '{0}' -Value x")]
    public async Task UnclassifiableCommand_FailsClosed_AndStartsNoProcess(string template)
    {
        var sentinel = SentinelPath("unclassifiable");
        var command = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, sentinel);

        // Note: the policy provider here is maximally permissive. Fail-closed must win anyway.
        var context = BuildContext(command, new FakeToolPolicyProvider(
            requiresApproval: false, fallback: ToolApprovalFallback.Allow));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new CommandCronAction().ExecuteAsync(context));

        Assert.Contains("unclassifiable", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task NoToolPolicyProviderRegistered_FailsClosed()
    {
        var sentinel = SentinelPath("no-policy");
        var context = BuildContext(SentinelCommand("no-policy"), policy: null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new CommandCronAction().ExecuteAsync(context));

        Assert.False(File.Exists(sentinel),
            "With no policy provider the gate cannot classify the command and must fail closed.");
    }

    // ---------------------------------------------------------------------
    // Criterion 5d: agent-prompt jobs are entirely unaffected by the command gate.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AgentPromptJob_IsUnaffectedByCommandAuthorization()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("prompt-job", actionType: "test-action");
        await store.Store.CreateAsync(job);

        var action = new CountingAction("test-action");
        // Deny-everything policy: a command job would be blocked outright.
        var scheduler = CreateScheduler(
            store.Store,
            [action, new CommandCronAction()],
            new FakeToolPolicyProvider(requiresApproval: true, fallback: ToolApprovalFallback.Deny));

        var run = await scheduler.RunNowAsync(JobId.From("prompt-job"));

        Assert.Equal("ok", run.Status);
        Assert.Equal(1, action.ExecutionCount);
        var history = await store.Store.GetRunHistoryAsync(JobId.From("prompt-job"));
        Assert.Equal("ok", Assert.Single(history).Status);
    }

    // ---------------------------------------------------------------------
    // Executable extraction unit coverage (supports the fail-closed classification).
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("pwsh -c foo", "pwsh")]
    [InlineData("  git status ", "git")]
    [InlineData("\"C:/Program Files/x/y.exe\" -a", "C:/Program Files/x/y.exe")]
    public void TryExtractExecutable_ReturnsLeadingToken(string command, string expected)
    {
        Assert.Equal(expected, ToolPolicyCommandCronAuthorizer.TryExtractExecutable(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("| foo")]
    [InlineData("$(foo)")]
    [InlineData("\"\" foo")]
    public void TryExtractExecutable_ReturnsNullForUnclassifiable(string? command)
    {
        Assert.Null(ToolPolicyCommandCronAuthorizer.TryExtractExecutable(command));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static CronExecutionContext BuildContext(
        string command,
        IToolPolicyProvider? policy,
        ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (loggerProvider is not null)
                b.AddProvider(loggerProvider);
        });
        if (policy is not null)
            services.AddSingleton(policy);
        services.AddSingleton<ICommandCronAuthorizer, ToolPolicyCommandCronAuthorizer>();
        var provider = services.BuildServiceProvider();

        var job = new CronJob
        {
            Id = JobId.From("auth-job"),
            Name = "Auth job",
            Schedule = "* * * * *",
            ActionType = "command",
            AgentId = AgentId.From("agent-a"),
            ShellCommand = command
        };

        return new CronExecutionContext
        {
            Job = job,
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = provider
        };
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        IToolPolicyProvider policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(policy);
        services.AddSingleton<ICommandCronAuthorizer, ToolPolicyCommandCronAuthorizer>();
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class TestOptionsMonitor(CronOptions value) : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = value;
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }

    private sealed class FakeToolPolicyProvider(bool requiresApproval, ToolApprovalFallback fallback)
        : IToolPolicyProvider
    {
        public ToolRiskLevel GetRiskLevel(string toolName) =>
            requiresApproval ? ToolRiskLevel.Dangerous : ToolRiskLevel.Safe;

        public bool RequiresApproval(string toolName, string? agentId = null) => requiresApproval;

        public ToolApprovalFallback GetApprovalFallback(string toolName, string? agentId = null) => fallback;

        public IReadOnlyList<string> GetDeniedForHttp() => [];
    }

    private sealed class CountingAction(string actionType) : ICronAction
    {
        public string ActionType { get; } = actionType;
        public int ExecutionCount;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ExecutionCount);
            return Task.CompletedTask;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capturing(this);
        public void Dispose() { }

        private sealed class Capturing(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                    owner.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
