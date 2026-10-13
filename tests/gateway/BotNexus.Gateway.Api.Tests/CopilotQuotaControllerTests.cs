using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed partial class CopilotQuotaControllerTests
{
    [Fact]
    public async Task GetQuota_ReturnsMonthlyPremiumInteractionsQuotaWithAvailabilityAndNoCredential()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "quota_reset_date": "2099-01-01", "quota_snapshots": {
                  "premium_interactions": { "quota_id": "premium_interactions", "percent_remaining": 75.5,
                    "quota_remaining": 755, "entitlement": 1000, "overage_count": 0,
                    "overage_permitted": false, "unlimited": false },
                  "chat": { "quota_id": "chat", "unlimited": true }
                } }
                """, Encoding.UTF8, "application/json")
        });
        var source = new CopilotQuotaService(
            CreateAuth("ghu_oauth_not_returned"), new CopilotDiscoveryClient(new HttpClient(handler)),
            NullLogger<CopilotQuotaService>.Instance);
        var controller = AuthorizedController(source);

        var result = await controller.RefreshQuota(CancellationToken.None);

        var quota = Assert.Single(Assert.IsType<CopilotQuotaState>(Assert.IsType<OkObjectResult>(result).Value).Snapshots, x => x.QuotaId == "premium_interactions");
        Assert.Equal(1000m, quota.Entitlement);
        Assert.Equal(755m, quota.Remaining);
        Assert.Equal(75.5m, quota.PercentRemaining);
        Assert.Equal(false, quota.IsUnlimited);
        Assert.Equal("premium_interactions", quota.QuotaId);
        Assert.Equal("2099-01-01", quota.ResetDate);
        Assert.DoesNotContain("ghu_oauth_not_returned", System.Text.Json.JsonSerializer.Serialize(quota), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal(CopilotDiscoveryClient.UserInfoUrl, (handler.Requests[0].RequestUri ?? throw new InvalidOperationException()).ToString());
        Assert.Equal("Bearer", handler.Requests[0].AuthorizationScheme);
        Assert.Equal("ghu_oauth_not_returned", handler.Requests[0].AuthorizationParameter);
    }

    [Fact]
    public async Task GetQuota_EmptySnapshots_ReturnsNoDataWithoutInventingQuota()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"quota_snapshots\": {} }", Encoding.UTF8, "application/json")
        });
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);

        var result = await AuthorizedController(service).RefreshQuota(CancellationToken.None);

        Assert.Empty(Assert.IsType<CopilotQuotaState>(Assert.IsType<OkObjectResult>(result).Value).Snapshots);
    }

    [Fact]
    public async Task GetQuota_MalformedFieldStaysUnknownRatherThanInventingZero()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "quota_snapshots": { "premium_interactions": {
                  "quota_id": "premium_interactions", "percent_remaining": "invalid",
                  "quota_remaining": 5, "entitlement": 10, "unlimited": false
                } } }
                """, Encoding.UTF8, "application/json")
        });
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);

        var result = Assert.IsType<OkObjectResult>(await AuthorizedController(service).RefreshQuota(CancellationToken.None));

        Assert.Null(Assert.Single(Assert.IsType<CopilotQuotaState>(result.Value).Snapshots).PercentRemaining);
        Assert.DoesNotContain("invalid", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetQuota_UnlimitedSnapshotIsExplicit()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "quota_snapshots": { "premium_interactions": {
                  "quota_id": "premium_interactions", "percent_remaining": 100,
                  "quota_remaining": 999999, "entitlement": 100, "unlimited": true
                } } }
                """, Encoding.UTF8, "application/json")
        });
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);

        var result = await AuthorizedController(service).RefreshQuota(CancellationToken.None);

        Assert.Equal(true, Assert.Single(Assert.IsType<CopilotQuotaState>(Assert.IsType<OkObjectResult>(result).Value).Snapshots, x => x.QuotaId == "premium_interactions").IsUnlimited);
    }

    [Fact]
    public async Task GetQuota_CachedValueExpiresAndRefreshesAfterFiveMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new TimerClock();
        timeProvider.SetNow(now);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "quota_snapshots": { "premium_interactions": {
                  "quota_id": "premium_interactions", "percent_remaining": 50,
                  "quota_remaining": 5, "entitlement": 10, "unlimited": false
                } } }
                """, Encoding.UTF8, "application/json")
        });
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, timeProvider);

        await service.RefreshAsync();
        await service.RefreshAsync();
        Assert.Single(handler.Requests);

        now = now.AddMinutes(6);
        timeProvider.SetNow(now);
        await service.RefreshAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetQuota_UpstreamFailureIsThrottledAndDoesNotExposeExceptionOrToken()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("private diagnostic ghu_secret"));
        var service = new CopilotQuotaService(CreateAuth("ghu_secret"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var controller = AuthorizedController(service);

        var first = Assert.IsType<OkObjectResult>(await controller.RefreshQuota(CancellationToken.None));
        var second = Assert.IsType<OkObjectResult>(await controller.RefreshQuota(CancellationToken.None));

        Assert.Equal("error", Assert.IsType<CopilotQuotaState>(first.Value).AttemptState);
        Assert.Equal("error", Assert.IsType<CopilotQuotaState>(second.Value).AttemptState);
        Assert.DoesNotContain("ghu_secret", System.Text.Json.JsonSerializer.Serialize(first.Value), StringComparison.Ordinal);
        Assert.DoesNotContain("private diagnostic", System.Text.Json.JsonSerializer.Serialize(first.Value), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetQuota_CancellationPropagatesAndDoesNotReturnCredential()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        using var cancellation = new CancellationTokenSource();
        var pending = service.RefreshAsync(cancellationToken: cancellation.Token);
        await handler.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task GetQuota_AgentScopedIdentityIsDeniedBeforeAnyUpstreamRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must not fetch quota"));
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var controller = AuthorizedController(service, isAdmin: false);

        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.RefreshQuota(CancellationToken.None)).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetQuota_ChangingCredentialInvalidatesCachedAccountQuota()
    {
        var fileSystem = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var authPath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem), "auth.json");
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(authPath) ?? throw new InvalidOperationException());
        void SetCredential(string token) => fileSystem.File.WriteAllText(authPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["github-copilot"] = new { type = "oauth", refresh = token, access = "session" }
        }));
        SetCredential("oauth-first");
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig());
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fileSystem);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"quota_snapshots\":{\"premium_interactions\":{\"quota_id\":\"premium_interactions\",\"entitlement\":10,\"quota_remaining\":5,\"percent_remaining\":50}}}", Encoding.UTF8, "application/json")
        });
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);

        await service.RefreshAsync();
        SetCredential("oauth-second-longer");
        Assert.Empty((await service.ReadAsync()).Snapshots);
        Assert.Single(handler.Requests);
        await service.RefreshAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("oauth-second-longer", handler.Requests[1].AuthorizationParameter);
    }

    [Fact]
    public async Task Read_IsLocalAndFractionsMissingFieldsAndAllDimensionsSurvive()
    {
        const string credential = "credential-unique-4626";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"login":"profile-unique-4626","organization_login_list":["org-unique-4626"],
                 "quota_reset_date":"2099-01-01","quota_snapshots":{
                 "premium_interactions":{"quota_id":"premium_interactions","entitlement":1000.5,"quota_remaining":800.25,"percent_remaining":79.975,"overage_count":0.25,"overage_permitted":false},
                 "chat":{"quota_id":"chat","unlimited":true,"entitlement":-1,"quota_remaining":-1},
                 "completions":{"quota_id":"completions"},"raw-unique-4626":{"entitlement":123}}}
                """, Encoding.UTF8, "application/json")
        });
        var service = new CopilotQuotaService(CreateAuth(credential), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var empty = await service.ReadAsync();
        Assert.Empty(empty.Snapshots);
        Assert.Empty(handler.Requests);
        await service.RefreshAsync();
        var state = await service.ReadAsync();
        Assert.Equal(3, state.Snapshots.Count);
        var premium = Assert.Single(state.Snapshots, x => x.QuotaId == "premium_interactions");
        Assert.Equal(1000.5m, premium.Entitlement);
        Assert.Equal(800.25m, premium.Remaining);
        Assert.Equal(79.975m, premium.PercentRemaining);
        Assert.Equal(0.25m, premium.OverageCount);
        Assert.Equal("provider quota units", premium.Unit);
        Assert.Null(Assert.Single(state.Snapshots, x => x.QuotaId == "chat").Remaining);
        Assert.Null(Assert.Single(state.Snapshots, x => x.QuotaId == "completions").Entitlement);
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        foreach (var sentinel in new[] { credential, "profile-unique-4626", "org-unique-4626", "raw-unique-4626" })
            Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refresh_FailurePreservesStaleSuccessAndForcedRefreshCannotBypassThrottle()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new TimerClock();
        clock.SetNow(now);
        var fail = false;
        var handler = new RecordingHandler(_ => fail ? throw new HttpRequestException("secret-unique-4626") : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":5.25}}}")
        });
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, clock);
        var first = await service.RefreshAsync();
        await service.RefreshAsync(force: true);
        Assert.Single(handler.Requests);
        now = now.AddMinutes(6);
        clock.SetNow(now);
        Assert.True((await service.ReadAsync()).IsStale);
        Assert.Single(handler.Requests);
        fail = true;
        var failed = await service.RefreshAsync();
        Assert.True(failed.IsStale);
        Assert.Equal(first.LastSuccessAtUtc, failed.LastSuccessAtUtc);
        Assert.Equal(5.25m, Assert.Single(failed.Snapshots).Remaining);
        await service.RefreshAsync(force: true);
        Assert.Equal(2, handler.Requests.Count);
        now = now.AddSeconds(59);
        clock.SetNow(now);
        await service.RefreshAsync(force: true);
        Assert.Equal(2, handler.Requests.Count);
        now = now.AddSeconds(1);
        clock.SetNow(now);
        await service.RefreshAsync(force: true);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Refresh_ConcurrentRequestsDoNotQueueAndReadDoesNotWait()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler((_, ct) => release.Task.WaitAsync(ct));
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var pending = service.RefreshAsync();
        await handler.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => service.RefreshAsync(force: true)));
        Assert.All(concurrent, x => Assert.Equal("loading", x.AttemptState));
        Assert.Equal("loading", (await service.ReadAsync()).AttemptState);
        Assert.Single(handler.Requests);
        release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"quota_snapshots\":{}}") });
        await pending;
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refresh_TimeoutIsBoundedEvenWhenTransportIgnoresCancellation()
    {
        var clock = new TimerClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler((_, _) => release.Task);
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance, clock);
        var pending = service.RefreshAsync();
        await handler.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(11));
        var state = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("error", state.AttemptState);
        await service.RefreshAsync(force: true);
        Assert.Single(handler.Requests);
        release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothRoutes_DenyMissingOrNonAdminIdentity(bool absent)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must not fetch"));
        var service = new CopilotQuotaService(CreateAuth("oauth"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var controller = AuthorizedController(service, false);
        if (absent) controller.HttpContext.Items.Clear();
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.GetQuota(CancellationToken.None)).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.RefreshQuota(CancellationToken.None)).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Instances_IsolatedAndUnknownCannotFetchDefault()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        fs.File.WriteAllText(path, """{"github-copilot":{"refresh":"first-secret"},"second-auth":{"refresh":"second-secret"}}""");
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig { Providers = new()
        {
            ["github-copilot"] = new(), ["second"] = new() { Type = "github-copilot", ApiKey = "auth:second-auth" },
            ["corp"] = new() { Type = "github-copilot", ApiKey = "auth:second-auth", BaseUrl = "https://enterprise.invalid" },
            ["wrong"] = new() { Type = "openai", ApiKey = "auth:second-auth" }
        } });
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs);
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.Headers.Authorization?.Parameter == "first-secret" ?
                "{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":1.25}}}" :
                "{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":2.75}}}")
        });
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        await service.RefreshAsync();
        await service.RefreshAsync("second");
        Assert.Equal("first-secret", await auth.GetCopilotOAuthTokenAsync("copilot"));
        Assert.Equal("first-secret", await auth.GetCopilotOAuthTokenAsync("github-copilot"));
        Assert.Equal("second-secret", await auth.GetCopilotOAuthTokenAsync("second"));
        Assert.Null(await auth.GetCopilotOAuthTokenAsync("wrong"));
        Assert.Null(await auth.GetCopilotOAuthTokenAsync("corp"));
        Assert.Null(await auth.GetCopilotOAuthTokenAsync("unknown"));
        Assert.Equal(1.25m, Assert.Single((await service.ReadAsync()).Snapshots).Remaining);
        Assert.Equal(2.75m, Assert.Single((await service.ReadAsync("second")).Snapshots).Remaining);
        Assert.Empty((await service.RefreshAsync("unknown")).Snapshots);
        Assert.Null((await service.ReadAsync("unknown")).Instance);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RotationDuringFlight_OldResponseCannotPopulateNewCredentialScope()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        fs.File.WriteAllText(path, """{"github-copilot":{"refresh":"old-secret"}}""");
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig());
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler((_, ct) => release.Task.WaitAsync(ct));
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var pending = service.RefreshAsync();
        await handler.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fs.File.WriteAllText(path, """{"github-copilot":{"refresh":"new-secret-longer"}}""");
        Assert.Empty((await service.ReadAsync()).Snapshots);
        await service.RefreshAsync();
        Assert.Single(handler.Requests);
        release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":99}}}") });
        Assert.Empty((await pending).Snapshots);
        Assert.Empty((await service.ReadAsync()).Snapshots);
    }

    [Fact]
    public async Task PayloadAndCacheCardinality_AreBoundedAndErrorsAndLogsDoNotDiscloseSentinels()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        fs.File.WriteAllText(path, """{"shared":{"refresh":"credential-unique-limit-4626"}}""");
        var config = new PlatformConfig { Providers = Enumerable.Range(0, 70).ToDictionary(i => "account" + i,
            _ => new ProviderConfig { Type = "github-copilot", ApiKey = "auth:shared" }) };
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(config);
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs);
        var logger = new CaptureLogger();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("raw-profile-unique-limit-4626" + new string('x', 256 * 1024))
        });
        var service = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), logger);
        for (var i = 0; i < 70; i++)
        {
            var state = await service.RefreshAsync("account" + i);
            Assert.Empty(state.Snapshots);
            Assert.Equal(i < 64 ? "error" : "unavailable", state.AttemptState);
            var serialized = System.Text.Json.JsonSerializer.Serialize(state);
            Assert.DoesNotContain("credential-unique-limit-4626", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-profile-unique-limit-4626", serialized, StringComparison.Ordinal);
        }
        Assert.Equal(64, handler.Requests.Count);
        Assert.Equal(64, logger.Messages.Count);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain("credential-unique-limit-4626", message, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-profile-unique-limit-4626", message, StringComparison.Ordinal);
        }
    }

    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<CopilotQuotaService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }

    private static CopilotQuotaController AuthorizedController(CopilotQuotaService service, bool isAdmin = true)
    {
        var context = new DefaultHttpContext();
        context.Items["BotNexus.Gateway.CallerIdentity"] = new GatewayCallerIdentity
        {
            CallerId = "test", IsAdmin = isAdmin, AllowedAgents = isAdmin ? [] : ["scoped-agent"]
        };
        return new CopilotQuotaController(service) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static GatewayAuthManager CreateAuth(string oauthToken)
    {
        var fileSystem = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var authPath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem), "auth.json");
        var authDirectory = Path.GetDirectoryName(authPath) ?? throw new InvalidOperationException("Auth path has no directory.");
        fileSystem.Directory.CreateDirectory(authDirectory);
        fileSystem.File.WriteAllText(authPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["github-copilot"] = new { type = "oauth", refresh = oauthToken, access = "session", expires = 4102444800000L, endpoint = "https://api.githubcopilot.com" }
        }));
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig());
        return new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fileSystem);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;
        public List<RequestRecord> Requests { get; } = [];
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request))) { }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(request.RequestUri, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            RequestEntered.TrySetResult();
            return await _response(request, cancellationToken);
        }
    }

    private sealed class TimerClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ClockTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetNow(DateTimeOffset now) => _now = now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (var timer in _timers.ToArray())
                if (!timer.Disposed && timer.Due <= _now) timer.Fire();
        }
        private sealed class ClockTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;
            public bool Disposed { get; private set; }
            public void Fire() { Disposed = true; callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed record RequestRecord(Uri? RequestUri, string? AuthorizationScheme, string? AuthorizationParameter);
}
