using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression tests for #2387: cancellation tied to <see cref="HttpContext.RequestAborted"/> must not
/// surface as an HTTP 500 nor be logged at Error level, while any other cancellation must still
/// surface as an error (proving the central seam is not a blanket catch).
/// </summary>
public sealed class RequestCancellationMiddlewareTests
{
    private sealed class RecordingLogger : ILogger<RequestCancellationMiddleware>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    [Fact]
    public async Task RequestAbortedMidFlight_DoesNotReturn500_AndIsNotLoggedAtError()
    {
        var logger = new RecordingLogger();
        using var abortSource = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = abortSource.Token };
        var slowEndpointEntered = new TaskCompletionSource();

        var middleware = new RequestCancellationMiddleware(
            async ctx =>
            {
                // Slow endpoint: still running when the client goes away.
                slowEndpointEntered.SetResult();
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            },
            logger);

        var pipeline = middleware.InvokeAsync(context);
        await slowEndpointEntered.Task;
        await abortSource.CancelAsync();

        await pipeline;

        context.Response.StatusCode.ShouldNotBe(StatusCodes.Status500InternalServerError);
        context.Response.StatusCode.ShouldBe(499);
        logger.Levels.ShouldNotContain(LogLevel.Error);
        logger.Levels.ShouldNotContain(LogLevel.Critical);
    }

    [Fact]
    public async Task InternalCancellation_NotTiedToRequestAborted_StillSurfacesAsError()
    {
        var logger = new RecordingLogger();
        using var abortSource = new CancellationTokenSource();
        using var internalSource = new CancellationTokenSource();
        await internalSource.CancelAsync();
        var context = new DefaultHttpContext { RequestAborted = abortSource.Token };

        var middleware = new RequestCancellationMiddleware(
            _ => Task.FromCanceled(internalSource.Token),
            logger);

        await Should.ThrowAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        context.Response.StatusCode.ShouldNotBe(499);
    }

    [Fact]
    public async Task NonCancellationException_IsNotSwallowed()
    {
        var logger = new RecordingLogger();
        var context = new DefaultHttpContext();

        var middleware = new RequestCancellationMiddleware(
            _ => throw new InvalidOperationException("boom"),
            logger);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task SuccessfulRequest_PassesThroughUntouched()
    {
        var logger = new RecordingLogger();
        var context = new DefaultHttpContext();

        var middleware = new RequestCancellationMiddleware(
            ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        logger.Levels.ShouldBeEmpty();
    }
}
