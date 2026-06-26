using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Core.Utilities;
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
    public void ModelsResponse_deserializes_mixed_type_limits()
    {
        var path = Path.Combine(FixturesDir, "models-mixed-limits.json");
        var json = File.ReadAllText(path);

        var resp = JsonSerializer.Deserialize<CopilotModelsResponse>(json, JsonOptions);

        resp.ShouldNotBeNull();
        resp!.Data.ShouldNotBeNull();
        resp.Data!.Count.ShouldBe(1);

        var model = resp.Data[0];
        model.Id.ShouldBe("gpt-5-mini");
        model.Capabilities.ShouldNotBeNull();
        model.Capabilities!.Limits.ShouldNotBeNull();
        model.Capabilities.Limits!.Count.ShouldBe(4);

        // Numeric values accessible via JsonElement
        model.Capabilities.Limits["max_context_window_tokens"].GetInt64().ShouldBe(264000);
        model.Capabilities.Limits["max_output_tokens"].GetInt64().ShouldBe(64000);

        // Non-numeric value (boolean) should not crash deserialization
        model.Capabilities.Limits["vision"].ValueKind.ShouldBe(System.Text.Json.JsonValueKind.True);
    }

    [Fact]
    public void ModelsResponse_numeric_limits_display_as_string()
    {
        var path = Path.Combine(FixturesDir, "models-mixed-limits.json");
        var json = File.ReadAllText(path);

        var resp = JsonSerializer.Deserialize<CopilotModelsResponse>(json, JsonOptions);
        var limits = resp!.Data![0].Capabilities!.Limits!;

        // ToString() on JsonElement works for display purposes regardless of value kind
        limits["max_output_tokens"].ToString().ShouldBe("64000");
        limits["vision"].ToString().ShouldBe("True");
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

    // --- #1653: untrusted Copilot discovery JSON bodies must be size-bounded (OOM-DoS guard) ---
    // The discovery host is config/auth-derived (endpoints.api from auth.json, see #1639), so a
    // hostile / MITM'd / malfunctioning endpoint streaming a multi-GB body must be aborted before
    // it is buffered. These tests prove the reads route through BoundedHttpContent rather than the
    // unbounded ReadFromJsonAsync. They use an INFINITE stream so a regression (unbounded read)
    // surfaces as an abort-or-hang, not a silently passing large allocation.

    [Fact]
    public async Task GetUserAsync_OverCapBody_AbortsWithoutBufferingWholeBody()
    {
        var stream = new NeverEndingStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = OverCapJsonContent(stream)
        });
        using var http = new HttpClient(handler);
        var client = new CopilotDiscoveryClient(http);

        // A declared Content-Length over the cap is rejected before a single body byte is pulled.
        await Should.ThrowAsync<ResponseContentTooLargeException>(() => client.GetUserAsync("ghu_test_token_value"));
        stream.BytesRead.ShouldBe(0L);
    }

    [Fact]
    public async Task GetUserAsync_UnboundedNoLengthBody_AbortsMidFlight()
    {
        var stream = new NeverEndingStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No Content-Length (chunked / lying endpoint): the streaming read itself must abort.
            Content = new StreamContent(stream)
        });
        using var http = new HttpClient(handler);
        var client = new CopilotDiscoveryClient(http);

        await Should.ThrowAsync<ResponseContentTooLargeException>(() => client.GetUserAsync("ghu_test_token_value"));
        // Bounded to roughly one chunk past the cap, never the full (infinite) body.
        stream.BytesRead.ShouldBeGreaterThan(0L);
        stream.BytesRead.ShouldBeLessThan(BoundedHttpContent.DefaultMaxResponseBytes + (10L * 1024 * 1024));
    }

    [Fact]
    public async Task GetModelsAsync_OverCapBody_AbortsWithoutBufferingWholeBody()
    {
        var stream = new NeverEndingStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = OverCapJsonContent(stream)
        });
        using var http = new HttpClient(handler);
        var client = new CopilotDiscoveryClient(http);

        await Should.ThrowAsync<ResponseContentTooLargeException>(
            () => client.GetModelsAsync("https://api.example.githubcopilot.com/", "tid=fake_session_token"));
        stream.BytesRead.ShouldBe(0L);
    }

    [Fact]
    public async Task GetUserAsync_NormalBody_StillDeserializesAfterBounding()
    {
        // Regression guard: bounding must not change deserialization semantics for legitimate bodies
        // (the discovery client passes its own CamelCase JsonOptions to the bounded reader).
        var fixtureJson = await File.ReadAllTextAsync(Path.Combine(FixturesDir, "user-enterprise.json"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixtureJson, System.Text.Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var client = new CopilotDiscoveryClient(http);

        var info = await client.GetUserAsync("ghu_test_token_value");

        info.Login.ShouldBe("octocat");
        info.CopilotPlan.ShouldBe("enterprise");
        info.Endpoints!.Api.ShouldBe("https://api.example.githubcopilot.com");
    }

    [Fact]
    public async Task GetModelsAsync_NormalBody_StillDeserializesAfterBounding()
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
        resp.Data.ShouldContain(m => m.Id == "claude-sonnet-4.5");
    }

    // Builds a response body whose declared Content-Length is over the bounded-read cap. Backed by
    // an infinite stream so any unbounded read would hang rather than pass.
    private static StreamContent OverCapJsonContent(Stream backing)
    {
        var content = new StreamContent(backing);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = BoundedHttpContent.DefaultMaxResponseBytes + 1;
        return content;
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

    /// <summary>
    /// A read stream that returns bytes forever -- stands in for a hostile endpoint streaming an
    /// unbounded body. Records how many bytes were actually pulled so a test can prove the bounded
    /// reader aborted (or rejected up front) instead of draining it.
    /// </summary>
    private sealed class NeverEndingStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'a');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
