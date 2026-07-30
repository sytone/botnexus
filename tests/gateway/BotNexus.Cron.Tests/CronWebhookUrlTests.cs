using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2552: the shared webhook URL normalisation boundary, and its use by the config-declared
/// job materialisation path in <see cref="CronScheduler"/>.
/// </summary>
public sealed class CronWebhookUrlTests
{
    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("http://example.com/hook")]
    [InlineData("https://example.com:8443/hook?a=1&b=2")]
    [InlineData("https://example.test/hook")] // shape used by the existing CronSchedulerTests fixture
    public void TryNormalize_AcceptsHttpAndHttpsWithoutUserInfo(string url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_TreatsAbsentValueAsValid(string? url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized).ShouldBeTrue();
        normalized.ShouldBeNull();
    }

    [Fact]
    public void TryNormalize_TrimsSurroundingWhitespace()
    {
        CronWebhookUrl.TryNormalize("  https://example.com/hook  ", out var normalized).ShouldBeTrue();
        normalized.ShouldBe("https://example.com/hook");
    }

    // AC4: non-http(s) schemes.
    [Theory]
    [InlineData("file:///c:/windows/system32/config")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hi")]
    [InlineData("/relative/hook")]
    [InlineData("not a url at all")]
    public void TryNormalize_RejectsNonHttpSchemes(string url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    // AC6 mutation target: these are the tests that must fail when the userinfo clause is removed.
    [Theory]
    [InlineData("https://u:p@example.com/hook")]
    [InlineData("http://user@example.com/hook")]
    [InlineData("https://user:@example.com/hook")]
    public void TryNormalize_RejectsEmbeddedCredentials(string url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    // AC3: the config-declared surface must reject via the same helper, at materialisation.
    [Fact]
    public async Task SyncConfiguredJobs_RejectsCredentialBearingWebhookUrl_AndPersistsNothing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = new CronOptions
        {
            Enabled = true,
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["bad-webhook-job"] = new()
                {
                    Name = "Bad webhook",
                    Schedule = "*/5 * * * *",
                    ActionType = "webhook",
                    WebhookUrl = "https://u:p@example.com/hook"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, options, logger);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        (await context.Store.GetAsync(JobId.From("bad-webhook-job"))).ShouldBeNull();
        logger.Messages.ShouldContain(m => m.Contains("bad-webhook-job") && m.Contains("webhookUrl"));
    }

    [Fact]
    public async Task SyncConfiguredJobs_RejectsNonHttpSchemeWebhookUrl()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var options = new CronOptions
        {
            Enabled = true,
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["file-webhook-job"] = new()
                {
                    Name = "File webhook",
                    Schedule = "*/5 * * * *",
                    ActionType = "webhook",
                    WebhookUrl = "file:///etc/passwd"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, options, new ListLogger<CronScheduler>());

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        (await context.Store.GetAsync(JobId.From("file-webhook-job"))).ShouldBeNull();
    }

    // Guard against the #2415 defect class: a legitimate config-declared webhook job must still
    // materialise, with its URL preserved byte-for-byte.
    [Fact]
    public async Task SyncConfiguredJobs_MaterialisesValidWebhookUrlUnchanged()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var options = new CronOptions
        {
            Enabled = true,
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["good-webhook-job"] = new()
                {
                    Name = "Good webhook",
                    Schedule = "*/5 * * * *",
                    ActionType = "webhook",
                    WebhookUrl = "https://example.test/hook"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, options, new ListLogger<CronScheduler>());

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("good-webhook-job"));
        stored.ShouldNotBeNull();
        stored!.WebhookUrl.ShouldBe("https://example.test/hook");
    }

    private static CronScheduler CreateScheduler(ICronStore store, CronOptions options, ILogger<CronScheduler> logger)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new CronScheduler(
            store,
            [new NoopAction("webhook")],
            services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(options),
            logger);
    }

    private sealed class NoopAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static async Task InvokeSyncConfiguredJobsAsync(CronScheduler scheduler, CronOptions options)
    {
        var method = typeof(CronScheduler).GetMethod("SyncConfiguredJobsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [options, CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
