using System.Diagnostics;
using System.Text;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression coverage for #2568: a non-image, non-<c>text/*</c> attachment (for example
/// <c>application/json</c>) uploaded correctly, transported correctly and then reached the agent as
/// a self-closing <c>&lt;attachment ... /&gt;</c> metadata tag with the payload silently discarded.
/// </summary>
/// <remarks>
/// <para>
/// NON-VACUITY (AC7): every inlining test asserts that the AGENT-VISIBLE message text contains a
/// SENTINEL STRING FROM INSIDE THE FILE. Asserting merely that an attachment tag was emitted passes
/// against the broken behaviour and would be worthless. The sentinels below
/// (<c>__SENTINEL_*__</c>) exist nowhere except inside the attachment payloads.
/// </para>
/// <para>
/// VACUITY: no test in this file contains an early <c>return</c>, a conditional skip, a
/// <c>Skip=</c> attribute, or a catch-and-continue. Every test ends in an unconditional assertion.
/// </para>
/// </remarks>
public sealed class TextualAttachmentInliningTests
{
    private const string JsonSentinel = "__SENTINEL_JSON_PAYLOAD__";
    private const string StructuredSentinel = "__SENTINEL_STRUCTURED_SUFFIX__";
    private const string PdfSentinel = "__SENTINEL_PDF_BYTES__";
    private const string TruncationSentinel = "__SENTINEL_HEAD_OF_FILE__";

    private static BinaryContentPart Binary(string mimeType, string fileName, string body) =>
        new()
        {
            MimeType = mimeType,
            FileName = fileName,
            Data = Encoding.UTF8.GetBytes(body)
        };

    // ── AC1 / AC2 / AC7: textual payloads actually reach the agent ───────

    [Fact]
    public void Compose_ApplicationJsonBinaryPart_InlinesTheFileContents()
    {
        var body = $"{{\"marker\":\"{JsonSentinel}\",\"count\":3}}";
        var message = AgentUserMessageComposer.Compose(
            "please analyse this",
            [Binary("application/json", "config.json", body)]);

        // THE assertion that matters: a string from INSIDE the file is visible to the agent.
        message.Content.ShouldContain(JsonSentinel);
        message.Content.ShouldContain(body);
        message.Content.ShouldContain("please analyse this");
        // And it is no longer a self-closing metadata-only tag.
        message.Content.ShouldNotContain("\" />");
        message.Content.ShouldContain("</attachment>");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/xml")]
    [InlineData("application/javascript")]
    [InlineData("application/x-yaml")]
    [InlineData("application/sql")]
    public void Compose_TextualApplicationTypes_InlineTheFileContents(string mimeType)
    {
        var body = $"prefix {JsonSentinel} suffix";
        var message = AgentUserMessageComposer.Compose(
            "hello",
            [Binary(mimeType, "payload.dat", body)]);

        message.Content.ShouldContain(JsonSentinel);
    }

    [Theory]
    [InlineData("application/vnd.api+json")]
    [InlineData("application/atom+xml")]
    [InlineData("application/vnd.custom+yaml")]
    public void Compose_StructuredSuffixTypes_InlineTheFileContents(string mimeType)
    {
        var body = $"<<{StructuredSentinel}>>";
        var message = AgentUserMessageComposer.Compose(
            "hello",
            [Binary(mimeType, "doc.bin", body)]);

        message.Content.ShouldContain(StructuredSentinel);
    }

    // ── AC4: opaque binaries stay metadata-only ─────────────────────────

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    public void Compose_OpaqueBinaryPart_EmitsMetadataOnly(string mimeType)
    {
        var message = AgentUserMessageComposer.Compose(
            "hello",
            [Binary(mimeType, "report.bin", PdfSentinel)]);

        // The payload must NOT be inlined - not verbatim and not base64.
        message.Content.ShouldNotContain(PdfSentinel);
        message.Content.ShouldNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(PdfSentinel)));
        // Metadata still reaches the agent so it knows the file exists.
        message.Content.ShouldContain("report.bin");
        message.Content.ShouldContain(mimeType);
        message.Content.ShouldContain("/>");
    }

    [Fact]
    public void Compose_ImagePart_IsUntouchedByTextualInlining()
    {
        var message = AgentUserMessageComposer.Compose(
            "look",
            [new BinaryContentPart
            {
                MimeType = "image/svg+xml",
                FileName = "logo.svg",
                Data = Encoding.UTF8.GetBytes($"<svg>{PdfSentinel}</svg>")
            }]);

        // image/svg+xml carries a +xml structured suffix, but images belong to the vision path and
        // must never be diverted into the text body (#2568 must not change image handling).
        message.Content.ShouldNotContain(PdfSentinel);
        message.Images.ShouldNotBeNull();
        message.Images!.Count.ShouldBe(1);
    }

    // ── AC6: bounded inlining with an explicit truncation marker ────────

    [Fact]
    public void Compose_OversizedTextualPart_TruncatesAndMarksTheCut()
    {
        var tail = "__SENTINEL_TAIL_OF_FILE__";
        var filler = new string('x', TextualMimeType.MaxInlineBytes + 4096);
        var body = TruncationSentinel + filler + tail;

        var message = AgentUserMessageComposer.Compose(
            "analyse",
            [Binary("application/json", "huge.json", body)]);

        // The head of the file is present (so this is not a blanket drop) ...
        message.Content.ShouldContain(TruncationSentinel);
        // ... the tail beyond the bound is not ...
        message.Content.ShouldNotContain(tail);
        // ... and the cut is explicit rather than an invisible short read.
        message.Content.ShouldContain(TextualMimeType.TruncationMarker);
    }

    [Fact]
    public void Compose_OversizedTextContentPart_IsBoundedByTheSameLimit()
    {
        var tail = "__SENTINEL_TEXT_TAIL__";
        var body = TruncationSentinel + new string('y', TextualMimeType.MaxInlineBytes + 1024) + tail;

        var message = AgentUserMessageComposer.Compose(
            "analyse",
            [new TextContentPart { MimeType = "text/plain", Text = body }]);

        message.Content.ShouldContain(TruncationSentinel);
        message.Content.ShouldNotContain(tail);
        message.Content.ShouldContain(TextualMimeType.TruncationMarker);
    }

    [Fact]
    public void Compose_TextualPartAtTheBound_IsNotTruncated()
    {
        var body = TruncationSentinel.PadRight(TextualMimeType.MaxInlineBytes, 'z');
        Encoding.UTF8.GetByteCount(body).ShouldBe(TextualMimeType.MaxInlineBytes);

        var message = AgentUserMessageComposer.Compose(
            "analyse",
            [Binary("application/json", "exact.json", body)]);

        message.Content.ShouldContain(TruncationSentinel);
        message.Content.ShouldNotContain(TextualMimeType.TruncationMarker);
    }

    // ── AC5: the withholding is reported, not silent ────────────────────

    [Fact]
    public void Compose_OpaqueBinaryPart_ReportsTheWithheldPayload()
    {
        var recorder = new RecordingLoggerProvider();
        var previous = ProviderDiagnostics.LoggerFactory;
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(recorder);
        });
        ProviderDiagnostics.LoggerFactory = factory;

        var events = new List<ActivityEvent>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("BotNexus.Tests.AttachmentGuard");
        using var activity = source.StartActivity("compose");

        try
        {
            AgentUserMessageComposer.Compose(
                "hello",
                [Binary("application/pdf", "report.pdf", PdfSentinel)]);
        }
        finally
        {
            ProviderDiagnostics.LoggerFactory = previous;
            factory.Dispose();
        }

        activity.ShouldNotBeNull();
        events.AddRange(activity!.Events);

        // Structured warning, mirroring the ImageModalityGuard (#2485) reporting shape.
        var warning = recorder.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("report.pdf");
        warning.Message.ShouldContain("application/pdf");
        warning.Message.ShouldContain(AgentUserMessageComposer.BinaryDropSite);

        // Activity event on the ambient span, same as the image guard.
        events.ShouldContain(e => e.Name == AttachmentPayloadGuard.WithheldActivityEventName);
    }

    [Fact]
    public void Compose_TextualPart_DoesNotReportAWithheldPayload()
    {
        var recorder = new RecordingLoggerProvider();
        var previous = ProviderDiagnostics.LoggerFactory;
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(recorder);
        });
        ProviderDiagnostics.LoggerFactory = factory;

        try
        {
            AgentUserMessageComposer.Compose(
                "hello",
                [Binary("application/json", "config.json", $"{{\"m\":\"{JsonSentinel}\"}}")]);
        }
        finally
        {
            ProviderDiagnostics.LoggerFactory = previous;
            factory.Dispose();
        }

        // A payload that DID reach the agent must not be reported as withheld - otherwise the
        // report degrades into noise and stops being a signal.
        recorder.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Compose_TruncatedTextualPart_ReportsTheTruncation()
    {
        var recorder = new RecordingLoggerProvider();
        var previous = ProviderDiagnostics.LoggerFactory;
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(recorder);
        });
        ProviderDiagnostics.LoggerFactory = factory;

        try
        {
            AgentUserMessageComposer.Compose(
                "hello",
                [Binary(
                    "application/json",
                    "huge.json",
                    new string('q', TextualMimeType.MaxInlineBytes + 32))]);
        }
        finally
        {
            ProviderDiagnostics.LoggerFactory = previous;
            factory.Dispose();
        }

        var warning = recorder.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("huge.json");
    }

    // ── AC3: ONE shared predicate, reachable from the WASM client ───────

    [Theory]
    [InlineData("text/plain", true)]
    [InlineData("text/csv", true)]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/xml", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("application/rss+xml", true)]
    [InlineData("application/javascript", true)]
    [InlineData("application/x-yaml", true)]
    [InlineData("application/pdf", false)]
    [InlineData("application/zip", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/png", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTextual_ClassifiesTypesAsSpecified(string? mimeType, bool expected)
        => TextualMimeType.IsTextual(mimeType).ShouldBe(expected);

    /// <summary>
    /// AC3 pin: the predicate must live in the zero-dependency wire assembly that the Blazor WASM
    /// client can legally reference, so the client-side part builder and the server-side composer
    /// cannot drift. If it is ever moved into a server-side assembly the client cannot reference it
    /// and the duplication that caused #2568 returns.
    /// </summary>
    [Fact]
    public void TextualMimeType_LivesInTheWasmSafeWireAssembly()
        => typeof(TextualMimeType).Assembly.GetName().Name.ShouldBe("BotNexus.Domain.Wire");

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Entries);

        public void Dispose() { }

        private sealed class Recorder(List<(LogLevel Level, string Message)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Add((logLevel, formatter(state, exception)));
        }
    }
}
