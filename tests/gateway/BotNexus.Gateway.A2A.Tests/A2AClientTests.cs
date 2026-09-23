using System.Net;
using System.Text;
using BotNexus.Gateway.A2A;

namespace BotNexus.Gateway.A2A.Tests;

public sealed class A2AClientTests
{
    [Fact]
    public async Task SendMessageAsync_SyntheticAgent_CompletesWithTypedResult()
    {
        var handler = new SyntheticAgentHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new A2AClient(httpClient);
        var options = new A2AClientOptions(new Uri("https://agents.example.test/"));

        var result = await client.SendMessageAsync(
            options,
            new A2AMessage("Summarize the three synthetic records.", "ctx-caller"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        result.Outcome.ShouldBe(A2ATerminalOutcome.Completed);
        result.ContextId.ShouldBe("ctx-synthetic");
        result.TaskId.ShouldBe("task-synthetic");
        result.Summary.ShouldBe("Three synthetic records were summarized.");
        result.Artifacts.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://agents.example.test/artifacts/report.json"));
        var provenance = result.Provenance.ShouldNotBeNull();
        provenance.AgentName.ShouldBe("Example Research Agent");
        provenance.AgentCardUri.ShouldBe(new Uri("https://agents.example.test/.well-known/agent-card.json"));
        result.Usage.ShouldNotBeNull().InputTokens.ShouldBe(17);
        handler.LastRequestJson.ShouldContain("\"method\":\"SendMessage\"");
        handler.LastRequestJson.ShouldContain("Summarize");
        handler.RequestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("TASK_STATE_INPUT_REQUIRED", A2ATerminalOutcome.InputRequired)]
    [InlineData("TASK_STATE_FAILED", A2ATerminalOutcome.Failed)]
    [InlineData("TASK_STATE_CANCELED", A2ATerminalOutcome.Cancelled)]
    [InlineData("TASK_STATE_REJECTED", A2ATerminalOutcome.PolicyBlocked)]
    public async Task SendMessageAsync_RemoteTerminalState_MapsDistinctOutcome(string state, A2ATerminalOutcome expected)
    {
        using var httpClient = new HttpClient(new SyntheticAgentHandler(state));
        using var client = new A2AClient(httpClient);

        var result = await client.SendMessageAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            new A2AMessage("Do bounded work."),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        result.Outcome.ShouldBe(expected);
    }

    [Fact]
    public async Task SendMessageAsync_ExpiredDeadline_DoesNotContactRemoteAgent()
    {
        var handler = new SyntheticAgentHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new A2AClient(httpClient);

        var result = await client.SendMessageAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            new A2AMessage("Too late."),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            CancellationToken.None);

        result.Outcome.ShouldBe(A2ATerminalOutcome.DeadlineExpired);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendMessageAsync_TransportFailure_ReturnsDistinctOutcome()
    {
        using var httpClient = new HttpClient(new ThrowingHandler());
        using var client = new A2AClient(httpClient);

        var result = await client.SendMessageAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            new A2AMessage("Do work."),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        result.Outcome.ShouldBe(A2ATerminalOutcome.TransportFailed);
        result.Usage.ShouldBeNull();
    }


    [Fact]
    public async Task SendMessageAsync_CallerCancellation_ReturnsDistinctOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var httpClient = new HttpClient(new CancellationAwareHandler());
        using var client = new A2AClient(httpClient);

        var result = await client.SendMessageAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            new A2AMessage("Cancel this work."),
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellation.Token);

        result.Outcome.ShouldBe(A2ATerminalOutcome.Cancelled);
    }

    [Theory]
    [InlineData("https://other.example.test/task")]
    [InlineData("http://agents.example.test/task")]
    public async Task DiscoverAsync_InterfaceEscapesApprovedOrigin_FailsClosed(string interfaceUrl)
    {
        var card = $$"""{"name":"Synthetic","supportedInterfaces":[{"url":"{{interfaceUrl}}","protocolBinding":"JSONRPC","protocolVersion":"1.0"}]}""";
        using var httpClient = new HttpClient(new CardOnlyHandler(card));
        using var client = new A2AClient(httpClient);

        Func<Task> act = () => client.DiscoverAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        (await Should.ThrowAsync<A2AProtocolException>(act)).Message.ShouldContain("approved origin");
    }

    [Fact]
    public async Task DiscoverAsync_Redirect_FailsClosed()
    {
        using var httpClient = new HttpClient(new RedirectHandler());
        using var client = new A2AClient(httpClient);

        Func<Task> act = () => client.DiscoverAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        (await Should.ThrowAsync<A2AProtocolException>(act)).Message.ShouldContain("redirect");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"name\":\"Synthetic\",\"supportedInterfaces\":[{\"url\":\"https://agents.example.test/task\",\"protocolBinding\":\"JSONRPC\",\"protocolVersion\":\"2.0\"}]}")]
    [InlineData("{\"name\":\"Synthetic\",\"supportedInterfaces\":[{\"url\":\"https://agents.example.test/task\",\"protocolBinding\":\"GRPC\",\"protocolVersion\":\"1.0\"}]}")]
    public async Task DiscoverAsync_MalformedOrUnsupportedCard_FailsClosed(string card)
    {
        using var httpClient = new HttpClient(new CardOnlyHandler(card));
        using var client = new A2AClient(httpClient);

        Func<Task> act = () => client.DiscoverAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        await Should.ThrowAsync<A2AProtocolException>(act);
    }

    [Fact]
    public async Task DiscoverAsync_OversizedCard_FailsClosed()
    {
        var oversized = "{\"name\":\"" + new string('x', A2AClientOptions.DefaultMaxAgentCardBytes) + "\"}";
        using var httpClient = new HttpClient(new CardOnlyHandler(oversized));
        using var client = new A2AClient(httpClient);

        Func<Task> act = () => client.DiscoverAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        (await Should.ThrowAsync<A2AProtocolException>(act)).Message.ShouldContain("size");
    }

    [Fact]
    public async Task SendMessageAsync_UnknownUsage_RemainsNull()
    {
        using var httpClient = new HttpClient(new SyntheticAgentHandler(includeUsage: false));
        using var client = new A2AClient(httpClient);

        var result = await client.SendMessageAsync(
            new A2AClientOptions(new Uri("https://agents.example.test/")),
            new A2AMessage("Do bounded work."),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        result.Usage.ShouldBeNull();
    }

    private sealed class SyntheticAgentHandler(string state = "TASK_STATE_COMPLETED", bool includeUsage = true) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastRequestJson { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method == HttpMethod.Get)
            {
                return Json("""{"name":"Example Research Agent","supportedInterfaces":[{"url":"https://agents.example.test/a2a","protocolBinding":"JSONRPC","protocolVersion":"1.0"}]}""");
            }

            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            requestJson.ShouldContain("\"method\":\"SendMessage\"");
            requestJson.ShouldContain("\"role\":\"ROLE_USER\"");
            requestJson.ShouldContain("\"returnImmediately\":false");
            request.Headers.GetValues("A2A-Version").ShouldHaveSingleItem().ShouldBe("1.0");
            LastRequestJson = requestJson;
            using var requestDocument = System.Text.Json.JsonDocument.Parse(requestJson);
            var requestId = requestDocument.RootElement.GetProperty("id").GetString().ShouldNotBeNull();
            var usage = includeUsage ? ",\"metadata\":{\"usage\":{\"inputTokens\":17,\"outputTokens\":9}}" : string.Empty;
            var responseJson = "{\"jsonrpc\":\"2.0\",\"id\":\"" + requestId + "\",\"result\":{\"task\":{\"id\":\"task-synthetic\",\"contextId\":\"ctx-synthetic\",\"status\":{\"state\":\"" + state + "\",\"message\":{\"parts\":[{\"text\":\"Three synthetic records were summarized.\"}]}},\"artifacts\":[{\"artifactId\":\"report\",\"name\":\"report.json\",\"parts\":[{\"url\":\"https://agents.example.test/artifacts/report.json\",\"mediaType\":\"application/json\"}]}]" + usage + "}}}";
            return Json(responseJson);
        }
    }

    private sealed class CardOnlyHandler(string card) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(card));
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example.test/card") }
            });
    }


    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Json("{}"));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("synthetic transport failure");
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
