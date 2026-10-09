using System.Net;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed partial class CopilotQuotaControllerTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("scoped")]
    [InlineData("satellite")]
    public async Task Details_DeniedMvcExecution_Returns403BeforeQuery(string caller)
    {
        var fixture = DetailsFixture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var controller = DetailsController(fixture.Service, false);
        controller.HttpContext.RequestServices = provider;
        if (caller == "missing") controller.HttpContext.Items.Clear();
        if (caller == "satellite") controller.HttpContext.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity
        { CallerId = "satellite:test", IsAdmin = false, Permissions = ["satellite"] };
        var result = await controller.GetDetails(CancellationToken.None);
        await result.ExecuteResultAsync(new ActionContext(controller.HttpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        controller.Response.StatusCode.ShouldBe(403);
        await fixture.Cron.DidNotReceiveWithAnyArgs().GetRunActivityAsync(default, default);
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Details_LocalRead_SeparatesFractionalCountsAndHeaderPercentageWithoutHeadroom()
    {
        var fixture = DetailsFixture();
        var empty = await fixture.Service.ReadAsync();
        empty.Account.State.ShouldBe("unavailable");
        fixture.Handler.Requests.ShouldBeEmpty();
        await fixture.Account.RefreshAsync();
        var credential = fixture.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        fixture.Headers.Observe(new(new(credential.Instance, credential.Generation), CopilotQuotaDimension.PremiumInteractions, 1, DateTimeOffset.UtcNow,
            new(1000.5m, 25.125m, 17.75m, 0.25m, false, DateTimeOffset.Parse("2099-01-01T00:00:00Z"), false)));
        fixture.Headers.ObserveLegacyUnattributed();
        var result = await fixture.Service.ReadAsync();
        result.Account.State.ShouldBe("fresh");
        result.Account.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(800.25m);
        var header = result.Headers.ShouldHaveSingleItem();
        header.RemainingPercent.ShouldBe(25.125m);
        header.TotalRemainingCount.ShouldBe(17.75m);
        header.Source.ShouldBe("response-headers");
        header.RemainingPercentUnit.ShouldBe("percent");
        result.Account.Source.ShouldBe("account-api");
        result.LegacyUnattributedHeaderResponses.ShouldBe(1);
        var json = JsonSerializer.Serialize(result);
        foreach (var sentinel in new[] { "credential-details-secret", "profile-details-secret", "org-details-secret", "private-raw", credential.Generation, "RequestOrder", "headroom" })
            json.ShouldNotContain(sentinel);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown-private-input")]
    [InlineData("disabled")]
    [InlineData("wrong")]
    [InlineData("enterprise")]
    public async Task Details_UnsupportedInstance_FailsClosedWithoutInputOrDefaultAccount(string instance)
    {
        var fixture = DetailsFixture();
        await fixture.Account.RefreshAsync();
        var result = await fixture.Service.ReadAsync(instance);
        result.Instance.ShouldBeNull();
        result.Account.State.ShouldBe("unavailable");
        result.Account.Snapshots.ShouldBeEmpty();
        result.Headers.ShouldBeEmpty();
        JsonSerializer.Serialize(result.Account).ShouldNotContain(instance);
        JsonSerializer.Serialize(result).ShouldNotContain("private-endpoint-secret");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Details_AvailableInstances_OnlyEnabledCopilotNamesAndTypesWithCanonicalAlias()
    {
        var fixture = DetailsFixture();
        var result = await fixture.Service.ReadAsync();
        result.AvailableInstances.Select(x => x.Instance).ShouldBe(new[] { "github-copilot", "second", "enterprise" }.Order(StringComparer.Ordinal));
        result.AvailableInstances.ShouldAllBe(x => x.Type == "github-copilot");
        var json = JsonSerializer.Serialize(result.AvailableInstances);
        json.ShouldNotContain("auth:");
        json.ShouldNotContain("https:");
    }

    [Fact]
    public async Task Details_TwoInstancesAndRotation_NeverMixGenerations()
    {
        var fixture = DetailsFixture();
        await fixture.Account.RefreshAsync();
        await fixture.Account.RefreshAsync("second");
        var first = fixture.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        var second = fixture.Auth.ResolveCopilotAccountCredential("second") ?? throw new InvalidOperationException();
        fixture.Headers.Observe(new(new(first.Instance, first.Generation), CopilotQuotaDimension.Chat, 1, DateTimeOffset.UtcNow, new(TotalRemainingCount: 11)));
        fixture.Headers.Observe(new(new(second.Instance, second.Generation), CopilotQuotaDimension.Chat, 2, DateTimeOffset.UtcNow, new(TotalRemainingCount: 22)));
        (await fixture.Service.ReadAsync()).Headers.ShouldHaveSingleItem().TotalRemainingCount.ShouldBe(11m);
        (await fixture.Service.ReadAsync("second")).Headers.ShouldHaveSingleItem().TotalRemainingCount.ShouldBe(22m);
        fixture.Rotate();
        var rotated = await fixture.Service.ReadAsync();
        rotated.Account.Snapshots.ShouldBeEmpty();
        rotated.Headers.ShouldBeEmpty();
        (await fixture.Service.ReadAsync("second")).Headers.ShouldHaveSingleItem().TotalRemainingCount.ShouldBe(22m);
    }

    [Theory]
    [InlineData("rotate", false)]
    [InlineData("remove", false)]
    [InlineData("disable", false)]
    [InlineData("rotate", true)]
    [InlineData("remove", true)]
    [InlineData("disable", true)]
    public async Task Details_CredentialChangesWhileSqlPending_NeverReturnOldAccountOrHeaders(string change, bool storeFails)
    {
        var fixture = DetailsFixture();
        await fixture.Account.RefreshAsync();
        var credential = fixture.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        fixture.Headers.Observe(new(new(credential.Instance, credential.Generation), CopilotQuotaDimension.Chat, 1,
            fixture.Clock.GetUtcNow(), new(TotalRemainingCount: 11)));
        var seeded = await fixture.Service.ReadAsync();
        seeded.Account.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(800.25m);
        seeded.Headers.ShouldHaveSingleItem().TotalRemainingCount.ShouldBe(11m);
        var entered = new TaskCompletionSource<CronRunActivityQuery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CronRunActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.TrySetResult(call.Arg<CronRunActivityQuery>());
            return release.Task;
        });
        var pending = fixture.Service.ReadAsync();
        var query = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pending.IsCompleted.ShouldBeFalse();
        // No local account/header read between changing credentials and releasing SQL.
        switch (change)
        {
            case "rotate": fixture.Rotate(); break;
            case "remove": fixture.RemoveCredential(); break;
            case "disable":
                var providers = fixture.Config.Providers ?? throw new InvalidOperationException();
                providers["github-copilot"] = new() { Type = "github-copilot", Enabled = false };
                break;
            default: throw new InvalidOperationException();
        }
        if (storeFails) release.SetException(new InvalidOperationException("private-db-path-secret"));
        else release.SetResult(EmptyActivity(query));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        result.Account.State.ShouldBe("unavailable");
        result.Account.Snapshots.ShouldBeEmpty();
        result.Account.LastSuccessAtUtc.ShouldBeNull();
        result.Headers.ShouldBeEmpty();
        if (change != "rotate") result.Instance.ShouldBeNull();
        result.Scheduled.State.ShouldBe(storeFails ? "unavailable" : "available");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Details_SqlPendingSamplesFreshClockForLocalHeaderStaleness()
    {
        var fixture = DetailsFixture();
        await fixture.Account.RefreshAsync();
        var credential = fixture.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        fixture.Headers.Observe(new(new(credential.Instance, credential.Generation), CopilotQuotaDimension.Chat, 1,
            fixture.Clock.GetUtcNow(), new(TotalRemainingCount: 11)));
        var entered = new TaskCompletionSource<CronRunActivityQuery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CronRunActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.TrySetResult(call.Arg<CronRunActivityQuery>());
            return release.Task;
        });
        var pending = fixture.Service.ReadAsync();
        var query = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        release.SetResult(EmptyActivity(query));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        result.Account.State.ShouldBe("stale");
        result.Headers.ShouldHaveSingleItem().State.ShouldBe("stale");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Details_RotationDuringCompositeRead_RetriesOnceOrReturnsUnavailable(bool continuous)
    {
        var fixture = DetailsFixture();
        await fixture.Account.RefreshAsync();
        var reads = 0;
        fixture.Options.CurrentValue.Returns(_ =>
        {
            reads++;
            if (reads == 3 || (continuous && reads == 5)) fixture.SetCredential(new string('r', reads));
            return fixture.Config;
        });
        var result = await fixture.Service.ReadAsync();
        result.Account.Snapshots.ShouldBeEmpty();
        result.Headers.ShouldBeEmpty();
        if (continuous) result.Instance.ShouldBeNull();
        reads.ShouldBeLessThanOrEqualTo(7);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Details_StaleAndUnlimitedHeaderFields_AreExplicitWithoutInventedNumbers()
    {
        var fixture = DetailsFixture();
        await fixture.Account.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var credential = fixture.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        fixture.Headers.Observe(new(new(credential.Instance, credential.Generation), CopilotQuotaDimension.Chat, 1, fixture.Clock.GetUtcNow().AddMinutes(-6), new(IsUnlimited: true)));
        var result = await fixture.Service.ReadAsync();
        result.Account.State.ShouldBe("stale");
        var header = result.Headers.ShouldHaveSingleItem();
        header.State.ShouldBe("stale");
        header.IsPartial.ShouldBeTrue();
        header.IsUnlimited.ShouldBe(true);
        header.TotalRemainingCount.ShouldBeNull();
        header.RemainingPercent.ShouldBeNull();
    }

    [Fact]
    public async Task Details_Activity_EmptyHasUtcMetadataAndSingleBoundedGatewayWideQuery()
    {
        var fixture = DetailsFixture();
        var end = DateTimeOffset.Parse("2026-08-10T16:00:00+02:00");
        var result = await fixture.Service.ReadAsync(endUtc: end);
        result.Scheduled.State.ShouldBe("available");
        var data = result.Scheduled.Data ?? throw new InvalidOperationException();
        data.Totals.RunCount.ShouldBe(0);
        result.Scheduled.IsPartial.ShouldBeFalse();
        data.Totals.TotalTokens.ShouldBeNull();
        data.Totals.TotalTurns.ShouldBeNull();
        data.Totals.TotalToolCalls.ShouldBeNull();
        data.Totals.TotalDurationMs.ShouldBeNull();
        data.RequestedEndExclusiveUtc.Offset.ShouldBe(TimeSpan.Zero);
        data.RequestedStartInclusiveUtc.ShouldBe(end.ToUniversalTime().AddDays(-1));
        result.Scheduled.Scope.ShouldBe("gateway-wide, provider-unattributed");
        result.Scheduled.Measurement.ShouldContain("delegated");
        result.Scheduled.Measurement.ShouldContain("overlap");
        result.Scheduled.CacheMeasurement.ShouldBe("unsupported");
        result.Scheduled.AllConversationMeasurement.ShouldBe("unsupported");
        await fixture.Cron.Received(1).GetRunActivityAsync(Arg.Is<CronRunActivityQuery>(q => q.TopJobLimit == 10 && q.JobId == null && q.EndExclusive == end.ToUniversalTime()), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("measured")]
    [InlineData("prompt")]
    [InlineData("completion")]
    [InlineData("turn")]
    [InlineData("tool")]
    [InlineData("duration")]
    [InlineData("complete")]
    public async Task Details_Activity_PartialIncludesEveryFieldCoverage(string missing)
    {
        var fixture = DetailsFixture();
        var totals = new CronRunActivityTotals
        {
            RunCount = 2, JobCount = 1, MeasuredRunCount = missing == "measured" ? 1 : 2,
            PromptTokenRunCount = missing == "prompt" ? 1 : 2, CompletionTokenRunCount = missing == "completion" ? 1 : 2,
            TurnRunCount = missing == "turn" ? 1 : 2, ToolCallRunCount = missing == "tool" ? 1 : 2,
            DurationRunCount = missing == "duration" ? 1 : 2,
            TotalPromptTokens = 0, TotalCompletionTokens = 0, TotalTokens = 0, TotalTurns = 0, TotalToolCalls = 0, TotalDurationMs = 0
        };
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            EmptyActivity(call.Arg<CronRunActivityQuery>()) with { Totals = totals });
        var result = await fixture.Service.ReadAsync();
        result.Scheduled.IsPartial.ShouldBe(missing != "complete");
        (result.Scheduled.Data ?? throw new InvalidOperationException()).Totals.ShouldBe(totals);
    }

    [Fact]
    public async Task Details_Activity_TotalsNotLimitedToTopTenAndProjectionCapsBackendRows()
    {
        var fixture = DetailsFixture();
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call => EmptyActivity(call.Arg<CronRunActivityQuery>()) with
        {
            Totals = new() { RunCount = 100, JobCount = 15, MeasuredRunCount = 80, TotalTokens = 1000, RunningRunCount = 2, UnfinalizedRunCount = 3 },
            TopJobs = Enumerable.Range(0, 15).Select(i => new CronRunActivityJob { JobId = JobId.From("job-" + i), Totals = new() { TotalTokens = 1 } }).ToArray()
        });
        var result = await fixture.Service.ReadAsync();
        var data = result.Scheduled.Data ?? throw new InvalidOperationException();
        data.TopJobs.Length.ShouldBe(10);
        data.Totals.TotalTokens.ShouldBe(1000);
        data.Totals.JobCount.ShouldBe(15);
        result.Scheduled.IsPartial.ShouldBeTrue();
    }

    [Fact]
    public async Task Details_MaximumWindow_PreservesRetentionAndNowClampsEvenWhenEmpty()
    {
        var fixture = DetailsFixture();
        var now = fixture.Clock.GetUtcNow();
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call => EmptyActivity(call.Arg<CronRunActivityQuery>()) with
        {
            EffectiveStartInclusiveUtc = now.AddDays(-30), EffectiveEndExclusiveUtc = now,
            WindowTruncatedByRetention = true, WindowTruncatedByNow = true
        });
        var result = await fixture.Service.ReadAsync(startUtc: DateTimeOffset.MinValue, endUtc: DateTimeOffset.MaxValue);
        var data = result.Scheduled.Data ?? throw new InvalidOperationException();
        data.RequestedStartInclusiveUtc.ShouldBe(DateTimeOffset.MinValue);
        data.RequestedEndExclusiveUtc.ShouldBe(DateTimeOffset.MaxValue);
        data.EffectiveStartInclusiveUtc.ShouldBe(now.AddDays(-30));
        data.EffectiveEndExclusiveUtc.ShouldBe(now);
        data.WindowTruncatedByRetention.ShouldBeTrue();
        data.WindowTruncatedByNow.ShouldBeTrue();
        data.Totals.RunCount.ShouldBe(0);
        result.Scheduled.IsPartial.ShouldBeTrue();
        await fixture.Cron.Received(1).GetRunActivityAsync(Arg.Is<CronRunActivityQuery>(q => q.TopJobLimit == 10 && q.StartInclusive == DateTimeOffset.MinValue && q.EndExclusive == DateTimeOffset.MaxValue), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("range")]
    [InlineData("private")]
    public async Task Details_ActivityFailure_IsGenericUnavailableWithoutDiagnostics(string failure)
    {
        var fixture = DetailsFixture();
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns<Task<CronRunActivity>>(_ => throw (failure switch
        {
            "unsupported" => new NotSupportedException("private-db-path-secret"),
            "range" => new InvalidOperationException("Activity measurement is out of range."),
            _ => new InvalidOperationException("private-db-path-secret SQL SELECT credential-details-secret")
        }));
        var result = await fixture.Service.ReadAsync();
        result.Scheduled.State.ShouldBe("unavailable");
        result.Scheduled.Data.ShouldBeNull();
        var json = JsonSerializer.Serialize(result);
        json.ShouldNotContain("private-db-path-secret");
        json.ShouldNotContain("SELECT");
    }

    [Fact]
    public async Task Details_InvalidWindow_ReturnsGeneric400BeforeQuery()
    {
        var fixture = DetailsFixture();
        var controller = DetailsController(fixture.Service);
        var now = DateTimeOffset.UtcNow;
        var result = await controller.GetDetails(CancellationToken.None, startUtc: now, endUtc: now.AddDays(-1));
        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBeOfType<ProviderUsageDetailsError>().Error.ShouldBe("Invalid activity interval.");
        await fixture.Cron.DidNotReceiveWithAnyArgs().GetRunActivityAsync(default, default);
    }

    [Fact]
    public async Task Details_CancellationAtStore_Propagates()
    {
        var fixture = DetailsFixture();
        fixture.Cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns<Task<CronRunActivity>>(_ => throw new OperationCanceledException());
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Service.ReadAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Service.ReadAsync(cancellationToken: cancelled.Token));
    }

    private static ProviderUsageDetailsController DetailsController(ProviderUsageDetailsService service, bool admin = true)
    {
        var context = new DefaultHttpContext();
        context.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity { CallerId = "test", IsAdmin = admin, AllowedAgents = admin ? [] : ["scoped"] };
        return new(service) { ControllerContext = new() { HttpContext = context } };
    }

    private static CronRunActivity EmptyActivity(CronRunActivityQuery query) => new()
    {
        RequestedStartInclusiveUtc = query.StartInclusive ?? throw new InvalidOperationException(),
        RequestedEndExclusiveUtc = query.EndExclusive ?? throw new InvalidOperationException(),
        EffectiveStartInclusiveUtc = query.StartInclusive ?? throw new InvalidOperationException(),
        EffectiveEndExclusiveUtc = query.EndExclusive ?? throw new InvalidOperationException(),
        WindowTruncatedByRetention = false, WindowTruncatedByNow = false, Totals = new(), TopJobs = []
    };

    private static DetailsTestFixture DetailsFixture()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        void Rotate(string suffix = "initial") => fs.File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["github-copilot"] = new { refresh = "credential-details-secret-" + suffix },
            ["second-auth"] = new { refresh = "second-credential-details-secret" }
        }));
        Rotate();
        var config = new PlatformConfig { Providers = new()
        {
            ["second"] = new() { Type = "github-copilot", ApiKey = "auth:second-auth" },
            ["disabled"] = new() { Type = "github-copilot", Enabled = false },
            ["wrong"] = new() { Type = "openai" },
            ["enterprise"] = new() { Type = "github-copilot", BaseUrl = "https://private-endpoint-secret.invalid" }
        } };
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(config);
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs);
        var clock = new TimerClock();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
            {"login":"profile-details-secret","organization":"org-details-secret","quota_snapshots":{
            "premium_interactions":{"quota_remaining":800.25,"percent_remaining":79.975,"entitlement":1000.5,"overage_count":0.25},"private-raw":{"quota_remaining":999}}}
            """) });
        var account = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, clock);
        var headers = new CopilotHeaderQuotaStore();
        var cron = Substitute.For<ICronStore>();
        cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call => EmptyActivity(call.Arg<CronRunActivityQuery>()));
        return new(new(auth, account, headers, cron, options, clock), account, auth, headers, cron, handler, clock, options, config, Rotate, () => fs.File.Delete(path));
    }

    private sealed record DetailsTestFixture(ProviderUsageDetailsService Service, CopilotQuotaService Account, GatewayAuthManager Auth,
        CopilotHeaderQuotaStore Headers, ICronStore Cron, RecordingHandler Handler, TimerClock Clock,
        IOptionsMonitor<PlatformConfig> Options, PlatformConfig Config, Action<string> SetCredential, Action RemoveCredential)
    {
        public void Rotate() => SetCredential("rotated-account-longer");
    }
}
