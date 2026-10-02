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

public sealed class CopilotQuotaControllerTests
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

        var result = await controller.GetQuota(CancellationToken.None);

        var quota = Assert.IsType<CopilotQuotaDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1000, quota.Entitlement);
        Assert.Equal(755, quota.Remaining);
        Assert.Equal(75.5, quota.PercentRemaining);
        Assert.False(quota.IsUnlimited);
        Assert.Equal("premium_interactions", quota.QuotaId);
        Assert.Equal("2099-01-01", quota.ResetDate);
        Assert.DoesNotContain("ghu_oauth_not_returned", System.Text.Json.JsonSerializer.Serialize(quota), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal(CopilotDiscoveryClient.UserInfoUrl, handler.Requests[0].RequestUri!.ToString());
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

        var result = await AuthorizedController(service).GetQuota(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetQuota_MalformedSnapshotReturnsUnavailableRatherThanInventingZero()
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

        var result = Assert.IsType<ObjectResult>(await AuthorizedController(service).GetQuota(CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
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

        var result = await AuthorizedController(service).GetQuota(CancellationToken.None);

        Assert.True(Assert.IsType<CopilotQuotaDto>(Assert.IsType<OkObjectResult>(result).Value).IsUnlimited);
    }

    [Fact]
    public async Task GetQuota_CachedValueExpiresAndRefreshesAfterFiveMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(_ => now);
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

        await service.GetQuotaAsync();
        await service.GetQuotaAsync();
        Assert.Single(handler.Requests);

        now = now.AddMinutes(6);
        await service.GetQuotaAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetQuota_UpstreamFailureIsThrottledAndDoesNotExposeExceptionOrToken()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("private diagnostic ghu_secret"));
        var service = new CopilotQuotaService(CreateAuth("ghu_secret"), new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var controller = AuthorizedController(service);

        var first = Assert.IsType<ObjectResult>(await controller.GetQuota(CancellationToken.None));
        var second = Assert.IsType<ObjectResult>(await controller.GetQuota(CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, second.StatusCode);
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
        var pending = service.GetQuotaAsync(cancellation.Token);
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

        Assert.IsType<ForbidResult>(await controller.GetQuota(CancellationToken.None));
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

        await service.GetQuotaAsync();
        SetCredential("oauth-second-longer");
        await service.GetQuotaAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("oauth-second-longer", handler.Requests[1].AuthorizationParameter);
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

    private sealed record RequestRecord(Uri? RequestUri, string? AuthorizationScheme, string? AuthorizationParameter);
}
