using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Tests.Diagnostics;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Regression coverage for #3443: the stream-assembly checksum on the Copilot Messages path must
/// be able to REPORT, not merely to compute.
/// </summary>
/// <remarks>
/// <para>
/// #2443 introduced <c>StreamAssemblyConformance.Reconcile</c> specifically to make stream-assembly
/// defects self-reporting, and #3336 wired it onto this parser - with <c>logger: null</c> and
/// <c>deltaCount: 0</c>. <c>Reconcile</c> guards its entire diagnostic behind <c>logger?.</c>, so
/// the null argument did not degrade the warning, it deleted it: the checksum computed a verdict
/// and discarded its own finding. <c>/v1/messages</c> is the dominant live transport, so the muted
/// call site was the one that mattered most.
/// </para>
/// <para>
/// The sibling reconciliation tests assert the CONTENT outcome (the provider's final text wins).
/// These assert the OBSERVABILITY outcome, which is a genuinely separate property: every one of
/// those tests passed while the warning was unreachable. A checksum that cannot report is
/// indistinguishable from a checksum reporting all-clear, and that ambiguity cost #3425 a full
/// investigation cycle.
/// </para>
/// </remarks>
[Collection(ProviderDiagnosticsCollection.Name)]
public class CopilotMessagesStreamAssemblyDiagnosticTests : IDisposable
{
    private const string WarningPrefix = "Stream assembly mismatch at provider seam";

    private readonly ILoggerFactory _previousFactory = ProviderDiagnostics.LoggerFactory;
    private readonly CapturingLoggerFactory _capture = new();

    public CopilotMessagesStreamAssemblyDiagnosticTests()
    {
        ProviderDiagnostics.LoggerFactory = _capture;
    }

    public void Dispose()
    {
        ProviderDiagnostics.LoggerFactory = _previousFactory;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// AC3: a mismatch on the Copilot Messages path emits the seam warning, carrying the provider,
    /// api and transport identifiers that tell an operator WHICH seam disagreed.
    /// </summary>
    [Fact]
    public async Task ContentBlockStop_WhenAssembledDisagreesWithFinalText_EmitsTheSeamWarning()
    {
        await RunAsync(BuildBody(deltas: ["alpha", "BETA"], finalText: "alpha beta"));

        var warning = _capture.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(WarningPrefix, StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        // The identifiers are what make the log line actionable: without them an operator knows a
        // seam disagreed but not which transport to look at.
        warning.State["Provider"].ShouldBe("github-copilot");
        warning.State["ModelId"].ShouldBe("claude-opus-5");
        warning.State["Api"].ShouldBe("github-copilot-messages");
        warning.State["Transport"].ShouldBe("sse");

        // Content must never reach the log store; only lengths and a bounded escaped window.
        warning.State["AssembledLength"].ShouldBe("9");
        warning.State["FinalLength"].ShouldBe("10");
    }

    /// <summary>
    /// AC4: the reported delta count equals the number of text deltas that contributed to the
    /// block. Pinning a value greater than one is the point - the defect was a literal
    /// <c>0</c>, and a count that silently returned to a constant would be the same defect back.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ContentBlockStop_ReportsTheNumberOfTextDeltasThatBuiltTheBlock(int deltaCount)
    {
        var deltas = Enumerable.Range(0, deltaCount).Select(i => $"frag{i}").ToArray();

        await RunAsync(BuildBody(deltas, finalText: "a genuinely different final value"));

        var warning = _capture.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(WarningPrefix, StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        warning.State["DeltaCount"].ShouldBe(
            deltaCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The warning is a MISMATCH report, not a per-block heartbeat. If it fired on agreement the
    /// AC3 assertion above would pass for the wrong reason and the log store would fill with noise.
    /// </summary>
    [Fact]
    public async Task ContentBlockStop_WhenAssembledMatchesFinalText_EmitsNoWarning()
    {
        await RunAsync(BuildBody(deltas: ["alpha", " beta"], finalText: "alpha beta"));

        _capture.Entries
            .Where(e => e.Message.Contains(WarningPrefix, StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    /// <summary>
    /// Fail-open pin: a stop frame with no final value has nothing to check against, so it is not
    /// a mismatch and must not be reported as one.
    /// </summary>
    [Fact]
    public async Task ContentBlockStop_WithNoProviderFinalText_EmitsNoWarning()
    {
        await RunAsync(BuildBody(deltas: ["alpha", " beta"], finalText: null));

        _capture.Entries
            .Where(e => e.Message.Contains(WarningPrefix, StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    private static string BuildBody(string[] deltas, string? finalText)
    {
        var body = new StringBuilder();
        body.Append("event: message_start\n");
        body.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_diag\",\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n");
        body.Append("event: content_block_start\n");
        body.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\n");

        foreach (var delta in deltas)
        {
            body.Append("event: content_block_delta\n");
            body.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":");
            body.Append(System.Text.Json.JsonSerializer.Serialize(delta));
            body.Append("}}\n\n");
        }

        body.Append("event: content_block_stop\n");
        body.Append("data: {\"type\":\"content_block_stop\",\"index\":0");
        if (finalText is not null)
        {
            body.Append(",\"text\":");
            body.Append(System.Text.Json.JsonSerializer.Serialize(finalText));
        }
        body.Append("}\n\n");

        body.Append("event: message_delta\n");
        body.Append("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":9}}\n\n");
        body.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n");
        return body.ToString();
    }

    private static async Task<AssistantMessage> RunAsync(string body)
    {
        var handler = new StreamingHandler(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = new LlmModel(
            Id: "claude-opus-5",
            Name: "claude-opus-5",
            Api: CopilotMessagesProvider.ApiId,
            Provider: "github-copilot",
            BaseUrl: "https://api.enterprise.githubcopilot.com",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 16384);
        var context = new Context(
            SystemPrompt: "diagnostic",
            Messages: [new UserMessage(new UserMessageContent("diagnostic"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class StreamingHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Captures the structured state as well as the rendered message: asserting on the message
    /// text alone would let a regression that dropped the named values still pass.
    /// </summary>
    private sealed record CapturedEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, string> State);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<CapturedEntry> _entries = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<CapturedEntry> Entries
        {
            get { lock (_gate) { return [.. _entries]; } }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private void Add(CapturedEntry entry)
        {
            lock (_gate) { _entries.Add(entry); }
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                        values[pair.Key] = pair.Value?.ToString() ?? "";
                }

                owner.Add(new CapturedEntry(logLevel, formatter(state, exception), values));
            }
        }
    }
}
