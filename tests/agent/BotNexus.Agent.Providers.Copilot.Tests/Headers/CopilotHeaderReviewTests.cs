using System.Collections;
using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

public sealed class CopilotHeaderReviewTests
{
    private static readonly CopilotHeaderScope Scope = new("work", "generation");
    private const string MessagesBody = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"test\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":0}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    private const string ResponsesBody = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\",\"output\":[]}}\n\n";

    [Theory]
    [InlineData("rem=80")]
    [InlineData("rem=malformed")]
    public async Task Messages_EffortRetry_LaterObservationWins(string firstQuota)
    {
        var store = new CopilotHeaderQuotaStore();
        var handler = new SequenceHandler(
            Response(HttpStatusCode.BadRequest, "output_config.effort \"max\" is not supported by model test; supported values: [low, medium, high]", firstQuota),
            Response(HttpStatusCode.OK, MessagesBody, "rem=20"));
        using var client = new HttpClient(handler);
        var result = await new CopilotMessagesProvider(client, headerSink: store).Stream(Model("messages"), Context(),
            new CopilotMessagesOptions { ApiKey = "synthetic", ThinkingEnabled = true, Effort = "max", Metadata = Metadata() })
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop);
        handler.Calls.ShouldBe(2);
        store.GetLatest(Scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(20m);
        store.GetLatest(Scope, CopilotQuotaDimension.Chat)?.ObservationOrder.ShouldBe(2);
    }

    [Theory]
    [InlineData("rem=80")]
    [InlineData("rem=malformed")]
    public async Task Responses_WebSocketFallback_LaterObservationWins(string firstQuota)
    {
        var store = new CopilotHeaderQuotaStore();
        var handler = new SequenceHandler(Response(HttpStatusCode.OK, ResponsesBody, "rem=20"));
        using var client = new HttpClient(handler);
        var socket = new FaultSocket(503, () => Headers(firstQuota));
        var result = await new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, socket, headerSink: store)
            .Stream(Model("responses"), Context(), SocketOptions()).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop);
        handler.Calls.ShouldBe(1);
        store.GetLatest(Scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(20m);
        store.GetLatest(Scope, CopilotQuotaDimension.Chat)?.ObservationOrder.ShouldBe(2);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task Responses_ThrowingHeaderProperty_PreservesHandshakeAuthAndFallback(int status)
    {
        var store = new CopilotHeaderQuotaStore();
        var handler = new SequenceHandler(Response(HttpStatusCode.OK, ResponsesBody, "rem=20"));
        using var client = new HttpClient(handler);
        var socket = new FaultSocket(status, () => throw new InvalidOperationException("header property fault"));
        var result = await new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, socket, headerSink: store)
            .Stream(Model("responses"), Context(), SocketOptions()).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        handler.Calls.ShouldBe(status == 503 ? 1 : 0);
        result.StopReason.ShouldBe(status == 503 ? StopReason.Stop : StopReason.Error);
        if (status != 503)
        {
            result.ErrorMessage.ShouldNotBeNull();
            result.ErrorMessage.ShouldContain(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.ErrorMessage.ShouldNotContain("header property fault");
        }
    }

    [Theory]
    [InlineData("enumerator")]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("dispose")]
    public async Task Responses_ThrowingHeaderEnumerable_PreservesSuccessfulModelCall(string fault)
    {
        using var client = new HttpClient(new SequenceHandler());
        var socket = new FaultSocket(null, () => new Dictionary<string, IEnumerable<string>> { ["x-quota-snapshot-chat"] = new FaultValues(fault) });
        var result = await new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, socket, headerSink: new CopilotHeaderQuotaStore())
            .Stream(Model("responses"), Context(), SocketOptions()).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop);
        socket.Sends.ShouldBe(1);
    }

    [Fact]
    public async Task Responses_ThrowingSink_PreservesSuccessfulModelCall()
    {
        using var client = new HttpClient(new SequenceHandler());
        var socket = new FaultSocket(null, () => Headers("rem=20"));
        var result = await new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, socket, headerSink: new ThrowingSink())
            .Stream(Model("responses"), Context(), SocketOptions()).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop);
        socket.Sends.ShouldBe(1);
    }

    [Fact]
    public void Capture_NullAndThrowingHeaderAccess_AreNoOps()
    {
        var capture = CopilotHeaderCapture.Begin(new CopilotHeaderQuotaStore(), Model("messages"), new StreamOptions { Metadata = Metadata() });
        Should.NotThrow(() => capture.Observe((HttpResponseMessage?)null));
        Should.NotThrow(() => capture.Observe((IReadOnlyDictionary<string, IEnumerable<string>>?)null));
        Should.NotThrow(() => capture.Observe(() => throw new InvalidOperationException("header getter")));
        // A disabled observer must not even evaluate the transport's header getter.
        Should.NotThrow(() => CopilotHeaderCapture.Begin(null, Model("messages"), null)
            .Observe(() => throw new InvalidOperationException("disabled header getter")));
    }

    [Fact]
    public void Capture_OlderLogicalRequestRetry_CannotOverwriteNewerLogicalRequest()
    {
        var store = new CopilotHeaderQuotaStore();
        var options = new StreamOptions { Metadata = Metadata() };
        var older = CopilotHeaderCapture.Begin(store, Model("messages"), options);
        var newer = CopilotHeaderCapture.Begin(store, Model("messages"), options);
        older.Observe(Headers("rem=80"));
        newer.Observe(Headers("rem=20"));
        older.Observe(Headers("rem=60"));
        older.Observe(Headers("rem=40"));
        store.GetLatest(Scope, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(20m);
    }

    [Theory]
    [InlineData("ent=-1&totRem=-1", true)]
    [InlineData("ent=-1&totRem=-1&rem=100", true)]
    [InlineData("ent=300&totRem=20", false)]
    [InlineData("ent=-2&totRem=-2", null)]
    [InlineData("ent=-1", null)]
    [InlineData("totRem=-1", null)]
    [InlineData("ent=-1&totRem=20", null)]
    [InlineData("ent=-1&ent=-1&totRem=-1", null)]
    [InlineData("rem=50", null)]
    public void Parse_OnlyEstablishedSentinelPair_IsExplicitUnlimited(string raw, bool? expected)
    {
        var quota = CopilotHeaderQuotaParser.Parse(raw);
        quota.IsUnlimited.ShouldBe(expected);
        if (expected == true)
        {
            quota.Entitlement.ShouldBeNull();
            quota.TotalRemainingCount.ShouldBeNull();
        }
    }

    private static LlmModel Model(string api) => new("claude-opus-4.6", "claude-opus-4.6", "github-copilot-" + api, "work",
        "https://api.githubcopilot.com", true, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
    private static Context Context() => new("test", [new UserMessage("hello", 1)]);
    private static Dictionary<string, object> Metadata() => new() { [CopilotHeaderScope.MetadataKey] = Scope };
    private static CopilotResponsesOptions SocketOptions() => new() { ApiKey = "synthetic", Metadata = Metadata(), TransportPreference = CopilotResponsesTransportPreference.WebSocket };
    private static IReadOnlyDictionary<string, IEnumerable<string>> Headers(string value) => new Dictionary<string, IEnumerable<string>> { ["x-quota-snapshot-chat"] = [value] };
    private static HttpResponseMessage Response(HttpStatusCode status, string body, string quota)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Headers.TryAddWithoutValidation("x-quota-snapshot-chat", quota);
        return response;
    }
    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Calls++;
            return Task.FromResult(responses[index]);
        }
    }
    private sealed class FaultSocket(int? status, Func<IReadOnlyDictionary<string, IEnumerable<string>>> headers) : ICopilotResponsesWebSocketTransport
    {
        public int Sends { get; private set; }
        public IReadOnlyDictionary<string, IEnumerable<string>> ResponseHeaders => headers();
        public CopilotResponsesCloseFrame? LastClose => null;
        public ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> requestHeaders, CancellationToken cancellationToken)
            => status is int code ? ValueTask.FromException(new CopilotResponsesWebSocketHandshakeException(code, "original handshake failure", null)) : ValueTask.CompletedTask;
        public ValueTask SendAsync(string payload, CancellationToken cancellationToken) { Sends++; return ValueTask.CompletedTask; }
        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("{\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\",\"output\":[]}}");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ThrowingSink : ICopilotHeaderSink
    {
        public void Observe(CopilotHeaderObservation observation) => throw new InvalidOperationException("sink fault");
        public void ObserveLegacyUnattributed() => throw new InvalidOperationException("sink fault");
    }
    private sealed class FaultValues(string fault) : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => fault == "enumerator" ? throw new InvalidOperationException("enumerator fault") : new Iterator(fault);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Iterator(string fault) : IEnumerator<string>
        {
            private int _index;
            public string Current => fault == "current" ? throw new InvalidOperationException("current fault") : "rem=20";
            object IEnumerator.Current => Current;
            public bool MoveNext() => fault == "move" ? throw new InvalidOperationException("move fault") : ++_index == 1;
            public void Reset() => throw new NotSupportedException();
            public void Dispose() { if (fault == "dispose") throw new InvalidOperationException("dispose fault"); }
        }
    }
}
