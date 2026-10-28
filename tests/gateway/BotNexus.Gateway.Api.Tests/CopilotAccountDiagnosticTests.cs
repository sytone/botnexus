using System.Net;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Api.Tests;

public sealed partial class CopilotQuotaControllerTests
{
    private static string Diagnostic(object state, string field)
    {
        var json = JsonSerializer.SerializeToElement(state);
        json.TryGetProperty(field, out var value).ShouldBeTrue($"Missing fixed diagnostic {field}");
        return value.GetString() ?? throw new InvalidOperationException();
    }

    [Fact]
    public async Task AccountDiagnostic_NotRequestedAndIdentityUnavailable_AreDistinctAndLocal()
    {
        var fixture = DetailsFixture();
        var initial = await fixture.Account.ReadAsync();
        Diagnostic(initial, "RefreshState").ShouldBe("not-requested");
        Diagnostic(initial, "Reason").ShouldBe("not-yet-requested");
        var details = await fixture.Service.ReadAsync();
        details.Account.State.ShouldBe("unavailable");
        Diagnostic(details.Account, "Reason").ShouldBe("not-yet-requested");
        fixture.RemoveCredential();
        foreach (var instance in new[] { "github-copilot", "unknown-private-input", "enterprise", "wrong", "disabled" })
        {
            var read = await fixture.Account.ReadAsync(instance);
            var refreshed = (await AuthorizedController(fixture.Account).RefreshQuota(CancellationToken.None, instance, true))
                .ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CopilotQuotaState>();
            foreach (var state in new[] { read, refreshed })
            {
                state.AttemptState.ShouldBe("unavailable");
                state.Instance.ShouldBeNull();
                state.Snapshots.ShouldBeEmpty();
                Diagnostic(state, "RefreshState").ShouldBe("unavailable");
                Diagnostic(state, "Reason").ShouldBe("identity-unavailable");
                JsonSerializer.Serialize(state).ShouldNotContain(instance);
            }
            var unavailable = await fixture.Service.ReadAsync(instance);
            Diagnostic(unavailable.Account, "Reason").ShouldBe("identity-unavailable");
        }
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AccountDiagnostic_RefreshingAndThrottle_PreserveAttemptAndSuccessContracts()
    {
        var fixture = DetailsFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new RecordingHandler((_, _) => { entered.SetResult(); return release.Task; }));
        var account = new CopilotQuotaService(fixture.Auth, new CopilotDiscoveryClient(client), NullLogger<CopilotQuotaService>.Instance, fixture.Clock);
        var details = new ProviderUsageDetailsService(fixture.Auth, account, fixture.Headers, fixture.Cron, fixture.Options, fixture.Clock);
        var pending = account.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var loading = await account.ReadAsync();
        loading.AttemptState.ShouldBe("loading");
        Diagnostic(loading, "RefreshState").ShouldBe("refreshing");
        Diagnostic(loading, "Reason").ShouldBe("refresh-in-progress");
        Diagnostic((await details.ReadAsync()).Account, "RefreshState").ShouldBe("refreshing");
        Diagnostic(await account.RefreshAsync(force: true), "RefreshState").ShouldBe("refreshing");
        release.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("{\"quota_snapshots\":{}}") });
        var success = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        success.AttemptState.ShouldBe("success");
        Diagnostic(success, "Reason").ShouldBe("success-empty");
        var empty = (await details.ReadAsync()).Account;
        empty.State.ShouldBe("fresh");
        empty.Snapshots.ShouldBeEmpty();
        empty.IsPartial.ShouldBeTrue();
        Diagnostic(empty, "Reason").ShouldBe("success-empty");
        var throttled = await account.RefreshAsync(force: true);
        throttled.AttemptState.ShouldBe("success");
        Diagnostic(throttled, "RefreshState").ShouldBe("throttled");
        Diagnostic(throttled, "Reason").ShouldBe("refresh-throttled");
        throttled.LastSuccessAtUtc.ShouldBe(success.LastSuccessAtUtc);
        Diagnostic(await account.ReadAsync(), "RefreshState").ShouldBe("throttled");
        var projected = (await details.ReadAsync()).Account;
        projected.AttemptState.ShouldBe("success");
        Diagnostic(projected, "RefreshState").ShouldBe("throttled");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Diagnostic(await account.ReadAsync(), "RefreshState").ShouldBe("success");
    }

    [Fact]
    public async Task AccountDiagnostic_CompleteSuccessThenRejectedManualRefresh_DoesNotReportSuccess()
    {
        var fixture = DetailsFixture();
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
            {"quota_reset_date":"2099-01-01","quota_snapshots":{"chat":{"entitlement":10,"quota_remaining":0,
            "percent_remaining":0,"overage_count":0,"overage_permitted":false,"unlimited":false}}}
            """) });
        using var client = new HttpClient(handler);
        var account = new CopilotQuotaService(fixture.Auth, new CopilotDiscoveryClient(client), NullLogger<CopilotQuotaService>.Instance, fixture.Clock);
        var details = new ProviderUsageDetailsService(fixture.Auth, account, fixture.Headers, fixture.Cron, fixture.Options, fixture.Clock);
        var success = (await AuthorizedController(account).RefreshQuota(CancellationToken.None)).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CopilotQuotaState>();
        success.AttemptState.ShouldBe("success");
        Diagnostic(success, "Reason").ShouldBe("success");
        var projected = (await details.ReadAsync()).Account;
        projected.State.ShouldBe("fresh");
        projected.IsPartial.ShouldBeFalse();
        projected.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(0m);
        fixture.RemoveCredential();
        var rejected = (await AuthorizedController(account).RefreshQuota(CancellationToken.None, force: true)).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CopilotQuotaState>();
        rejected.AttemptState.ShouldBe("unavailable");
        rejected.LastSuccessAtUtc.ShouldBeNull();
        rejected.Snapshots.ShouldBeEmpty();
        Diagnostic(rejected, "Reason").ShouldBe("identity-unavailable");
        Diagnostic((await details.ReadAsync()).Account, "Reason").ShouldBe("identity-unavailable");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AccountDiagnostic_UpstreamFailureAndStaleSuccess_AreSanitizedAndSeparate()
    {
        var fixture = DetailsFixture();
        var fail = true;
        var handler = new RecordingHandler(_ => fail ? throw new HttpRequestException("private-endpoint-secret credential-details-secret") :
            new(HttpStatusCode.OK) { Content = new StringContent("{\"login\":\"profile-details-secret\",\"quota_snapshots\":{\"chat\":{\"quota_remaining\":2.5}}}") });
        var account = new CopilotQuotaService(fixture.Auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, fixture.Clock);
        var details = new ProviderUsageDetailsService(fixture.Auth, account, fixture.Headers, fixture.Cron, fixture.Options, fixture.Clock);
        var error = await account.RefreshAsync();
        error.AttemptState.ShouldBe("error");
        Diagnostic(error, "Reason").ShouldBe("upstream-error");
        Diagnostic((await details.ReadAsync()).Account, "Reason").ShouldBe("upstream-error");
        var throttled = await account.RefreshAsync(force: true);
        throttled.AttemptState.ShouldBe("error");
        Diagnostic(throttled, "RefreshState").ShouldBe("throttled");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fail = false;
        var success = await account.RefreshAsync();
        Diagnostic(success, "Reason").ShouldBe("success-partial");
        var fresh = (await details.ReadAsync()).Account;
        fresh.State.ShouldBe("fresh");
        fresh.IsPartial.ShouldBeTrue();
        Diagnostic(fresh, "RefreshState").ShouldBe("success");
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var stale = (await details.ReadAsync()).Account;
        stale.State.ShouldBe("stale");
        Diagnostic(stale, "Reason").ShouldBe("success-stale");
        fail = true;
        var failed = await account.RefreshAsync();
        failed.LastSuccessAtUtc.ShouldBe(success.LastSuccessAtUtc);
        failed.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(2.5m);
        Diagnostic(failed, "Reason").ShouldBe("upstream-error");
        var retained = (await details.ReadAsync()).Account;
        retained.State.ShouldBe("stale");
        retained.AttemptState.ShouldBe("error");
        Diagnostic(retained, "Reason").ShouldBe("upstream-error");
        foreach (var state in new object[] { error, throttled, success, stale, failed, retained })
            foreach (var secret in new[] { "private-endpoint-secret", "credential-details-secret", "profile-details-secret" })
                JsonSerializer.Serialize(state).ShouldNotContain(secret);
        handler.Requests.Count.ShouldBe(3);
    }
}
