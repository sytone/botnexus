using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Phase 0c of the Copilot provider carve-out (#810): negative regression.
///
/// Pins the outbound /v1/messages envelope <see cref="AnthropicProvider"/>
/// emits today on the <b>non-Copilot</b> paths (direct Anthropic API key and
/// OAuth tokens for Claude Code). When Phase 1 deletes the
/// <c>AuthMode.Copilot</c> branch from <see cref="AnthropicProvider"/>, these
/// snapshots must stay byte-identical — they are the safety net for direct
/// Anthropic users who never touch Copilot.
/// </summary>
public class AnthropicDirectRequestSnapshotTests
{
    private const long FixedTimestamp = 1_700_000_000_000L;

    [Fact]
    public Task Stream_DirectAnthropic_ApiKeyAuth_MatchesSnapshot()
        => RunSnapshot(
            apiKey: "sk-ant-api03-redacted",
            snapshotPath: "Fixtures/Requests/messages/claude-sonnet-4/apikey-direct.json");

    [Fact]
    public Task Stream_DirectAnthropic_OAuthAuth_MatchesSnapshot()
        => RunSnapshot(
            apiKey: "sk-ant-oat01-redacted",
            snapshotPath: "Fixtures/Requests/messages/claude-sonnet-4/oauth-direct.json");

    private static async Task RunSnapshot(string apiKey, string snapshotPath)
    {
        var handler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var provider = new AnthropicProvider(new HttpClient(handler));

        var model = new LlmModel(
            Id: "claude-sonnet-4",
            Name: "claude-sonnet-4",
            Api: "anthropic-messages",
            Provider: "anthropic",
            BaseUrl: "https://api.anthropic.com",
            Reasoning: true,
            Input: ["text", "image"],
            Cost: new ModelCost(3.0m, 15.0m, 0.3m, 3.75m),
            ContextWindow: 200000,
            MaxTokens: 12288);

        var toolParams = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "count": { "type": "integer", "minimum": 1, "maximum": 100 }
              },
              "required": ["count"]
            }
            """).RootElement.Clone();

        var context = new Context(
            SystemPrompt: "You are a helpful assistant operating inside BotNexus tests. Reply concisely and use tools when asked.",
            Messages: [new UserMessage(new UserMessageContent("List the first three prime numbers."), FixedTimestamp)],
            Tools: [new Tool("list_primes", "Returns the first N prime numbers.", toolParams)]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = apiKey });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestBody.ShouldNotBeNull();
        handler.LastRequestUri.ShouldNotBeNull();

        var actual = BuildEnvelope(handler);
        var actualJson = SerializeStable(actual);

        var fullPath = Path.Combine(AppContext.BaseDirectory, snapshotPath);
        if (!File.Exists(fullPath))
        {
            throw new Xunit.Sdk.XunitException(
                $"Snapshot file not found: {snapshotPath}\n" +
                "Bootstrap by writing the following content into the file:\n\n" +
                actualJson);
        }

        var expectedJson = await File.ReadAllTextAsync(fullPath);
        Normalise(actualJson).ShouldBe(Normalise(expectedJson));
    }

    private static IDictionary<string, object?> BuildEnvelope(RecordingHandler handler)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in HeadersOfInterest)
        {
            if (!handler.RequestHeaders.TryGetValue(key, out var value)) continue;

            headers[key] = key switch
            {
                _ when string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase) => RedactAuthorization(value),
                _ when string.Equals(key, "x-api-key", StringComparison.OrdinalIgnoreCase) => "<redacted>",
                _ => value,
            };
        }

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var bodyObject = JsonSerializer.Deserialize<JsonElement>(body.RootElement.GetRawText());

        return new Dictionary<string, object?>
        {
            ["method"] = "POST",
            ["path"] = handler.LastRequestUri!.AbsolutePath,
            ["headers"] = headers,
            ["body"] = bodyObject,
        };
    }

    private static string RedactAuthorization(string raw)
    {
        var space = raw.IndexOf(' ');
        return space < 0 ? "<redacted>" : $"{raw[..space]} <redacted>";
    }

    private static readonly string[] HeadersOfInterest =
    [
        "Authorization",
        "x-api-key",
        "anthropic-version",
        "anthropic-beta",
        "user-agent",
        "x-app",
    ];

    private static string SerializeStable(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static string Normalise(string raw) =>
        raw.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private const string MinimalSse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1"}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    private static HttpResponseMessage SseResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public Dictionary<string, string> RequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
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
}
