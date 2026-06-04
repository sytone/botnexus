using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Outbound request-shape regression for the Copilot carve-out (#810,
/// Phase 0a). Drives <see cref="AnthropicProvider"/> with a representative
/// Copilot-flavoured <see cref="LlmModel"/> + <see cref="Context"/>, captures
/// the request body and Copilot-specific headers, and compares against a
/// committed snapshot.
///
/// The dedicated <c>CopilotProvider</c> introduced in Phase 1 must produce
/// the same on-the-wire request from the same inputs; a snapshot diff is the
/// canonical signal that the carve-out has accidentally changed the contract.
/// </summary>
public class CopilotRequestSnapshotTests
{
    private static readonly string SnapshotRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Requests");

    // Headers we assert on. The full set Copilot adds is larger but most are
    // either invariant or computed from BaseUrl — these three are the ones
    // the carve-out has the most opportunity to silently change.
    private static readonly string[] InterestingHeaders =
    {
        "Authorization",
        "anthropic-version",
        "anthropic-beta",
    };

    [Fact]
    public async Task Stream_CopilotMessages_HaikuWithThinkingAndToolCatalogue_MatchesSnapshot()
    {
        var handler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var provider = new AnthropicProvider(new HttpClient(handler));

        var model = new LlmModel(
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

        const long fixedTimestamp = 1_700_000_000_000L;
        var context = new Context(
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

        var options = new AnthropicOptions
        {
            ApiKey = "test-copilot-token",
            MaxTokens = 4096,
            CacheRetention = CacheRetention.Short,
        };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestBody.ShouldNotBeNullOrWhiteSpace();
        handler.RequestUri.ShouldNotBeNull();
        handler.RequestUri!.AbsoluteUri.ShouldBe("https://api.enterprise.githubcopilot.com/v1/messages");

        var actual = BuildEnvelope(handler);
        var actualJson = SerializeStable(actual);

        var snapshotPath = Path.Combine(SnapshotRoot, "messages", "claude-haiku-4.5", "haiku-thinking-tool.json");
        File.Exists(snapshotPath).ShouldBeTrue(
            $"missing snapshot: {snapshotPath}\n" +
            "If this is the first run, write the snapshot manually from the captured envelope:\n" +
            actualJson);

        var expectedJson = await File.ReadAllTextAsync(snapshotPath);
        Normalise(actualJson).ShouldBe(Normalise(expectedJson),
            "Outbound Copilot request shape diverged from snapshot. " +
            "If this is an intentional change, update the snapshot file:\n" + snapshotPath);
    }

    private const string MinimalSse =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_01FixtureMessage00000001\"}}\n" +
        "\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n" +
        "\n";

    private static JsonObject BuildEnvelope(RecordingHandler handler)
    {
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();

        var headers = new JsonObject();
        foreach (var name in InterestingHeaders.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (handler.RequestHeaders.TryGetValue(name, out var value))
            {
                // Authorization carries the (fake) bearer token; we redact
                // its value but keep the scheme so the snapshot proves Copilot
                // uses Bearer auth (not x-api-key).
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
            // Keep angle brackets etc. unescaped so the snapshot is
            // human-readable when reviewers diff it. The payload only
            // contains our own placeholder text — never untrusted input.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    // Strip line-ending differences so snapshots survive CRLF/LF normalisation.
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
}
