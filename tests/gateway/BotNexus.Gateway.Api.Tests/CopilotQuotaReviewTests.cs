using System.Net;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed partial class CopilotQuotaControllerTests
{
    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("removed")]
    [InlineData("success")]
    [InlineData("busy")]
    public async Task Refresh_RotationWithoutRead_NeverReturnsPreviousAccount(string completion)
    {
        var (fs, path, auth) = MutableAuth();
        var clock = new TimerClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            if (++requests == 1) return Task.FromResult(QuotaResponse());
            entered.TrySetResult();
            return release.Task;
        });
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, clock);
        var seeded = await service.RefreshAsync();
        Assert.Equal(13m, Assert.Single(seeded.Snapshots).Remaining);
        Assert.NotNull(seeded.LastSuccessAtUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        var pending = service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fs.File.WriteAllText(path, completion == "removed" ? "{}" : """{"github-copilot":{"refresh":"account-B-longer"}}""");
        if (completion == "busy")
        {
            var busy = await service.RefreshAsync(force: true);
            Assert.Empty(busy.Snapshots);
            Assert.Null(busy.LastSuccessAtUtc);
            Assert.Equal(2, requests);
        }
        if (completion == "timeout") clock.Advance(TimeSpan.FromSeconds(11));
        else if (completion is "failure" or "removed") release.SetException(new HttpRequestException("private failure"));
        else release.SetResult(QuotaResponse());
        var state = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(state.Snapshots);
        Assert.Null(state.LastSuccessAtUtc);
        Assert.Null(state.LastAttemptAtUtc);
        Assert.Equal("unavailable", state.AttemptState);
        if (completion == "timeout") release.SetResult(QuotaResponse());
    }

    [Fact]
    public async Task Refresh_RotationInsideThrottle_InvalidatesBeforeReturningOrFetching()
    {
        var (fs, path, auth) = MutableAuth();
        var handler = new RecordingHandler(_ => QuotaResponse());
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var seeded = await service.RefreshAsync();
        fs.File.WriteAllText(path, """{"github-copilot":{"refresh":"account-B-longer"}}""");
        var current = await service.RefreshAsync();
        Assert.Equal(13m, Assert.Single(current.Snapshots).Remaining);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("account-B-longer", handler.Requests[1].AuthorizationParameter);
        Assert.NotNull(seeded.LastSuccessAtUtc);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("scoped", false)]
    [InlineData("scoped", true)]
    [InlineData("satellite", false)]
    [InlineData("satellite", true)]
    public async Task BothRoutes_ExecuteDeniedResponseWithoutAuthenticationScheme_Return403(string caller, bool post)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No upstream permitted"));
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(); // No default authentication/forbid scheme.
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var controller = AuthorizedController(service, false);
        controller.HttpContext.RequestServices = provider;
        if (caller == "missing") controller.HttpContext.Items.Clear();
        else if (caller == "satellite") controller.HttpContext.Items["BotNexus.Gateway.CallerIdentity"] = new GatewayCallerIdentity
        {
            CallerId = "satellite:test", IsAdmin = false, Permissions = ["satellite"]
        };
        var result = post ? await controller.RefreshQuota(CancellationToken.None) : await controller.GetQuota(CancellationToken.None);
        await result.ExecuteResultAsync(new ActionContext(controller.HttpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("copilot", "github-copilot")]
    [InlineData("github-copilot", "copilot")]
    public async Task Credential_DisabledCanonicalAlias_CannotBypassViaOtherName(string configured, string requested)
    {
        var (fs, _, _) = MutableAuth();
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig { Providers = new() { [configured] = new ProviderConfig { Enabled = false } } });
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs);
        Assert.Null(auth.ResolveCopilotAccountCredential(configured));
        Assert.Null(auth.ResolveCopilotAccountCredential(requested));
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Disabled"));
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        Assert.Empty((await service.RefreshAsync(requested)).Snapshots);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("completions")]
    [InlineData("premium_interactions")]
    public void Parser_FractionalFieldsAcrossEveryDimension_AreNotFloored(string dimension)
    {
        using var json = JsonDocument.Parse("{\"quota_snapshots\":{\"" + dimension + "\":{\"entitlement\":10.5,\"quota_remaining\":8.25,\"percent_remaining\":79.975,\"overage_count\":0.125}}}");
        var quota = Assert.Single(CopilotAccountQuotaParser.Parse(json.RootElement, DateTimeOffset.UtcNow));
        Assert.Equal(10.5m, quota.Entitlement);
        Assert.Equal(8.25m, quota.Remaining);
        Assert.Equal(79.975m, quota.PercentRemaining);
        Assert.Equal(0.125m, quota.OverageCount);
        Assert.Null(quota.IsUnlimited);
        Assert.True(quota.IsPartial);
    }

    [Theory]
    [InlineData("{\"entitlement\":\"bad\",\"quota_remaining\":-1,\"percent_remaining\":101,\"overage_count\":null,\"unlimited\":\"yes\"}")]
    [InlineData("{}")]
    [InlineData("{\"unlimited\":true,\"entitlement\":-1,\"quota_remaining\":-1}")]
    public void Parser_MalformedAllInvalidAndUnlimitedUnknown_StayPartialWithoutInventedValues(string item)
    {
        using var json = JsonDocument.Parse("{\"quota_snapshots\":{\"chat\":" + item + "}}");
        var quota = Assert.Single(CopilotAccountQuotaParser.Parse(json.RootElement, DateTimeOffset.UtcNow));
        Assert.True(quota.IsPartial);
        Assert.Null(quota.Entitlement);
        Assert.Null(quota.Remaining);
        Assert.Null(quota.PercentRemaining);
        Assert.Null(quota.OverageCount);
        Assert.Null(quota.OveragePermitted);
        Assert.Equal(item.Contains("true", StringComparison.Ordinal) ? true : (bool?)null, quota.IsUnlimited);
        Assert.Equal("provider quota units", quota.Unit);
    }

    [Fact]
    public void Parser_CompleteDimensionAndUnsupportedUnits_AreHonest()
    {
        using var json = JsonDocument.Parse("""{"quota_reset_date":"2099-01-01","quota_snapshots":{"chat":{"entitlement":10.5,"quota_remaining":8.25,"percent_remaining":79.975,"overage_count":0.125,"overage_permitted":false,"unlimited":false,"unit":"private-unsupported-billing-credits"}}}""");
        var quota = Assert.Single(CopilotAccountQuotaParser.Parse(json.RootElement, DateTimeOffset.UtcNow));
        Assert.False(quota.IsPartial);
        Assert.Equal("provider quota units", quota.Unit);
        Assert.DoesNotContain("private-unsupported", JsonSerializer.Serialize(quota), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_FailedAttempt_RetainsPriorSuccessfulObservationIndependentlyOfFractions()
    {
        var clock = new TimerClock();
        var fail = false;
        var handler = new RecordingHandler(_ => fail ? throw new HttpRequestException("failure") : QuotaResponse());
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, clock);
        var success = await service.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        fail = true;
        var failed = await service.RefreshAsync();
        Assert.Equal("error", failed.AttemptState);
        Assert.True(failed.IsStale);
        Assert.Equal(success.LastSuccessAtUtc, failed.LastSuccessAtUtc);
        Assert.Equal(13m, Assert.Single(failed.Snapshots).Remaining);
    }

    private static HttpResponseMessage QuotaResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"quota_snapshots":{"chat":{"quota_remaining":13}}}""")
    };

    private static (System.IO.Abstractions.TestingHelpers.MockFileSystem Fs, string Path, GatewayAuthManager Auth) MutableAuth()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = System.IO.Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        fs.File.WriteAllText(path, """{"github-copilot":{"refresh":"account-A"}}""");
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig());
        return (fs, path, new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs));
    }
}
