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

    // #2745 AC2: every blocked address class must be refused by the shared boundary, and a public
    // https host must still round-trip byte-for-byte.
    [Theory]
    [InlineData("http://127.0.0.1/hook")]          // loopback
    [InlineData("https://localhost/hook")]         // loopback by name
    [InlineData("http://[::1]/hook")]              // IPv6 loopback
    [InlineData("http://10.0.0.5/hook")]           // private RFC-1918
    [InlineData("http://192.168.1.10/hook")]       // private RFC-1918
    [InlineData("http://172.16.0.1/hook")]         // private RFC-1918
    [InlineData("http://169.254.1.1/hook")]        // link-local
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata (IMDS)
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")] // cloud metadata by name
    public void TryNormalize_RejectsBlockedAddressClasses(string url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    [Fact]
    public void TryNormalize_AcceptsPublicHttpsHostByteForByte()
    {
        const string url = "https://hooks.example.com/services/T000/B000?token=abc";
        CronWebhookUrl.TryNormalize(url, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(url);
    }

    // #2745 AC5: an operator must be able to tell WHICH rule fired.
    [Fact]
    public void TryNormalize_BlockedAddressReasonDiffersFromSchemeAndUserInfoReason()
    {
        CronWebhookUrl.TryNormalize("http://169.254.169.254/latest/", out _, out var blockedReason).ShouldBeFalse();
        CronWebhookUrl.TryNormalize("https://u:p@example.com/hook", out _, out var credentialReason).ShouldBeFalse();
        CronWebhookUrl.TryNormalize("ftp://example.com/hook", out _, out var schemeReason).ShouldBeFalse();

        blockedReason.ShouldBe(CronWebhookUrl.BlockedAddressRejectionMessage);
        credentialReason.ShouldBe(CronWebhookUrl.RejectionMessage);
        schemeReason.ShouldBe(CronWebhookUrl.RejectionMessage);
        blockedReason.ShouldNotBe(credentialReason);
    }

    [Fact]
    public void TryNormalize_SuccessReportsNoRejectionReason()
    {
        CronWebhookUrl.TryNormalize("https://example.com/hook", out var normalized, out var reason).ShouldBeTrue();
        normalized.ShouldBe("https://example.com/hook");
        reason.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // #3779: operator-configured blocked hosts.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AC5, and the exact case that fails on main. <c>internal-svc.example.com</c> resolves
    /// publicly and sits in NO private, loopback, link-local or metadata range - the address table
    /// structurally cannot classify it. Only the configured list can, which is why passing that
    /// list is the whole of the fix.
    /// </summary>
    [Theory]
    [InlineData("https://internal-svc.example.com/hook")]
    [InlineData("http://internal-svc.example.com:8080/hook?x=1")]
    public void TryNormalize_RejectsConfiguredBlockedHost_ThatNoAddressRangeCatches(string url)
    {
        string[] blocked = ["internal-svc.example.com"];

        // Pin the premise: without the list this URL is accepted, so the test cannot pass for the
        // wrong reason (e.g. the host silently falling into some range).
        CronWebhookUrl.TryNormalize(url, out var unguarded).ShouldBeTrue();
        unguarded.ShouldBe(url);

        CronWebhookUrl.TryNormalize(url, blocked, out var normalized, out var reason).ShouldBeFalse();
        normalized.ShouldBeNull();
        reason.ShouldBe(CronWebhookUrl.BlockedAddressRejectionMessage);
    }

    /// <summary>AC1: host matching is case-insensitive, matching SsrfValidator's contract.</summary>
    [Theory]
    [InlineData("https://INTERNAL-SVC.example.com/hook")]
    [InlineData("https://Internal-Svc.Example.Com/hook")]
    public void TryNormalize_ConfiguredBlockedHostMatchIsCaseInsensitive(string url)
    {
        CronWebhookUrl.TryNormalize(url, ["internal-svc.example.com"], out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    /// <summary>
    /// AC4: with no list configured - null or empty - behaviour is unchanged. Both spellings are
    /// asserted because <c>SsrfValidator</c> gates enforcement on <c>{ Count: &gt; 0 }</c>, so an
    /// empty list and a null list must be indistinguishable here.
    /// </summary>
    [Fact]
    public void TryNormalize_WithNoConfiguredHosts_BehavesExactlyAsBefore()
    {
        const string url = "https://hooks.example.com/services/T000/B000?token=abc";

        CronWebhookUrl.TryNormalize(url, null, out var viaNull, out var nullReason).ShouldBeTrue();
        CronWebhookUrl.TryNormalize(url, [], out var viaEmpty, out var emptyReason).ShouldBeTrue();

        viaNull.ShouldBe(url);
        viaEmpty.ShouldBe(url);
        nullReason.ShouldBeNull();
        emptyReason.ShouldBeNull();

        // ...and the address-class half of the policy still fires with a list present but unrelated.
        CronWebhookUrl.TryNormalize("http://169.254.169.254/latest/", ["unrelated.example.com"], out var imds, out var imdsReason)
            .ShouldBeFalse();
        imds.ShouldBeNull();
        imdsReason.ShouldBe(CronWebhookUrl.BlockedAddressRejectionMessage);
    }

    /// <summary>AC1: a host NOT on the configured list still round-trips byte-for-byte.</summary>
    [Fact]
    public void TryNormalize_AcceptsHostAbsentFromConfiguredList()
    {
        const string url = "https://hooks.example.com/hook";
        CronWebhookUrl.TryNormalize(url, ["internal-svc.example.com", "other.example.com"], out var normalized)
            .ShouldBeTrue();
        normalized.ShouldBe(url);
    }

    /// <summary>
    /// AC3, declarative half: a config-declared job whose webhook host is on the configured list is
    /// refused at materialisation and persists nothing.
    /// </summary>
    [Fact]
    public async Task SyncConfiguredJobs_RejectsConfiguredBlockedHostWebhookUrl()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = new CronOptions
        {
            Enabled = true,
            WebhookBlockedHosts = ["internal-svc.example.com"],
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["blocked-host-job"] = new()
                {
                    Name = "Blocked host webhook",
                    Schedule = "*/5 * * * *",
                    ActionType = "webhook",
                    WebhookUrl = "https://internal-svc.example.com/hook"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, options, logger);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        (await context.Store.GetAsync(JobId.From("blocked-host-job"))).ShouldBeNull();
        logger.Messages.ShouldContain(m => m.Contains(CronWebhookUrl.BlockedAddressRejectionMessage));
    }

    /// <summary>
    /// AC4, declarative half: the SAME job with no configured list materialises unchanged. Without
    /// this pair the test above would pass equally if the scheduler had simply started rejecting
    /// every webhook job.
    /// </summary>
    [Fact]
    public async Task SyncConfiguredJobs_MaterialisesSameHostWhenNoHostsConfigured()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var options = new CronOptions
        {
            Enabled = true,
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["blocked-host-job"] = new()
                {
                    Name = "Blocked host webhook",
                    Schedule = "*/5 * * * *",
                    ActionType = "webhook",
                    WebhookUrl = "https://internal-svc.example.com/hook"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, options, new ListLogger<CronScheduler>());

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("blocked-host-job"));
        stored.ShouldNotBeNull();
        stored!.WebhookUrl.ShouldBe("https://internal-svc.example.com/hook");
    }

    [Fact]
    public async Task SyncConfiguredJobs_RejectsBlockedAddressClassWebhookUrl()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = new CronOptions
        {
            Enabled = true,
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["imds-webhook-job"] = new()
                {
                    Name = "IMDS webhook",
                    Schedule = "*/5 * * * *",
                    ActionType = "webhook",
                    WebhookUrl = "http://169.254.169.254/latest/meta-data/"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, options, logger);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        (await context.Store.GetAsync(JobId.From("imds-webhook-job"))).ShouldBeNull();
        logger.Messages.ShouldContain(m => m.Contains(CronWebhookUrl.BlockedAddressRejectionMessage));
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
