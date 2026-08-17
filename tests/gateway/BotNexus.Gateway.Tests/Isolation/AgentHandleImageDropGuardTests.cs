using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Tests.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentUserMessage = BotNexus.Gateway.Abstractions.Models.AgentUserMessage;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Regression coverage for #2495: <c>IAgentHandle</c>'s typed <see cref="AgentUserMessage"/>
/// defaults (added by PR #2494 for #2484) forwarded only <c>message.Content</c> to the text-only
/// overload, so any handle that did not override them - <see cref="DockerSandboxAgentHandle"/> and
/// every future non-in-process handle - discarded the <see cref="AgentImageContent"/> vision
/// payload with no error, no warning, and no user-visible signal.
/// </summary>
/// <remarks>
/// <para>
/// Every test below asserts an OBSERVABLE: an actual log record captured from a real
/// <see cref="ILoggerProvider"/>, or an <see cref="ActivityEvent"/> recorded on a real listening
/// <see cref="ActivitySource"/>. None asserts "a method returned true" or inspects an internal
/// flag.
/// </para>
/// <para>
/// Vacuity: no test contains an early <c>return</c>, a conditional skip, a <c>Skip</c> attribute or
/// a catch-and-continue. Every test ends in unconditional assertions that fail if the guard is
/// removed.
/// </para>
/// </remarks>
[Collection(ProviderDiagnosticsCollection.Name)]
public sealed class AgentHandleImageDropGuardTests : IDisposable
{
    private readonly CapturingLoggerProvider _capture = new();
    private readonly ILoggerFactory _factory;
    private readonly ILoggerFactory _previous = ProviderDiagnostics.LoggerFactory;

    public AgentHandleImageDropGuardTests()
    {
        _factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(_capture);
        });
        // The API composition root assigns this same ambient hook (Program.cs), which is what makes
        // the guard reachable in production without threading a logger through IAgentHandle.
        ProviderDiagnostics.LoggerFactory = _factory;
    }

    public void Dispose()
    {
        ProviderDiagnostics.LoggerFactory = _previous;
        _factory.Dispose();
    }

    private static AgentUserMessage MessageWithImage() =>
        new("look at this", [new BotNexus.Gateway.Abstractions.Models.AgentImageContent("data:image/png;base64,AQID")]);

    private static DockerSandboxAgentHandle CreateSandboxHandle(ILogger? logger = null) =>
        new(
            AgentId.From("agent-sandbox"),
            SessionId.From("sess-sandbox"),
            "sandbox-1",
            new SandboxState("sandbox-1"),
            logger ?? NullLogger.Instance);

    // ── The non-in-process handle reports the drop on every dispatch path ──

    [Fact]
    public async Task DockerSandboxHandle_SteerWithImages_EmitsDropWarningNamingTheSteerSite()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var handle = CreateSandboxHandle(factory.CreateLogger("sandbox"));

        await handle.SteerAsync(MessageWithImage(), CancellationToken.None);

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.ShouldContain(AgentHandleImageDropGuard.SteerSite);
        warning.Message.ShouldContain(nameof(DockerSandboxAgentHandle));
        warning.Message.ShouldContain("1 image content part");
    }

    [Fact]
    public async Task DockerSandboxHandle_InterruptAndSteerWithImages_EmitsDropWarningNamingTheRedirectSite()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var handle = CreateSandboxHandle(factory.CreateLogger("sandbox"));

        await handle.InterruptAndSteerAsync(MessageWithImage(), CancellationToken.None);

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.ShouldContain(AgentHandleImageDropGuard.RedirectSite);
        warning.Message.ShouldContain(nameof(DockerSandboxAgentHandle));
    }

    [Fact]
    public async Task DockerSandboxHandle_PromptWithImages_EmitsDropWarningBeforeTheUnsupportedThrow()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var handle = CreateSandboxHandle(factory.CreateLogger("sandbox"));

        await Should.ThrowAsync<NotSupportedException>(
            () => handle.PromptAsync(MessageWithImage(), CancellationToken.None));

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.ShouldContain(AgentHandleImageDropGuard.PromptSite);
    }

    [Fact]
    public async Task DockerSandboxHandle_StreamWithImages_EmitsDropWarningNamingTheStreamSite()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var handle = CreateSandboxHandle(factory.CreateLogger("sandbox"));

        var stream = handle.StreamAsync(MessageWithImage(), CancellationToken.None);
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in stream)
            {
                // Enumerate to force the iterator to run; it throws NotSupportedException.
            }
        });

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.ShouldContain(AgentHandleImageDropGuard.StreamSite);
    }

    [Fact]
    public async Task DockerSandboxHandle_SteerWithoutImages_EmitsNoWarning()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var handle = CreateSandboxHandle(factory.CreateLogger("sandbox"));

        await handle.SteerAsync(new AgentUserMessage("plain text only"), CancellationToken.None);

        capture.Records.ShouldNotContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DockerSandboxHandle_SteerWithImages_StillDeliversTheComposedText()
    {
        // The degrade must remain lossless for the text (which AgentUserMessageComposer has
        // already folded non-image attachments into) - the guard reports, it does not swallow.
        var composed = AgentUserMessageComposer.Compose(
            "look at this",
            [new BinaryContentPart { MimeType = "image/png", Data = [1, 2, 3], FileName = "shot.png" },
             new TextContentPart { MimeType = "text/plain", Text = "line one" }]);
        composed.Images.ShouldNotBeNull();
        composed.Images!.Count.ShouldBe(1);

        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var handle = new TextCapturingSandboxProbe(factory.CreateLogger("probe"));

        await handle.SteerAsync(composed, CancellationToken.None);

        handle.SteeredText.ShouldNotBeNull();
        handle.SteeredText!.ShouldContain("look at this");
        handle.SteeredText.ShouldContain("line one");
        capture.Records.ShouldContain(r => r.Level == LogLevel.Warning);
    }

    // ── The interface default itself is now loud (covers every unaudited handle) ──

    [Fact]
    public async Task InterfaceDefaultSteer_OnAHandleThatDoesNotOverride_ReportsTheDropViaAmbientLogger()
    {
        IAgentHandle handle = new BareInheritingHandle();

        await handle.SteerAsync(MessageWithImage(), CancellationToken.None);

        var warning = _capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.ShouldContain(AgentHandleImageDropGuard.SteerSite);
        warning.Message.ShouldContain(nameof(BareInheritingHandle));
        ((BareInheritingHandle)handle).SteeredText.ShouldBe("look at this");
    }

    [Fact]
    public async Task InterfaceDefaultInterruptAndSteer_OnAHandleThatDoesNotOverride_ReportsTheDrop()
    {
        IAgentHandle handle = new BareInheritingHandle();

        await handle.InterruptAndSteerAsync(MessageWithImage(), CancellationToken.None);

        var warning = _capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.ShouldContain(AgentHandleImageDropGuard.RedirectSite);
    }

    [Fact]
    public void ReportDropped_RecordsAnActivityEventOnTheAmbientSpan()
    {
        using var source = new ActivitySource("test.2495");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.2495",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("dispatch");
        activity.ShouldNotBeNull();

        AgentHandleImageDropGuard.ReportDropped("SomeHandle", 3, AgentHandleImageDropGuard.FollowUpSite);

        var evt = activity!.Events.Single(e => e.Name == AgentHandleImageDropGuard.DropActivityEventName);
        evt.Tags.ShouldContain(t => t.Key == "botnexus.image.dropped_count" && Equals(t.Value, 3));
        evt.Tags.ShouldContain(t => t.Key == "botnexus.image.drop_site"
            && Equals(t.Value, AgentHandleImageDropGuard.FollowUpSite));
        evt.Tags.ShouldContain(t => t.Key == "botnexus.agent_handle.type" && Equals(t.Value, "SomeHandle"));
    }

    // ── Probes ──

    /// <summary>
    /// A handle that implements only the mandatory members and therefore inherits every typed
    /// default - exactly the shape #2495 says silently loses images.
    /// </summary>
    private sealed class BareInheritingHandle : IAgentHandle
    {
        public AgentId AgentId { get; } = AgentId.From("bare");
        public SessionId SessionId { get; } = SessionId.From("bare-sess");
        public bool IsRunning => true;
        public string? SteeredText { get; private set; }

        public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentResponse> PromptAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SteerAsync(string message, CancellationToken cancellationToken = default)
        {
            SteeredText = message;
            return Task.CompletedTask;
        }

        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default)
        {
            SteeredText = message;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Same shape, but records the text it received and uses an injected logger.</summary>
    private sealed class TextCapturingSandboxProbe(ILogger logger) : IAgentHandle
    {
        public AgentId AgentId { get; } = AgentId.From("probe");
        public SessionId SessionId { get; } = SessionId.From("probe-sess");
        public bool IsRunning => true;
        public string? SteeredText { get; private set; }

        public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentResponse> PromptAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SteerAsync(string message, CancellationToken cancellationToken = default)
        {
            SteeredText = message;
            return Task.CompletedTask;
        }

        public Task SteerAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => SteerAsync(
                AgentHandleImageDropGuard.DegradeToText(this, message, AgentHandleImageDropGuard.SteerSite, logger),
                cancellationToken);

        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record LogRecord(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogRecord> _records = [];

        public IReadOnlyList<LogRecord> Records
        {
            get { lock (_records) { return [.. _records]; } }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private void Add(LogRecord record)
        {
            lock (_records) { _records.Add(record); }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }
}
