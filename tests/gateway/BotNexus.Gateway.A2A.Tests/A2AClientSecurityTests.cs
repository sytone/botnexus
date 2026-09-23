using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.A2A;

namespace BotNexus.Gateway.A2A.Tests;

public sealed class A2AClientSecurityTests
{
    private static readonly Uri ApprovedOrigin = new("https://agents.example.test/");

    [Theory]
    [InlineData("ftp://agents.example.test/")]
    [InlineData("https://user@agents.example.test/")]
    [InlineData("https://agents.example.test/path")]
    [InlineData("https://agents.example.test/?query=1")]
    [InlineData("https://agents.example.test/#fragment")]
    public void Options_InvalidApprovedOrigin_Rejects(string value)
    {
        Action act = () => _ = new A2AClientOptions(new Uri(value));
        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public async Task DiscoverAsync_AlwaysUsesFixedWellKnownCardPath()
    {
        var handler = new RecordingHandler(_ => Card());
        using var client = Client(handler);

        await client.DiscoverAsync(Options(), Deadline());

        handler.Requests.ShouldHaveSingleItem().RequestUri.ShouldBe(
            new Uri("https://agents.example.test/.well-known/agent-card.json"));
    }

    [Theory]
    [InlineData("not a URI")]
    [InlineData("ftp://agents.example.test/report")]
    [InlineData("http://127.0.0.1/report")]
    [InlineData("https://other.example.test/report")]
    [InlineData("https://blocked.example.test/report")]
    public async Task SendMessageAsync_UntrustedArtifactUrl_FailsClosed(string artifactUrl)
    {
        var handler = new RpcHandler(artifactUrl: artifactUrl);
        using var client = Client(handler);
        var options = Options() with { AdditionalBlockedHosts = ["blocked.example.test"] };

        var result = await client.SendMessageAsync(options, new A2AMessage("Do work."), Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.TransportFailed);
        result.Artifacts.ShouldBeEmpty();
        result.Error.ShouldBe("The A2A response failed protocol validation.");
    }

    [Fact]
    public async Task SendMessageAsync_ValidSameOriginArtifact_IsExposed()
    {
        using var client = Client(new RpcHandler());

        var result = await client.SendMessageAsync(Options(), new A2AMessage("Do work."), Deadline());

        result.Artifacts.ShouldHaveSingleItem().Uri.ShouldBe(
            new Uri("https://agents.example.test/artifacts/report.json"));
    }

    [Fact]
    public async Task SendMessageAsync_CompletedArtifactText_BecomesBoundedSummary()
    {
        using var client = Client(new RpcHandler(responseFactory: id =>
            "{\"jsonrpc\":\"2.0\",\"id\":\"" + id
            + "\",\"result\":{\"task\":{\"id\":\"task\",\"status\":{\"state\":\"TASK_STATE_COMPLETED\"},"
            + "\"artifacts\":[{\"artifactId\":\"answer\",\"parts\":[{\"text\":\"synthetic answer\"}]}]}}}"));

        var result = await client.SendMessageAsync(
            Options() with { MaxSummaryCharacters = 9 },
            new A2AMessage("Do work."),
            Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.Completed);
        result.Summary.ShouldBe("synthetic…");
        result.Artifacts.ShouldBeEmpty();
    }

    [Fact]
    public void OwnedClient_HasInfiniteTransportTimeout()
    {
        using var client = new A2AClient();
        client.OwnedTransportTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task SendMessageAsync_UnrelatedOperationCancellation_IsTransportFailure()
    {
        using var client = Client(new ThrowingHandler(new OperationCanceledException("remote text")));

        var result = await client.SendMessageAsync(Options(), new A2AMessage("Do work."), Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.TransportFailed);
        result.Error.ShouldBe("The A2A transport failed.");
        result.Error.ShouldNotBeNull().ShouldNotContain("remote text");
    }

    [Fact]
    public async Task SendMessageAsync_HttpFailure_DoesNotExposeExceptionText()
    {
        using var client = Client(new ThrowingHandler(new HttpRequestException("secret endpoint detail")));

        var result = await client.SendMessageAsync(Options(), new A2AMessage("Do work."), Deadline());

        result.Error.ShouldBe("The A2A transport failed.");
        result.Error.ShouldNotBeNull().ShouldNotContain("secret endpoint detail");
    }

    [Fact]
    public async Task SendMessageAsync_JsonRpcError_DoesNotExposeRemoteText()
    {
        using var client = Client(new RpcHandler(responseFactory: id =>
            "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"error\":{\"code\":-32603,\"message\":\"remote secret detail\"}}"));

        var result = await client.SendMessageAsync(Options(), new A2AMessage("Do work."), Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.Failed);
        result.Error.ShouldBe("The remote A2A agent reported an error.");
        result.Error.ShouldNotBeNull().ShouldNotContain("remote secret detail");
    }

    [Theory]
    [InlineData("TASK_STATE_SECRET_REMOTE_VALUE")]
    [InlineData("")]
    public async Task SendMessageAsync_InvalidRemoteState_DoesNotExposeState(string state)
    {
        using var client = Client(new RpcHandler(state: state));

        var result = await client.SendMessageAsync(Options(), new A2AMessage("Do work."), Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.TransportFailed);
        result.Error.ShouldBe("The A2A response failed protocol validation.");
        if (state.Length > 0)
            result.Error.ShouldNotBeNull().ShouldNotContain(state);
    }

    [Theory]
    [InlineData("1.0", true, true, false)]
    [InlineData("2.0", false, true, false)]
    [InlineData("2.0", true, true, true)]
    [InlineData("2.0", true, false, false)]
    public async Task SendMessageAsync_InvalidJsonRpcEnvelope_FailsClosed(
        string jsonrpc, bool matchingId, bool includeResult, bool includeError)
    {
        using var client = Client(new RpcHandler(responseFactory: requestId =>
        {
            var id = matchingId ? requestId : "wrong-id";
            var result = includeResult
                ? ",\"result\":{\"task\":{\"id\":\"task\",\"status\":{\"state\":\"TASK_STATE_COMPLETED\"},\"artifacts\":[]}}"
                : string.Empty;
            var error = includeError ? ",\"error\":{\"code\":-1,\"message\":\"bad\"}" : string.Empty;
            return "{\"jsonrpc\":\"" + jsonrpc + "\",\"id\":\"" + id + "\"" + result + error + "}";
        }));

        var result = await client.SendMessageAsync(Options(), new A2AMessage("Do work."), Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.TransportFailed);
        result.Error.ShouldBe("The A2A response failed protocol validation.");
    }

    [Fact]
    public async Task SendMessageAsync_EachCallUsesFreshJsonRpcId()
    {
        var handler = new RpcHandler();
        using var client = Client(handler);

        _ = await client.SendMessageAsync(Options(), new A2AMessage("First."), Deadline());
        _ = await client.SendMessageAsync(Options(), new A2AMessage("Second."), Deadline());

        handler.RequestIds.Count.ShouldBe(2);
        handler.RequestIds.ShouldBeUnique();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessageAsync_EmptyObjective_RejectsBeforeRemoteCall(string objective)
    {
        var handler = new CountingHandler();
        using var client = Client(handler);

        Func<Task> act = async () => _ = await client.SendMessageAsync(Options(), new A2AMessage(objective), Deadline());

        await Should.ThrowAsync<ArgumentException>(act);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendMessageAsync_OversizedTextContextOrRequest_RejectsBeforeRemoteCall()
    {
        var handler = new CountingHandler();
        using var client = Client(handler);
        var options = Options() with
        {
            MaxMessageTextCharacters = 8,
            MaxContextIdCharacters = 4,
            MaxRequestBytes = 128
        };

        await RejectWithoutCall(new A2AMessage("123456789"), options, handler, client);
        await RejectWithoutCall(new A2AMessage("ok", "12345"), options, handler, client);
        await RejectWithoutCall(new A2AMessage("12345678", "1234"), options, handler, client);
    }

    [Fact]
    public async Task Authentication_RunsSeparatelyAfterDestinationValidation()
    {
        var calls = new List<string>();
        var handler = new RpcHandler();
        using var client = Client(handler);
        var options = Options() with
        {
            AuthenticateDiscoveryAsync = (request, _) =>
            {
                request.Headers.Add("X-Auth-Stage", "discovery");
                calls.Add("discovery");
                return ValueTask.CompletedTask;
            },
            AuthenticateSubmissionAsync = (request, _) =>
            {
                request.Headers.Add("X-Auth-Stage", "submission");
                calls.Add("submission");
                return ValueTask.CompletedTask;
            }
        };

        _ = await client.SendMessageAsync(options, new A2AMessage("Do work."), Deadline());

        calls.ShouldBe(["discovery", "submission"]);
        handler.AuthenticationStages.ShouldBe(["discovery", "submission"]);
    }

    [Fact]
    public async Task SendMessageAsync_RejectedDiscoveredEndpoint_NeverInvokesSubmissionAuthentication()
    {
        var authCalls = 0;
        var handler = new RecordingHandler(_ => Json(
            """{"name":"Synthetic","supportedInterfaces":[{"url":"https://blocked.example.test/a2a","protocolBinding":"JSONRPC","protocolVersion":"1.0"}]}"""));
        using var client = Client(handler);
        var options = Options() with
        {
            AdditionalBlockedHosts = ["blocked.example.test"],
            AuthenticateSubmissionAsync = (_, _) =>
            {
                authCalls++;
                return ValueTask.CompletedTask;
            }
        };

        var result = await client.SendMessageAsync(options, new A2AMessage("Do work."), Deadline());

        result.Outcome.ShouldBe(A2ATerminalOutcome.TransportFailed);
        authCalls.ShouldBe(0);
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://blocked.example.test/")]
    public async Task DiscoverAsync_RejectedDestination_NeverInvokesAuthentication(string origin)
    {
        var authCalls = 0;
        using var client = Client(new CountingHandler());
        var options = new A2AClientOptions(new Uri(origin))
        {
            AdditionalBlockedHosts = ["blocked.example.test"],
            AuthenticateDiscoveryAsync = (_, _) =>
            {
                authCalls++;
                return ValueTask.CompletedTask;
            }
        };

        Func<Task> act = () => client.DiscoverAsync(options, Deadline());
        await Should.ThrowAsync<A2AProtocolException>(act);
        authCalls.ShouldBe(0);
    }

    private static async Task RejectWithoutCall(
        A2AMessage message,
        A2AClientOptions options,
        CountingHandler handler,
        A2AClient client)
    {
        Func<Task> act = async () => _ = await client.SendMessageAsync(options, message, Deadline());
        await Should.ThrowAsync<ArgumentException>(act);
        handler.RequestCount.ShouldBe(0);
    }

    private static A2AClientOptions Options() => new(ApprovedOrigin);
    private static DateTimeOffset Deadline() => DateTimeOffset.UtcNow.AddMinutes(1);
    private static A2AClient Client(HttpMessageHandler handler) => new(new HttpClient(handler));

    private static HttpResponseMessage Card() => Json(
        """{"name":"Synthetic","supportedInterfaces":[{"url":"https://agents.example.test/a2a","protocolBinding":"JSONRPC","protocolVersion":"1.0"}]}""");

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(Card());
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class RpcHandler(
        string state = "TASK_STATE_COMPLETED",
        string artifactUrl = "https://agents.example.test/artifacts/report.json",
        Func<string, string>? responseFactory = null) : HttpMessageHandler
    {
        public List<string> RequestIds { get; } = [];
        public List<string> AuthenticationStages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthenticationStages.Add(
                request.Headers.TryGetValues("X-Auth-Stage", out var values) ? values.Single() : "none");
            if (request.Method == HttpMethod.Get)
                return Card();

            var json = await request.Content.ShouldNotBeNull().ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var id = document.RootElement.GetProperty("id").GetString().ShouldNotBeNull();
            RequestIds.Add(id);
            if (responseFactory is not null)
                return Json(responseFactory(id));

            return Json("{\"jsonrpc\":\"2.0\",\"id\":\"" + id
                + "\",\"result\":{\"task\":{\"id\":\"task\",\"contextId\":\"ctx\",\"status\":{\"state\":\""
                + state + "\",\"message\":{\"parts\":[{\"text\":\"summary\"}]}},\"artifacts\":[{\"artifactId\":\"artifact\",\"name\":\"report\",\"parts\":[{\"url\":\""
                + artifactUrl + "\",\"mediaType\":\"application/json\"}]}]}}}");
        }
    }
}
