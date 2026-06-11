using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class DiagnosticsEndpointTests
{
    // --- Threadpool endpoint ---

    [Fact]
    public void GetThreadpool_WithMetricsRegistered_ReturnsOkWithSnapshot()
    {
        var metrics = Substitute.For<IThreadPoolMetrics>();
        metrics.PendingWorkItemCount.Returns(5L);
        metrics.GetThreadCounts().Returns((90, 100, 4, 95, 100, 4));

        var options = Options.Create(new ThreadPoolWatchdogOptions { QueueDepthThreshold = 100 });
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger, threadPoolMetrics: metrics, threadPoolOptions: options);

        var result = controller.GetThreadpool();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<ThreadPoolSnapshotDto>();
        dto.PendingWorkItems.ShouldBe(5);
        dto.WorkerAvailable.ShouldBe(90);
        dto.WorkerMax.ShouldBe(100);
        dto.WorkerMin.ShouldBe(4);
        dto.IoAvailable.ShouldBe(95);
        dto.IoMax.ShouldBe(100);
        dto.IoMin.ShouldBe(4);
        dto.IsHealthy.ShouldBeTrue();
        dto.QueueDepthThreshold.ShouldBe(100);
    }

    [Fact]
    public void GetThreadpool_WhenPendingExceedsThreshold_ReportsUnhealthy()
    {
        var metrics = Substitute.For<IThreadPoolMetrics>();
        metrics.PendingWorkItemCount.Returns(150L);
        metrics.GetThreadCounts().Returns((10, 100, 4, 50, 100, 4));

        var options = Options.Create(new ThreadPoolWatchdogOptions { QueueDepthThreshold = 100 });
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger, threadPoolMetrics: metrics, threadPoolOptions: options);

        var result = controller.GetThreadpool();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<ThreadPoolSnapshotDto>();
        dto.PendingWorkItems.ShouldBe(150);
        dto.IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public void GetThreadpool_WhenNotRegistered_ReturnsNotFound()
    {
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger);

        var result = controller.GetThreadpool();

        var notFound = result.ShouldBeOfType<NotFoundObjectResult>();
        notFound.Value.ShouldBe("Threadpool diagnostics not enabled.");
    }

    // --- Activity endpoint ---

    [Fact]
    public void GetActivity_WithTrackerRegistered_ReturnsOkWithSnapshot()
    {
        var tracker = Substitute.For<IActivityTracker>();
        tracker.LastActivityUtc.Returns(DateTimeOffset.UtcNow.AddSeconds(-30));
        tracker.TimeSinceLastActivity.Returns(TimeSpan.FromSeconds(30));

        var options = Options.Create(new LivenessWatchdogOptions { WarningThreshold = TimeSpan.FromMinutes(5) });
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger, activityTracker: tracker, livenessOptions: options);

        var result = controller.GetActivity();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<ActivitySnapshotDto>();
        dto.InactivitySeconds.ShouldBeInRange(29, 31);
        dto.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void GetActivity_WhenInactivityExceedsThreshold_ReportsUnhealthy()
    {
        var tracker = Substitute.For<IActivityTracker>();
        tracker.LastActivityUtc.Returns(DateTimeOffset.UtcNow.AddMinutes(-10));
        tracker.TimeSinceLastActivity.Returns(TimeSpan.FromMinutes(10));

        var options = Options.Create(new LivenessWatchdogOptions { WarningThreshold = TimeSpan.FromMinutes(5) });
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger, activityTracker: tracker, livenessOptions: options);

        var result = controller.GetActivity();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<ActivitySnapshotDto>();
        dto.IsHealthy.ShouldBeFalse();
        dto.InactivitySeconds.ShouldBeGreaterThanOrEqualTo(600);
    }

    [Fact]
    public void GetActivity_WhenNotRegistered_ReturnsNotFound()
    {
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger);

        var result = controller.GetActivity();

        var notFound = result.ShouldBeOfType<NotFoundObjectResult>();
        notFound.Value.ShouldBe("Activity tracking not enabled.");
    }
}
