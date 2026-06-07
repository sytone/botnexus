using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Discovery;
using Shouldly;

namespace BotNexus.Agent.Providers.Copilot.Tests.Discovery;

/// <summary>
/// Verifies the discovery DTOs deserialize the real on-wire shape of
/// <c>/copilot_internal/user</c> and <c>/models</c> as captured from a live
/// Copilot session. Fixtures are scrubbed: real account login, organization,
/// analytics IDs, and endpoint hostnames were replaced with generic
/// placeholders (`octocat`, `example-org`, etc.) before being checked in.
/// </summary>
public class CopilotDiscoveryClientTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery");

    [Fact]
    public void UserInfo_deserializes_enterprise_shape()
    {
        var path = Path.Combine(FixturesDir, "user-enterprise.json");
        var json = File.ReadAllText(path);

        var info = JsonSerializer.Deserialize<CopilotUserInfo>(json, JsonOptions);

        info.ShouldNotBeNull();
        info!.Login.ShouldBe("octocat");
        info.CopilotPlan.ShouldBe("enterprise");
        info.AccessTypeSku.ShouldBe("copilot_enterprise_seat_quota");
        info.ChatEnabled.ShouldBeTrue();
        info.CliEnabled.ShouldBeTrue();
        info.OrganizationLoginList.ShouldNotBeNull();
        info.OrganizationLoginList!.ShouldContain("example-org");

        info.Endpoints.ShouldNotBeNull();
        info.Endpoints!.Api.ShouldBe("https://api.example.githubcopilot.com");
        info.Endpoints.Proxy.ShouldBe("https://proxy.example.githubcopilot.com");
        info.Endpoints.OriginTracker.ShouldBe("https://origin-tracker.example.githubcopilot.com");

        info.QuotaResetDate.ShouldBe("2099-01-01");
        info.QuotaSnapshots.ShouldNotBeNull();
        info.QuotaSnapshots!.Count.ShouldBe(3);
        info.QuotaSnapshots["premium_interactions"].PercentRemaining.ShouldBe(75.5);
        info.QuotaSnapshots["premium_interactions"].Entitlement.ShouldBe(1000);
        info.QuotaSnapshots["premium_interactions"].QuotaRemaining.ShouldBe(755.0);
        info.QuotaSnapshots["chat"].Unlimited.ShouldBeTrue();
    }

    [Fact]
    public void ModelsResponse_deserializes_three_model_shape()
    {
        var path = Path.Combine(FixturesDir, "models-three.json");
        var json = File.ReadAllText(path);

        var resp = JsonSerializer.Deserialize<CopilotModelsResponse>(json, JsonOptions);

        resp.ShouldNotBeNull();
        resp!.Object.ShouldBe("list");
        resp.Data.ShouldNotBeNull();
        resp.Data!.Count.ShouldBe(3);

        var sonnet = resp.Data.Single(m => m.Id == "claude-sonnet-4.5");
        sonnet.Vendor.ShouldBe("Anthropic");
        sonnet.Capabilities!.Family.ShouldBe("claude-sonnet-4.5");
        sonnet.Capabilities.Supports!.Streaming.ShouldBeTrue();
        sonnet.Capabilities.Supports.ToolCalls.ShouldBeTrue();
        sonnet.Capabilities.Supports.Vision.ShouldBeTrue();
        sonnet.Billing!.IsPremium.ShouldBeTrue();
        sonnet.Billing.RestrictedTo.ShouldNotBeNull();
        sonnet.Billing.RestrictedTo!.ShouldContain("enterprise");

        var gpt = resp.Data.Single(m => m.Id == "gpt-5-mini");
        gpt.Capabilities!.Supports!.StructuredOutputs.ShouldBeTrue();
        gpt.Billing!.IsPremium.ShouldBeFalse();
    }

    [Fact]
    public async Task GetUserAsync_sends_bearer_auth_and_accepts_json()
    {
        var fixtureJson = await File.ReadAllTextAsync(Path.Combine(FixturesDir, "user-enterprise.json"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixtureJson, System.Text.Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var client = new CopilotDiscoveryClient(http);

        var info = await client.GetUserAsync("ghu_test_token_value");

        info.Login.ShouldBe("octocat");
        var request = handler.LastRequest.ShouldNotBeNull();
        request!.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri!.ToString().ShouldBe(CopilotDiscoveryClient.UserInfoUrl);
        request.Headers.Authorization.ShouldNotBeNull();
        request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        request.Headers.Authorization.Parameter.ShouldBe("ghu_test_token_value");
        request.Headers.Accept.ShouldContain(h => h.MediaType == "application/json");
    }

    [Fact]
    public async Task GetUserAsync_requires_github_token()
    {
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new CopilotDiscoveryClient(http);

        await Should.ThrowAsync<ArgumentException>(() => client.GetUserAsync(""));
    }

    [Fact]
    public async Task GetModelsAsync_targets_endpoint_models_path_with_copilot_headers()
    {
        var fixtureJson = await File.ReadAllTextAsync(Path.Combine(FixturesDir, "models-three.json"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixtureJson, System.Text.Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var client = new CopilotDiscoveryClient(http);

        var resp = await client.GetModelsAsync("https://api.example.githubcopilot.com/", "tid=fake_session_token");

        resp.Data!.Count.ShouldBe(3);
        var request = handler.LastRequest.ShouldNotBeNull();
        request!.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri!.ToString().ShouldBe("https://api.example.githubcopilot.com/models");
        request.Headers.Authorization!.Parameter.ShouldBe("tid=fake_session_token");
        request.Headers.GetValues("Copilot-Integration-Id").ShouldContain("copilot-developer-cli");
        request.Headers.GetValues("Editor-Version").Single().ShouldStartWith("copilot/");
        request.Headers.GetValues("X-GitHub-Api-Version").ShouldContain("2026-06-01");
    }

    [Fact]
    public async Task GetModelsAsync_requires_endpoint_and_token()
    {
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new CopilotDiscoveryClient(http);

        await Should.ThrowAsync<ArgumentException>(() => client.GetModelsAsync("", "token"));
        await Should.ThrowAsync<ArgumentException>(() => client.GetModelsAsync("https://x", ""));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }
}
