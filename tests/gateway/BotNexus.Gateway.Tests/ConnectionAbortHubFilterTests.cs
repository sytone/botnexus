using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Security.Claims;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3679: a client that navigates away mid-<c>SubscribeAll</c> cancels
/// <c>Context.ConnectionAborted</c>, the session store honours the token and throws
/// <see cref="TaskCanceledException"/>, and SignalR's dispatcher logs the escape as
/// <c>Failed to invoke hub method 'SubscribeAll'</c> at <see cref="LogLevel.Error"/>. Ordinary
/// browser churn therefore wears a store-outage costume in the ERR channel.
///
/// These tests pin <see cref="ConnectionAbortHubFilter"/> in BOTH directions. Asserting only that
/// the normal-disconnect path is quiet would be vacuous — a filter that swallowed everything would
/// pass it — so every case asserts the emitted log LEVEL and whether the exception propagated to
/// the dispatcher (which is what decides whether the Error line is written at all).
/// </summary>
public sealed class ConnectionAbortHubFilterTests
{
    /// <summary>AC1: a cancellation attributable to the client's own abort token is absorbed at Debug.</summary>
    [Fact]
    public async Task ConnectionAbortedCancellation_IsAbsorbed_AndLoggedBelowWarning()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var logger = new RecordingLogger<ConnectionAbortHubFilter>();
        var filter = new ConnectionAbortHubFilter(logger);
        var context = CreateInvocationContext(aborted.Token);

        var result = await filter.InvokeMethodAsync(
            context,
            _ => throw new TaskCanceledException("A task was canceled.", innerException: null, aborted.Token));

        result.ShouldBeNull("an abandoned connection has no one left to receive a completion");

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(
            LogLevel.Debug,
            "a normal client disconnect must be logged below Warning; ERR is the #3679 defect");
        entry.Message.ShouldContain("SubscribeAll");
        logger.Entries.ShouldAllBe(e => e.Level < LogLevel.Warning);
    }

    /// <summary>
    /// AC2: a genuine fault must still escape to the dispatcher (which logs it at Error with the
    /// exception attached) and must NOT be quietly downgraded to a Debug line by this filter.
    /// </summary>
    [Fact]
    public async Task NonCancellationException_StillPropagates_AndIsNotDowngraded()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var logger = new RecordingLogger<ConnectionAbortHubFilter>();
        var filter = new ConnectionAbortHubFilter(logger);
        var context = CreateInvocationContext(aborted.Token);
        var fault = new InvalidOperationException("session store is wedged");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await filter.InvokeMethodAsync(context, _ => throw fault));

        thrown.ShouldBeSameAs(
            fault,
            "a store failure must reach the dispatcher unchanged so it is still reported at Error");
        logger.Entries.ShouldBeEmpty(
            "the filter must not narrate a genuine fault at Debug — the dispatcher owns that Error line");
    }

    /// <summary>
    /// AC3: an INTERNAL cancellation (a timeout carrying its own token, with the connection alive)
    /// must not be silenced. This is the case that makes the filter a seam rather than a blanket catch.
    /// </summary>
    [Fact]
    public async Task InternalTimeoutCancellation_WithLiveConnection_StillPropagates()
    {
        using var connectionAlive = new CancellationTokenSource();
        using var internalTimeout = new CancellationTokenSource();
        await internalTimeout.CancelAsync();

        connectionAlive.IsCancellationRequested.ShouldBeFalse("the client is still connected in this scenario");

        var logger = new RecordingLogger<ConnectionAbortHubFilter>();
        var filter = new ConnectionAbortHubFilter(logger);
        var context = CreateInvocationContext(connectionAlive.Token);

        await Should.ThrowAsync<TaskCanceledException>(async () =>
            await filter.InvokeMethodAsync(
                context,
                _ => throw new TaskCanceledException("timed out", innerException: null, internalTimeout.Token)));

        logger.Entries.ShouldBeEmpty("an internal timeout is a fault, not client churn");
    }

    /// <summary>
    /// AC3, harder variant: the connection HAS aborted, but the cancellation demonstrably came from
    /// a different, separately-signalled token. Attributing that to the client would let a real
    /// internal timeout hide behind coincidental client churn.
    /// </summary>
    [Fact]
    public async Task ForeignSignalledToken_DuringConnectionAbort_StillPropagates()
    {
        using var aborted = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        await aborted.CancelAsync();
        await foreign.CancelAsync();

        var logger = new RecordingLogger<ConnectionAbortHubFilter>();
        var filter = new ConnectionAbortHubFilter(logger);
        var context = CreateInvocationContext(aborted.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await filter.InvokeMethodAsync(
                context,
                _ => throw new OperationCanceledException("internal", foreign.Token)));

        logger.Entries.ShouldBeEmpty(
            "a distinctly-signalled foreign token must not be attributed to the client disconnect");
    }

    /// <summary>
    /// AC1 (negative control): with the connection still LIVE, a bare cancellation carrying no token
    /// is not client churn and must surface as a fault. Without this, the predicate could degenerate
    /// into "swallow every OperationCanceledException" and all the above would still pass.
    /// </summary>
    [Fact]
    public async Task BareCancellation_WithLiveConnection_StillPropagates()
    {
        using var connectionAlive = new CancellationTokenSource();

        var logger = new RecordingLogger<ConnectionAbortHubFilter>();
        var filter = new ConnectionAbortHubFilter(logger);
        var context = CreateInvocationContext(connectionAlive.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await filter.InvokeMethodAsync(context, _ => throw new OperationCanceledException("bare")));

        logger.Entries.ShouldBeEmpty();
    }

    /// <summary>A successful invocation is passed straight through and logs nothing.</summary>
    [Fact]
    public async Task SuccessfulInvocation_IsUntouched()
    {
        using var connectionAlive = new CancellationTokenSource();

        var logger = new RecordingLogger<ConnectionAbortHubFilter>();
        var filter = new ConnectionAbortHubFilter(logger);
        var context = CreateInvocationContext(connectionAlive.Token);

        var result = await filter.InvokeMethodAsync(context, _ => ValueTask.FromResult<object?>("ok"));

        result.ShouldBe("ok");
        logger.Entries.ShouldBeEmpty();
    }

    /// <summary>
    /// AC5: the audit clause is satisfied STRUCTURALLY rather than by enumerating verbs — the filter
    /// is registered globally for <see cref="GatewayHub"/>, so every verb that threads
    /// <c>Context.ConnectionAborted</c> into a store call routes through the one seam. This test is
    /// what stops the filter above from being a well-tested but unreachable class.
    /// </summary>
    [Fact]
    public void Contributor_RegistersTheFilter_GloballyForTheGatewayHub()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new SignalRServiceContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HubOptions<GatewayHub>>>().Value;

        // HubOptions.HubFilters is INTERNAL to ASP.NET Core, so it is read reflectively. The lookup
        // fails loudly if that shape ever changes: a silently-null probe would turn this test into a
        // no-op that passes with the filter unregistered.
        var filtersProperty = typeof(HubOptions).GetProperty(
            "HubFilters",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        filtersProperty.ShouldNotBeNull(
            "HubOptions.HubFilters no longer exists; this assertion can no longer prove global registration");

        var filters = (IEnumerable<IHubFilter>?)filtersProperty!.GetValue(options);
        filters.ShouldNotBeNull("no hub filters were registered for GatewayHub");
        filters!.OfType<ConnectionAbortHubFilter>().ShouldHaveSingleItem(
            "the connection-abort seam must be registered globally so no hub verb can be overlooked (#3679 AC5)");
    }

    private static HubInvocationContext CreateInvocationContext(CancellationToken connectionAborted)
    {
        var method = typeof(SubscribeAllProbeHub).GetMethod(nameof(SubscribeAllProbeHub.SubscribeAll))!;
        method.Name.ShouldBe("SubscribeAll", "the probe must carry the production verb name");

        return new HubInvocationContext(
            new ProbeHubCallerContext(connectionAborted),
            new ServiceCollection().BuildServiceProvider(),
            new SubscribeAllProbeHub(),
            method,
            []);
    }

    /// <summary>
    /// Stands in for <see cref="GatewayHub"/> purely to supply a <see cref="MethodInfo"/> whose name
    /// matches the production verb in the #3679 evidence, so the Debug line's content is asserted
    /// against the real method name rather than a placeholder.
    /// </summary>
    private sealed class SubscribeAllProbeHub : Hub
    {
        public Task SubscribeAll() => Task.CompletedTask;
    }

    private sealed class ProbeHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public ProbeHubCallerContext(CancellationToken connectionAborted)
        {
            ConnectionAborted = connectionAborted;
        }

        public override string ConnectionId => "conn-probe";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => new();
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; }
        public override void Abort() { }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
