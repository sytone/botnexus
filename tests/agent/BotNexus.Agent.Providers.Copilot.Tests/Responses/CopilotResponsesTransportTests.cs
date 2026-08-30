using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests.Responses;

public sealed class CopilotResponsesTransportTests
{
    [Fact]
    public void DiscoveryDescriptor_PreservesAdvertisedResponsesTransportsPrivately()
    {
        var info = new CopilotModelInfo
        {
            Id = "gpt-5.6-sol",
            Capabilities = new CopilotModelCapabilities { Family = "gpt-5.6-sol" },
            SupportedEndpoints = ["/responses", "ws:/responses"]
        };

        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        model.ShouldNotBeNull();
        model!.GetType().GetProperties().Select(p => p.Name)
            .ShouldNotContain(name => name.Contains("Transport", StringComparison.OrdinalIgnoreCase));
        CopilotResolvedModelDescriptors.Get(model).SupportsResponsesWebSocket.ShouldBeTrue();
        CopilotResponsesTransportPolicy.Select(model, CopilotResponsesTransportPreference.Auto)
            .ShouldBe(CopilotResponsesWireTransport.WebSocket);
    }

    [Fact]
    public void Auto_UsesSse_WhenWebSocketWasNotAdvertised()
    {
        var model = MapModel(["/responses"]);

        CopilotResponsesTransportPolicy.Select(model, CopilotResponsesTransportPreference.Auto)
            .ShouldBe(CopilotResponsesWireTransport.Sse);
    }

    [Fact]
    public async Task WebSocketAndSseFixtures_ProduceEquivalentNormalizedEvents()
    {
        var events = FixtureEvents();
        var websocket = await ParseJsonEventsAsync(events);
        var sse = await ParseSseAsync(events);

        Project(websocket).ShouldBe(Project(sse));
        websocket.OfType<ThinkingDeltaEvent>().ShouldHaveSingleItem();
        websocket.OfType<TextDeltaEvent>().Count().ShouldBe(2);
        websocket.OfType<ToolCallDeltaEvent>().ShouldHaveSingleItem();
        websocket.OfType<DoneEvent>().Single().Message.Usage.TotalTokens.ShouldBe(15);
    }

    [Fact]
    public async Task Gpt56_WebSocketAndSse_StripRepeatedChunkCrLf_WithEquivalentDeltaAndFinalText()
    {
        // #2119 acceptance: reproduce via the actual capability-aware WebSocket path AND the
        // SSE fallback for the same GPT-5.6 frame sequence, asserting both the emitted
        // TextDeltaEvent values and the final accumulated assistant text on each path. The
        // model advertises both endpoints so Auto selects WebSocket; the SSE run pins the
        // transport explicitly so the two paths are compared head-to-head.
        var frames = new[]
        {
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\r\\nUnder\"}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\r\\nstood\"}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\r\\n now\"}",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}"
        };
        var model = MapModel(["/responses", "ws:/responses"], "gpt-5.6-sol");

        var socket = new StubWebSocketTransport(messages: frames);
        var websocketProvider = new CopilotResponsesProvider(
            new HttpClient(new RecordingHandler(_ =>
                throw new InvalidOperationException("SSE fallback must not run for a clean WebSocket stream."))),
            NullLogger<CopilotResponsesProvider>.Instance,
            socket);
        var sseProvider = new CopilotResponsesProvider(
            new HttpClient(new RecordingHandler(_ => SseResponse(frames))),
            NullLogger<CopilotResponsesProvider>.Instance);

        var websocketEvents = await CollectAsync(
            websocketProvider.Stream(model, BuildContext(), Options()));
        var sseEvents = await CollectAsync(
            sseProvider.Stream(model, BuildContext(), SseOptions()));

        string[] expectedDeltas = ["Under", "stood", " now"];
        websocketEvents.OfType<TextDeltaEvent>().Select(x => x.Delta).ShouldBe(expectedDeltas);
        sseEvents.OfType<TextDeltaEvent>().Select(x => x.Delta).ShouldBe(expectedDeltas);

        websocketEvents.OfType<DoneEvent>().Single().Message.Content.OfType<TextContent>().Single().Text
            .ShouldBe("Understood now");
        sseEvents.OfType<DoneEvent>().Single().Message.Content.OfType<TextContent>().Single().Text
            .ShouldBe("Understood now");
    }

    [Fact]
    public async Task JsonEventParser_PreservesStandaloneNewlineDelta()
    {
        var events = new[]
        {
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\n\"}",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}"
        };

        var parsed = await ParseJsonEventsAsync(events);

        parsed.OfType<TextDeltaEvent>().Single().Delta.ShouldBe("\n");
        parsed.OfType<DoneEvent>().Single().Message.Content.OfType<TextContent>().Single().Text.ShouldBe("\n");
    }

    [Fact]
    public async Task Auto_WebSocketSetupFailure_FallsBackToSse()
    {
        var socket = new StubWebSocketTransport(connectFailure: new WebSocketException("upgrade rejected"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);
        var model = MapModel(["/responses", "ws:/responses"]);

        var result = await provider.Stream(model, BuildContext(), Options()).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        socket.ConnectCount.ShouldBe(1);
        handler.RequestCount.ShouldBe(1);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
    }

    [Fact]
    public async Task Auto_WebSocketCleanCloseBeforeSemanticOutput_FallsBackToSse()
    {
        var socket = new StubWebSocketTransport();
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
    }

    [Fact]
    public async Task Auto_WebSocketCleanCloseAfterSemanticOutput_DoesNotReplayOverSse()
    {
        var socket = new StubWebSocketTransport(messages:
        [
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
        ]);
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(0);
        result.StopReason.ShouldBe(StopReason.Error);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task Auto_WebSocketFailureAfterSemanticOutput_DoesNotReplayOverSse()
    {
        var socket = new StubWebSocketTransport(messages:
        [
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
        ], receiveFailure: new WebSocketException("connection lost"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(0);
        result.StopReason.ShouldBe(StopReason.Error);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task WebSocketCloseAfterSemanticOutput_SurfacesCloseCodeAndReason()
    {
        // #3366 AC2/AC4: a server close carrying 1009 + a reason must reach the surfaced failure.
        // Semantic output already happened, so SSE replay is suppressed (AC5) and the error text is
        // the observable channel for the close evidence.
        var socket = new StubWebSocketTransport(messages:
        [
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
        ], close: new CopilotResponsesCloseFrame(1009, "request payload too large"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(0);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("1009");
        result.ErrorMessage!.ShouldContain("request payload too large");
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task WebSocketCloseBeforeSemanticOutput_TagsFallbackReasonWithCloseCode()
    {
        // #3366 AC3: the fallback reason tag must distinguish a 1008 close from any other cause,
        // and AC5: the SSE replay still happens because no semantic output was emitted.
        // The fallback tag is set before the SSE forward runs, so capturing the activity at START and
        // reading its tags after the result is deterministic; ActivityStopped fires only when the
        // provider method returns, which is AFTER the awaited result completes.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProviderDiagnostics.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => { lock (activities) activities.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);

        var socket = new StubWebSocketTransport(close: new CopilotResponsesCloseFrame(1008, "policy violation"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
        List<Activity> observed;
        lock (activities) observed = [.. activities];
        var reasons = observed
            .Select(a => a.GetTagItem("botnexus.provider.transport.fallback_reason") as string)
            .Where(value => value != null)
            .ToList();
        reasons.ShouldContain(value => value != null && value.Contains("1008"));
    }

    [Fact]
    public async Task WebSocketCloseWithNoReason_StillReportsTheCloseCode()
    {
        // #3366: a close frame with an empty reason must still carry its numeric code, and must not
        // fabricate a reason string.
        var socket = new StubWebSocketTransport(messages:
        [
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
        ], close: new CopilotResponsesCloseFrame(1011, null));
        var provider = new CopilotResponsesProvider(
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("SSE replay must be suppressed."))),
            NullLogger<CopilotResponsesProvider>.Instance,
            socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("1011");
    }

    /// <summary>
    /// #3382 AC1, end-to-end through the real provider: a WebSocket Responses stream cancelled
    /// mid-parse must not strand an <see cref="LlmStreamIncompleteException"/> on the internal
    /// stream's result task. Nothing awaits that task once the turn unwinds, so a faulted one is
    /// collected unobserved and re-raised by the finalizer thread as an
    /// <c>UnobservedTaskException</c> - the live-site shape reported in the issue.
    /// <para>
    /// The assertion is made against the runtime's own escalation event after a forced finalization,
    /// because that event <em>is</em> the defect; asserting only on the returned message would pass
    /// even with the bug present. The filter is deliberately narrow - it matches the
    /// cancellation-shaped message this scenario produces - because
    /// <see cref="TaskScheduler.UnobservedTaskException"/> is process-wide and sibling tests in this
    /// class exercise the AC2 fault path on purpose, whose faulted result tasks legitimately escalate.
    /// </para>
    /// <para>
    /// Non-vacuity is pinned separately: the drained events must show the turn actually reached the
    /// cancellation path (a terminal <see cref="StopReason.Aborted"/>), so an empty escape list cannot
    /// be an artefact of the scenario never running.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WebSocketStreamCancelledMidParse_LeavesNoUnobservedExceptionToEscalate()
    {
        const string CancellationShape = "Copilot Responses stream parse failed: The operation was canceled";

        var escaped = new List<string>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs args)
        {
            var text = args.Exception?.ToString() ?? string.Empty;
            if (text.Contains(CancellationShape, StringComparison.Ordinal))
            {
                lock (escaped) escaped.Add(text);
            }

            // Never observe: a sibling test's genuine fault escalation is not ours to swallow, and
            // xunit's own handler already tolerates it.
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using var cts = new CancellationTokenSource();
            var socket = new StubWebSocketTransport(
                messages:
                [
                    "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
                    "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
                ],
                // The turn is cancelled while the parse is still running, then the transport reports
                // the cancellation exactly as a real socket does under an aborted request.
                receiveFailure: new OperationCanceledException(cts.Token),
                onReceive: () => cts.Cancel());

            var provider = new CopilotResponsesProvider(
                new HttpClient(new RecordingHandler(_ =>
                    throw new InvalidOperationException("SSE fallback must not run for a cancelled turn."))),
                NullLogger<CopilotResponsesProvider>.Instance,
                socket);

            var options = new CopilotResponsesOptions { ApiKey = "test-token", CancellationToken = cts.Token };
            var stream = provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), options);

            var events = new List<AssistantMessageEvent>();
            try
            {
                // Enumerate with an uncancelled token: the consumer unwinds when the producer ends,
                // without ever awaiting the internal result task - precisely the condition that leaves
                // a faulted task unobserved.
                await foreach (var evt in stream.WithCancellation(CancellationToken.None))
                    events.Add(evt);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is normal control flow for this scenario.
            }

            // Non-vacuity: the turn genuinely took the cancellation path.
            events.OfType<ErrorEvent>().ShouldContain(
                e => e.Reason == StopReason.Aborted,
                "the scenario must actually cancel, or an empty escape list proves nothing");

            // Force finalization so any unobserved faulted task escalates now rather than at some
            // arbitrary later point in the suite.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (escaped)
            {
                escaped.ShouldBeEmpty(
                    "a cancelled Copilot Responses stream must not leave an unobserved LlmStreamIncompleteException "
                    + "for the finalizer thread to raise as an UnobservedTaskException");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Theory]
    [InlineData(403)]
    [InlineData(401)]
    public async Task Auto_WebSocketHandshakeRejectedWithAuthStatus_ShortCircuitsWithoutSseFallback(int status)
    {
        // #3674 AC1/AC2/AC6/AC7: a 401 or 403 on the upgrade handshake is a rejected CREDENTIAL, not a
        // degraded transport. SSE would present the same credential to the same provider, so the
        // fallback is guaranteed to fail and must not be attempted. Exactly one provider call.
        var socket = new StubWebSocketTransport(
            connectFailure: new CopilotResponsesWebSocketHandshakeException(
                status, $"handshake rejected with HTTP {status}", null));
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("SSE fallback must not run for an authentication failure."));
        var provider = new CopilotResponsesProvider(
            new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Exactly one provider call: the WebSocket connect, and no SSE retry behind it.
        socket.ConnectCount.ShouldBe(1);
        handler.RequestCount.ShouldBe(0);

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        // AC2: the surfaced failure is the ProviderAuthenticationException contract - it names the
        // provider and the status, matching what the SSE path already produces for a 401/403 body.
        result.ErrorMessage!.ShouldContain("Authentication failed for provider 'Copilot Responses'");
        result.ErrorMessage!.ShouldContain($"HTTP {status}");
    }

    [Fact]
    public async Task Auto_WebSocketHandshakeRejectedWithAuthStatus_LogsAnAuthErrorNotATransportFallbackWarning()
    {
        // #3674 AC3: the operator's first and most prominent signal must identify an AUTHENTICATION
        // failure at Error level, not a WRN about falling back to SSE. During the 2026-08-29 incident
        // the transport-health wording sent diagnosis to the wrong subsystem for 39 minutes.
        var logger = new CapturingLogger<CopilotResponsesProvider>();
        var socket = new StubWebSocketTransport(
            connectFailure: new CopilotResponsesWebSocketHandshakeException(403, "handshake rejected", null));
        var provider = new CopilotResponsesProvider(
            new HttpClient(new RecordingHandler(_ =>
                throw new InvalidOperationException("SSE fallback must not run for an authentication failure."))),
            logger,
            socket);

        await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var entries = logger.Entries;
        entries.ShouldContain(
            e => e.Level == LogLevel.Error && e.Message.Contains("authentication failure", StringComparison.OrdinalIgnoreCase),
            "an auth failure must be reported as an error naming authentication");
        entries.ShouldNotContain(
            e => e.Message.Contains("falling back to SSE", StringComparison.OrdinalIgnoreCase),
            "the misleading transport-fallback warning must not be emitted for an auth failure");
    }

    [Fact]
    public async Task Auto_WebSocketHandshakeRejectedWithNonAuthStatus_StillFallsBackToSse()
    {
        // #3674 AC4, non-vacuity for the narrowing: a 503 handshake rejection is a genuine transport
        // fault. Only 401/403 short-circuit; everything else must retain today's fallback behaviour,
        // proving the fix did not disable the fallback wholesale.
        var socket = new StubWebSocketTransport(
            connectFailure: new CopilotResponsesWebSocketHandshakeException(503, "handshake rejected", null));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(
            new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
    }

    [Fact]
    public async Task Auto_TransientWebSocketFailure_StillFallsBackToSseAndReturnsTheSseResult()
    {
        // #3674 AC8: a plain transport drop carries no HTTP status at all and must keep falling back.
        var socket = new StubWebSocketTransport(receiveFailure: new WebSocketException("connection reset"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(
            new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        result.StopReason.ShouldNotBe(StopReason.Error);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
    }

    [Fact]
    public async Task Auto_CancelledTurn_IsNotReclassifiedAsAnAuthFailure()
    {
        // #3674 AC5: the OperationCanceledException path is unchanged. The new auth filter runs BEFORE
        // the existing fallback filter, so a cancellation must not be captured by it.
        using var cts = new CancellationTokenSource();
        var socket = new StubWebSocketTransport(
            receiveFailure: new OperationCanceledException(cts.Token),
            onReceive: () => cts.Cancel());
        var provider = new CopilotResponsesProvider(
            new HttpClient(new RecordingHandler(_ =>
                throw new InvalidOperationException("SSE fallback must not run for a cancelled turn."))),
            NullLogger<CopilotResponsesProvider>.Instance,
            socket);

        var options = new CopilotResponsesOptions { ApiKey = "test-token", CancellationToken = cts.Token };
        var events = new List<AssistantMessageEvent>();
        try
        {
            await foreach (var evt in provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), options)
                .WithCancellation(CancellationToken.None))
            {
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
        }

        events.OfType<ErrorEvent>().ShouldContain(e => e.Reason == StopReason.Aborted);
        events.OfType<ErrorEvent>().ShouldNotContain(
            e => (e.Error.ErrorMessage ?? string.Empty).Contains("Authentication failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("The server returned status code '403' when status code '101' was expected", 403)]
    [InlineData("The server returned status code '401' when status code '101' was expected", 401)]
    [InlineData("The server returned status code '503' when status code '101' was expected", 503)]
    [InlineData("Unable to connect to the remote server", null)]
    [InlineData("", null)]
    public void HandshakeStatusParse_ReadsTheRejectedStatusFromTheClrMessage(string message, int? expected)
    {
        // #3674: pins the fallback extraction path used when CollectHttpResponseDetails yields nothing,
        // so a runtime message change surfaces as a test failure rather than silently re-routing auth
        // failures back into the SSE fallback.
        CopilotResponsesHandshakeStatus.TryParseStatus(message).ShouldBe(expected);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public void HandshakeStatusClassification_TreatsOnly401And403AsAuthFailures(int status, bool expected)
    {
        // #3674 AC4: a 429 is a rate limit and a 5xx is a server fault; both stay retryable over SSE.
        CopilotResponsesHandshakeStatus.IsAuthFailure(status).ShouldBe(expected);
    }

    private static CopilotResponsesOptions Options() => new() { ApiKey = "test-token" };

    private static CopilotResponsesOptions SseOptions() => new()
    {
        ApiKey = "test-token",
        TransportPreference = CopilotResponsesTransportPreference.Sse
    };

    private static LlmModel MapModel(IReadOnlyList<string> endpoints, string id = "gpt-5.5")
        => CopilotModelDiscoveryProvider.MapToLlmModel(new CopilotModelInfo
        {
            Id = id,
            Name = id,
            Capabilities = new CopilotModelCapabilities { Family = id },
            SupportedEndpoints = endpoints.ToList()
        }) ?? throw new InvalidOperationException();

    private static Context BuildContext() => new(
        "Be helpful.",
        [new UserMessage(new UserMessageContent("hello"), 1)],
        []);

    private static string[] FixtureEvents() =>
    [
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"reason_1\",\"type\":\"reasoning\"}}",
        "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"reason_1\",\"delta\":\"think\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"reason_1\",\"type\":\"reasoning\"}}",
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
        "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}",
        "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\n\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"tool_1\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"\",\"type\":\"function_call\"}}",
        "{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"tool_1\",\"delta\":\"{\\\"value\\\":1}\"}",
        "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"tool_1\",\"arguments\":\"{\\\"value\\\":1}\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"tool_1\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"{\\\"value\\\":1}\",\"type\":\"function_call\"}}",
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}"
    ];

    private static async Task<List<AssistantMessageEvent>> ParseJsonEventsAsync(IEnumerable<string> json)
    {
        var queue = new Queue<string>(json);
        return await ParseAsync(ct => ValueTask.FromResult(queue.TryDequeue(out var value)
            ? new ResponsesEvent(JsonDocument.Parse(value).RootElement.GetProperty("type").GetString() ?? "", value)
            : null));
    }

    private static async Task<List<AssistantMessageEvent>> ParseSseAsync(IEnumerable<string> json)
    {
        var payload = SsePayload(json);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);
        var llm = new LlmStream();
        await ResponsesStreamParser.ParseAsync(llm, reader, BaseModel(), null, "test", NullLogger.Instance,
            static (_, _, _, _) => { }, null, null, null, CancellationToken.None);
        return await CollectAsync(llm);
    }

    private static async Task<List<AssistantMessageEvent>> ParseAsync(
        Func<CancellationToken, ValueTask<ResponsesEvent?>> read)
    {
        var llm = new LlmStream();
        await ResponsesStreamParser.ParseEventsAsync(llm, read, BaseModel(), null, "test", NullLogger.Instance,
            static (_, _, _, _) => { }, null, null, null, CancellationToken.None);
        return await CollectAsync(llm);
    }

    private static async Task<List<AssistantMessageEvent>> CollectAsync(LlmStream stream)
    {
        var result = new List<AssistantMessageEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private static string[] Project(IEnumerable<AssistantMessageEvent> events) => events.Select(e => e switch
    {
        TextDeltaEvent x => $"text:{x.Delta}",
        ThinkingDeltaEvent x => $"thinking:{x.Delta}",
        ToolCallDeltaEvent x => $"tool:{x.Delta}",
        DoneEvent x => $"done:{x.Message.Usage.TotalTokens}",
        _ => e.Type
    }).ToArray();

    private static LlmModel BaseModel() => new("gpt-5.5", "GPT-5.5", "test", "test", "https://example.test", true,
        ["text"], new ModelCost(0, 0, 0, 0), 1000, 1000);

    private static string SsePayload(IEnumerable<string> events) => string.Join("", events.Select(value =>
    {
        var type = JsonDocument.Parse(value).RootElement.GetProperty("type").GetString();
        return $"event: {type}\ndata: {value}\n\n";
    }));

    private static HttpResponseMessage SseResponse(IEnumerable<string> events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SsePayload(events), Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    /// <summary>
    /// Captures level + rendered message so a test can assert on WHAT was logged, not merely that the
    /// code path ran. #3674 AC3 is a statement about operator-visible severity and wording, so the log
    /// is the assertion surface and <c>NullLogger</c> cannot express it.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class StubWebSocketTransport(
        IReadOnlyList<string>? messages = null,
        Exception? connectFailure = null,
        Exception? receiveFailure = null,
        CopilotResponsesCloseFrame? close = null,
        Action? onReceive = null) : ICopilotResponsesWebSocketTransport
    {
        private readonly Queue<string> _messages = new(messages ?? []);
        public int ConnectCount { get; private set; }
        public CopilotResponsesCloseFrame? LastClose { get; private set; }
        public ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            ConnectCount++;
            return connectFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(connectFailure);
        }
        public ValueTask SendAsync(string payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            onReceive?.Invoke();
            if (_messages.TryDequeue(out var message)) return ValueTask.FromResult<string?>(message);
            if (receiveFailure is not null) return ValueTask.FromException<string?>(receiveFailure);
            LastClose = close;
            return ValueTask.FromResult<string?>(null);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
