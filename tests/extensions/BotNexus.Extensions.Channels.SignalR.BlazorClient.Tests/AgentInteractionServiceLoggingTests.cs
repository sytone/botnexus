using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #1624: failure paths in <see cref="AgentInteractionService"/> must route through
/// <see cref="ILogger"/> (structured, correlated) rather than <c>Console.Error</c>, and the
/// best-effort hydration catches must log at Debug while staying non-fatal. These tests assert
/// the observability contract directly via a capturing logger.
/// </summary>
public sealed class AgentInteractionServiceLoggingTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly RecordingLogger<AgentInteractionService> _logger = new();
    private readonly AgentInteractionService _service;

    public AgentInteractionServiceLoggingTests()
    {
        _service = new AgentInteractionService(_store, new GatewayHubConnection(), _restClient, _logger);
        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true
        });
    }

    [Fact]
    public async Task RefreshAgents_failure_logs_error_with_exception_not_console()
    {
        var boom = new InvalidOperationException("rest down");
        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<AgentSummary>>>(_ => throw boom);

        // Must swallow (best-effort refresh) and not propagate.
        await _service.RefreshAgentsAsync();

        var error = _logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Error);
        error.ShouldNotBeNull("RefreshAgents failure must be logged at Error.");
        error!.Exception.ShouldBeSameAs(boom);
        error.Message.ShouldContain("RefreshAgents");
    }

    [Fact]
    public async Task SelectConversation_canvas_hydration_failure_logs_debug_and_stays_nonfatal()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "One",
            HistoryLoaded = true // skip the history-load REST path; isolate the canvas best-effort catch
        };

        var boom = new InvalidOperationException("canvas endpoint 500");
        _restClient.GetConversationCanvasAsync("agent-1", "conv-1", Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw boom);

        // Best-effort hydration must NOT throw out of SelectConversation.
        await Should.NotThrowAsync(async () => await _service.SelectConversationAsync("agent-1", "conv-1"));

        // The conversation selection still succeeded.
        agent.ActiveConversationId.ShouldBe("conv-1");

        // The swallowed best-effort failure is now diagnosable at Debug (was a silent empty catch).
        var debug = _logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Debug && e.Exception == boom);
        debug.ShouldNotBeNull("Best-effort canvas hydration failure must be logged at Debug.");
        debug!.Message.ShouldContain("conv-1");
    }

    [Fact]
    public async Task CreateConversation_failure_logs_error_and_returns_null()
    {
        var boom = new HttpRequestException("create failed");
        _restClient.CreateConversationAsync(Arg.Any<CreateConversationRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<Task<ConversationResponseDto?>>(_ => throw boom);

        var result = await _service.CreateConversationAsync("agent-1", select: true);

        result.ShouldBeNull();
        var error = _logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Error);
        error.ShouldNotBeNull("CreateConversation failure must be logged at Error.");
        error!.Exception.ShouldBeSameAs(boom);
        error.Message.ShouldContain("agent-1");
    }

    [Fact]
    public void Service_has_no_console_error_writes_remaining()
    {
        // Guard against regression to Console.Error in this service's source.
        var source = ReadServiceSource("AgentInteractionService.cs");
        source.ShouldNotContain("Console.Error", customMessage: "AgentInteractionService must not write to Console.Error -- use ILogger.");
    }

    [Fact]
    public void Service_has_no_silent_empty_catch_remaining()
    {
        // The four best-effort hydration catches must now log; none may be a bare empty catch.
        var source = ReadServiceSource("AgentInteractionService.cs");
        source.ShouldNotContain("catch { ", customMessage: "Best-effort catches must log at Debug, not swallow silently.");
    }

    private static string ReadServiceSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate BotNexus.slnx from test base directory.");

        var path = Path.Combine(
            dir.FullName,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services",
            fileName);
        return File.ReadAllText(path);
    }
}

/// <summary>
/// Minimal capturing <see cref="ILogger{T}"/> that records every entry (level, rendered message,
/// exception) so logging-contract tests can assert structured output. Enabled at all levels so
/// Debug best-effort entries are captured.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
}
