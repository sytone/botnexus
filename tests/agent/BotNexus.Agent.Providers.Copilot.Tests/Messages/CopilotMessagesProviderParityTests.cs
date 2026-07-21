using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Phase 1a — proves that the carved-out <see cref="CopilotMessagesProvider"/>
/// emits a byte-identical outbound request to the legacy path
/// (<see cref="AnthropicProvider"/> driven against a github-copilot model with
/// Bearer auth). This is the deployment-safety pivot for Phase 1b: when the
/// model registry flips the Claude entries from <c>anthropic-messages</c> to
/// <c>github-copilot-messages</c>, the wire contract must not change.
///
/// The test purposefully reuses the Phase 0a snapshot
/// (<c>Fixtures/Requests/messages/claude-haiku-4.5/haiku-thinking-tool.json</c>)
/// so any drift between the two providers, or between either provider and the
/// committed contract, surfaces here.
/// </summary>
public class CopilotMessagesProviderParityTests
{
    private static readonly string SnapshotRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Requests");

    private static readonly string[] InterestingHeaders =
    {
        "Authorization",
        "anthropic-version",
        "anthropic-beta",
    };

    [Fact]
    public void Api_IsGitHubCopilotMessages()
    {
        var provider = new CopilotMessagesProvider(new HttpClient(new NoOpHandler()));
        provider.Api.ShouldBe("github-copilot-messages");
    }

    [Fact]
    public async Task Stream_HaikuWithToolCatalogue_MatchesAnthropicCopilotModeBody()
    {
        var (copilotHandler, anthropicHandler) = await DriveBothProvidersAsync();

        var copilotBody = Normalise(copilotHandler.RequestBody!);
        var anthropicBody = Normalise(anthropicHandler.RequestBody!);

        copilotBody.ShouldBe(
            anthropicBody,
            "CopilotMessagesProvider must emit a request body that is byte-identical to " +
            "AnthropicProvider's Copilot-mode output. A diff here will become a user-visible " +
            "behaviour change the moment BuiltInModels flips Claude entries to github-copilot-messages.");
    }

    [Fact]
    public async Task Stream_HaikuWithToolCatalogue_MatchesPhase0aSnapshot()
    {
        var (copilotHandler, _) = await DriveBothProvidersAsync();

        copilotHandler.RequestUri!.AbsoluteUri.ShouldBe(
            "https://api.enterprise.githubcopilot.com/v1/messages");

        var envelope = BuildEnvelope(copilotHandler);
        var actualJson = SerializeStable(envelope);

        var snapshotPath = Path.Combine(SnapshotRoot, "messages", "claude-haiku-4.5", "haiku-thinking-tool.json");
        File.Exists(snapshotPath).ShouldBeTrue($"missing Phase 0a snapshot: {snapshotPath}");

        var expectedJson = await File.ReadAllTextAsync(snapshotPath);
        Normalise(actualJson).ShouldBe(
            Normalise(expectedJson),
            "CopilotMessagesProvider diverged from the Phase 0a snapshot. The carved-out " +
            "provider must produce the same wire request as the legacy Anthropic-with-Copilot-auth path.");
    }

    [Fact]
    public async Task Stream_AppliesBearerAuth_AndCopilotDynamicHeaders()
    {
        var (handler, _) = await DriveBothProvidersAsync();

        handler.RequestHeaders.TryGetValue("Authorization", out var auth).ShouldBeTrue();
        auth.ShouldStartWith("Bearer ");

        // Two of the Copilot dynamic headers that are unconditionally applied to every
        // Copilot request (Copilot-Vision-Request is only set when images are present).
        handler.RequestHeaders.ShouldContainKey("X-Initiator");
        handler.RequestHeaders.ShouldContainKey("Openai-Intent");
    }

    [Fact]
    public async Task Stream_Gpt56_RemovesCopilotChunkCrLf_WhilePreservingFormattingPayload()
    {
        const string expected = """
            ## Formatting Test

            This has **bold**, *italic*, and `inline code`.

            [BotNexus](https://github.com/Sytone/botnexus)

            ```powershell
            botnexus gateway status
            ```

            | Component | Status |
            |---|---|
            | Gateway | Running |
            """;
        var fragments = new[]
        {
            "##", " Formatting", " Test\n\nThis", " has", " **", "bold", "**, *italic*, and `inline code`.\n\n[",
            "BotNexus](https://github.com/Sytone/botnexus)\n\n```powershell\nbotnexus gateway status\n```\n\n",
            "| Component | Status |\n|---|---|\n| Gateway | Running |"
        };
        var sse = BuildTextSse(fragments.Select(fragment => "\r\n" + fragment));
        var provider = new CopilotMessagesProvider(new HttpClient(new RecordingHandler(_ => SseResponse(sse))));
        var model = BuildModel() with { Id = "gpt-5.6-sol", Name = "gpt-5.6-sol" };

        var result = await provider.Stream(
                model,
                BuildContext(),
                new CopilotMessagesOptions { ApiKey = "test-copilot-token" })
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.Content.OfType<TextContent>().Single().Text.ShouldBe(expected);
    }

    [Fact]
    public async Task Stream_NonGpt56_PreservesLeadingCrLfVerbatim()
    {
        var sse = BuildTextSse(["\r\nintentional"]);
        var provider = new CopilotMessagesProvider(new HttpClient(new RecordingHandler(_ => SseResponse(sse))));

        var result = await provider.Stream(
                BuildModel(),
                BuildContext(),
                new CopilotMessagesOptions { ApiKey = "test-copilot-token" })
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.Content.OfType<TextContent>().Single().Text.ShouldBe("\r\nintentional");
    }

    private static async Task<(RecordingHandler CopilotHandler, RecordingHandler AnthropicHandler)> DriveBothProvidersAsync()
    {
        var copilotHandler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var copilotProvider = new CopilotMessagesProvider(new HttpClient(copilotHandler));

        var anthropicHandler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var anthropicProvider = new AnthropicProvider(new HttpClient(anthropicHandler));

        var model = BuildModel();
        var context = BuildContext();

        var copilotOpts = new CopilotMessagesOptions
        {
            ApiKey = "test-copilot-token",
            MaxTokens = 4096,
            CacheRetention = CacheRetention.Short,
        };
        var anthropicOpts = new AnthropicOptions
        {
            ApiKey = "test-copilot-token",
            MaxTokens = 4096,
            CacheRetention = CacheRetention.Short,
        };

        var copilotStream = copilotProvider.Stream(model, context, copilotOpts);
        _ = await copilotStream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var anthropicStream = anthropicProvider.Stream(model, context, anthropicOpts);
        _ = await anthropicStream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        return (copilotHandler, anthropicHandler);
    }

    private static LlmModel BuildModel() => new(
        Id: "claude-haiku-4.5",
        Name: "claude-haiku-4.5",
        Api: "anthropic-messages",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: true,
        Input: ["text", "image"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200000,
        MaxTokens: 16384);

    private static Context BuildContext()
    {
        const long fixedTimestamp = 1_700_000_000_000L;
        return new Context(
            SystemPrompt: "You are a helpful assistant operating inside BotNexus tests. " +
                          "Reply concisely and use tools when asked.",
            Messages:
            [
                new UserMessage(new UserMessageContent("List the first three prime numbers."), fixedTimestamp),
            ],
            Tools:
            [
                new Tool(
                    Name: "list_primes",
                    Description: "Returns the first N prime numbers.",
                    Parameters: JsonDocument.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "count": { "type": "integer", "minimum": 1, "maximum": 100 }
                          },
                          "required": ["count"]
                        }
                        """).RootElement.Clone()),
            ]);
    }

    private const string MinimalSse =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_01FixtureMessage00000001\"}}\n" +
        "\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n" +
        "\n";

    private static string BuildTextSse(IEnumerable<string> fragments)
    {
        var builder = new StringBuilder()
            .AppendLine("event: message_start")
            .AppendLine("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}")
            .AppendLine()
            .AppendLine("event: content_block_start")
            .AppendLine("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}")
            .AppendLine();
        foreach (var fragment in fragments)
        {
            builder.AppendLine("event: content_block_delta")
                .Append("data: ")
                .AppendLine(JsonSerializer.Serialize(new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "text_delta", text = fragment }
                }))
                .AppendLine();
        }
        return builder
            .AppendLine("event: content_block_stop")
            .AppendLine("data: {\"type\":\"content_block_stop\",\"index\":0}")
            .AppendLine()
            .AppendLine("event: message_stop")
            .AppendLine("data: {\"type\":\"message_stop\"}")
            .AppendLine()
            .ToString();
    }

    private static JsonObject BuildEnvelope(RecordingHandler handler)
    {
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();

        var headers = new JsonObject();
        foreach (var name in InterestingHeaders.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (handler.RequestHeaders.TryGetValue(name, out var value))
            {
                if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var scheme = value.Split(' ', 2)[0];
                    headers[name] = $"{scheme} <redacted>";
                }
                else
                {
                    headers[name] = value;
                }
            }
        }

        return new JsonObject
        {
            ["method"] = handler.RequestMethod ?? "POST",
            ["path"] = handler.RequestUri!.AbsolutePath,
            ["headers"] = headers,
            ["body"] = body,
        };
    }

    private static string SerializeStable(JsonNode node)
        => JsonSerializer.Serialize(node, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private static string Normalise(string text)
        => text.Replace("\r\n", "\n").TrimEnd();

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? RequestMethod { get; private set; }
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        public Dictionary<string, string> RequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method.Method;
            RequestUri = request.RequestUri;
            RequestHeaders = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
