using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

public sealed class CopilotHeaderCaptureTests
{
    [Theory]
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task Sse_AllProviders_CaptureWithoutActivityBeforeBodyRead(string api)
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work-a", "generation-a");
        var content = new ProbeContent(() => store.GetLatest(scope, CopilotQuotaDimension.Chat).ShouldNotBeNull());
        using var client = new HttpClient(new Handler(content));
        var provider = CreateProvider(api, client, store);
        var registry = new ApiProviderRegistry();
        registry.Register(provider);
        var llm = new LlmClient(registry, new ModelRegistry());
        var result = await llm.Stream(Model(api, "work-a"), Context(), Options(scope)).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Error); // intentional 403, still capture headers first
        content.Reads.ShouldBeGreaterThan(0);
        store.GetLatest(scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(42.5m);
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task Sse_MismatchedScope_IsRejectedRatherThanAttributed(string api)
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work-b", "generation-b");
        using var client = new HttpClient(new Handler(new ProbeContent(() => { })));
        _ = await CreateProvider(api, client, store).Stream(Model(api, "work-a"), Context(), Options(scope)).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        store.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task Sse_NoScope_IsExplicitlyLegacyUnattributed_NotDefaultAccount(string api)
    {
        var store = new CopilotHeaderQuotaStore();
        using var client = new HttpClient(new Handler(new ProbeContent(() => { })));
        _ = await CreateProvider(api, client, store).Stream(Model(api, "github-copilot"), Context(), new StreamOptions { ApiKey = "synthetic" }).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        store.Count.ShouldBe(0);
        store.LegacyUnattributedResponses.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocket_SuccessAndRejectedHandshake_ObservedBeforePayload(bool reject)
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work-a", "generation-a");
        var socket = new Socket(reject, () => store.GetLatest(scope, CopilotQuotaDimension.Chat).ShouldNotBeNull());
        using var client = new HttpClient(new Handler(new ProbeContent(() => { })));
        var provider = new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, socket, headerSink: store);
        var options = new CopilotResponsesOptions
        {
            ApiKey = "synthetic", TransportPreference = CopilotResponsesTransportPreference.WebSocket,
            Metadata = Options(scope).Metadata
        };
        _ = await provider.Stream(Model("responses", "work-a"), Context(), options).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        store.GetLatest(scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(42.5m);
        socket.Sends.ShouldBe(reject ? 0 : 1);
    }

    [Fact]
    public void Capture_AbsentAndMalformedHeaders_UnknownWithoutRawSerialization()
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work", "gen");
        var capture = CopilotHeaderCapture.Begin(store, Model("messages", "work"), Options(scope));
        using var absent = new HttpResponseMessage(HttpStatusCode.OK);
        capture.Observe(absent);
        store.Count.ShouldBe(0);
        var next = CopilotHeaderCapture.Begin(store, Model("messages", "work"), Options(scope));
        using var malformed = new HttpResponseMessage(HttpStatusCode.OK);
        malformed.Headers.TryAddWithoutValidation("x-quota-snapshot-chat", "rem=NaN&secret=do-not-store");
        next.Observe(malformed);
        var latest = store.GetLatest(scope, CopilotQuotaDimension.Chat);
        latest.ShouldNotBeNull();
        latest.Quota.RemainingPercent.ShouldBeNull();
        System.Text.Json.JsonSerializer.Serialize(latest).ShouldNotContain("do-not-store");
    }

    [Fact]
    public void Capture_RequestStartOrder_NotResponseArrivalOrder()
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work", "gen");
        var first = CopilotHeaderCapture.Begin(store, Model("messages", "work"), Options(scope));
        var second = CopilotHeaderCapture.Begin(store, Model("messages", "work"), Options(scope));
        second.Observe(new Dictionary<string, IEnumerable<string>> { ["x-quota-snapshot-chat"] = ["rem=20"] });
        first.Observe(new Dictionary<string, IEnumerable<string>> { ["x-quota-snapshot-chat"] = ["rem=80"] });
        store.GetLatest(scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(20m);
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task Sse_SuccessBody_AndSimpleExecutionMetadata_ComposedCapture(string api)
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work-a", "generation-a");
        var payload = api switch
        {
            "messages" => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
            "completions" => "data: {\"id\":\"test\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            _ => "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\"}}\n\ndata: [DONE]\n\n"
        };
        var content = new ProbeContent(() => store.GetLatest(scope, CopilotQuotaDimension.Chat).ShouldNotBeNull(), payload);
        using var client = new HttpClient(new Handler(content, HttpStatusCode.OK));
        var registry = new ApiProviderRegistry();
        registry.Register(CreateProvider(api, client, store));
        var llm = new LlmClient(registry, new ModelRegistry());
        var result = await llm.StreamSimple(Model(api, "work-a"), Context(), new GenerationOptions(),
            new ProviderExecutionOptions { ApiKey = "synthetic", Metadata = Options(scope).Metadata })
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop);
        content.Reads.ShouldBeGreaterThan(0);
        store.Count.ShouldBe(1);
    }

    [Fact]
    public void Capture_MultiValueAndOversize_AreUnknown_AndFailingSinkIsIsolated()
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work", "gen");
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["x-quota-snapshot-chat"] = ["rem=5", "rem=9"],
            ["x-quota-snapshot-completions"] = [new string('x', 4097)],
            ["x-quota-snapshot-premium_interactions"] = ["rem=25"]
        };
        CopilotHeaderCapture.Begin(store, Model("messages", "work"), Options(scope)).Observe(headers);
        store.GetLatest(scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBeNull();
        store.GetLatest(scope, CopilotQuotaDimension.Completions)?.Quota.RemainingPercent.ShouldBeNull();
        store.GetLatest(scope, CopilotQuotaDimension.PremiumInteractions)?.Quota.RemainingPercent.ShouldBe(25m);
        Should.NotThrow(() => CopilotHeaderCapture.Begin(new ThrowingSink(), Model("messages", "work"), Options(scope)).Observe(headers));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("x-api-key")]
    [InlineData("api-key")]
    public void Capture_CredentialHeaderOverride_FailsClosed(string header)
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work", "gen");
        var options = Options(scope) with { Headers = new() { [header] = "other-credential" } };
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-quota-snapshot-chat", "rem=50");
        CopilotHeaderCapture.Begin(store, Model("messages", "work"), options).Observe(response);
        store.Count.ShouldBe(0);
        store.LegacyUnattributedResponses.ShouldBe(0);
        var model = Model("messages", "work") with { Headers = new Dictionary<string, string> { [header] = "other-credential" } };
        CopilotHeaderCapture.Begin(store, model, Options(scope)).Observe(response);
        store.Count.ShouldBe(0);
    }

    private sealed class ThrowingSink : ICopilotHeaderSink
    {
        public void Observe(CopilotHeaderObservation observation) => throw new InvalidOperationException("sink fault");
        public void ObserveLegacyUnattributed() => throw new InvalidOperationException("sink fault");
    }

    private static IApiProvider CreateProvider(string api, HttpClient client, CopilotHeaderQuotaStore store) => api switch
    {
        "messages" => new CopilotMessagesProvider(client, headerSink: store),
        "completions" => new CopilotCompletionsProvider(client, NullLogger<CopilotCompletionsProvider>.Instance, headerSink: store),
        _ => new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, headerSink: store)
    };
    private static LlmModel Model(string api, string instance) => new("test", "test", "github-copilot-" + api, instance,
        "https://api.githubcopilot.com", false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
    private static Context Context() => new("test", [new UserMessage("hello", 1)]);
    private static StreamOptions Options(CopilotHeaderScope scope) => new()
    {
        ApiKey = "synthetic", Metadata = new() { [CopilotHeaderScope.MetadataKey] = scope }
    };
    private sealed class Handler(HttpContent content, HttpStatusCode status = HttpStatusCode.Forbidden) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = content };
            response.Headers.TryAddWithoutValidation("x-quota-snapshot-chat", "rem=42.5");
            return Task.FromResult(response);
        }
    }
    private sealed class ProbeContent(Action beforeRead, string payload = "{\"error\":\"rejected\"}") : HttpContent
    {
        public int Reads { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            beforeRead(); Reads++;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(payload));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class Socket(bool reject, Action beforeSend) : ICopilotResponsesWebSocketTransport
    {
        public int Sends { get; private set; }
        public CopilotResponsesCloseFrame? LastClose => null;
        public IReadOnlyDictionary<string, IEnumerable<string>> ResponseHeaders { get; } = new Dictionary<string, IEnumerable<string>> { ["x-quota-snapshot-chat"] = ["rem=42.5"] };
        public ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
            => reject ? ValueTask.FromException(new CopilotResponsesWebSocketHandshakeException(403, "rejected", null)) : ValueTask.CompletedTask;
        public ValueTask SendAsync(string payload, CancellationToken cancellationToken) { beforeSend(); Sends++; return ValueTask.CompletedTask; }
        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("{\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\"}}");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
