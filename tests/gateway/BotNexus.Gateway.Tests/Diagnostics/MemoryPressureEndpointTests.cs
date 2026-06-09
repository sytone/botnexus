using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class MemoryPressureEndpointTests
{
    private readonly MemoryPressureMonitor _monitor;
    private readonly DiagnosticsController _controller;

    public MemoryPressureEndpointTests()
    {
        _monitor = new MemoryPressureMonitor(
            NullLogger<MemoryPressureMonitor>.Instance);
        _controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance,
            logBuffer: null,
            memoryMonitor: _monitor);
    }

    [Fact]
    public void GetMemoryPressure_ReturnsOkWithSnapshot()
    {
        var result = _controller.GetMemoryPressure();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MemoryPressureDto>(ok.Value);
        Assert.True(dto.WorkingSetBytes > 0);
        Assert.True(dto.GcCommittedBytes > 0);
        Assert.True(dto.TotalAvailableBytes > 0);
        Assert.True(dto.PressurePercent >= 0);
        Assert.NotEmpty(dto.WorkingSetReadable);
        Assert.NotEmpty(dto.GcCommittedReadable);
        Assert.NotEmpty(dto.TotalAvailableReadable);
        Assert.NotEmpty(dto.Level);
        Assert.NotEmpty(dto.Guidance);
    }

    [Fact]
    public void GetMemoryPressure_ReturnsNotFound_WhenMonitorNull()
    {
        var controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance,
            logBuffer: null,
            memoryMonitor: null);

        var result = controller.GetMemoryPressure();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetMemoryPressureHistory_ReturnsOkWithSnapshots()
    {
        // Capture a few snapshots first
        _monitor.CaptureSnapshot();
        _monitor.CaptureSnapshot();

        var result = _controller.GetMemoryPressureHistory(count: 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryPressureHistoryResponse>(ok.Value);
        // +1 because GetMemoryPressureHistory doesn't capture, but GetMemoryPressure does
        // so we just check we get at least 2 from our manual captures
        Assert.True(response.Snapshots.Count >= 2);
        Assert.True(response.SnapshotsRetained >= 2);
    }

    [Fact]
    public void GetMemoryPressureHistory_ClampsCount()
    {
        _monitor.CaptureSnapshot();

        var result = _controller.GetMemoryPressureHistory(count: 500);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MemoryPressureHistoryResponse>(ok.Value);
        Assert.Single(response.Snapshots);
    }

    [Fact]
    public void GetMemoryPressureHistory_ReturnsNotFound_WhenMonitorNull()
    {
        var controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance,
            logBuffer: null,
            memoryMonitor: null);

        var result = controller.GetMemoryPressureHistory();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetMemoryPressure_Dto_LevelIsValidString()
    {
        var result = _controller.GetMemoryPressure();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MemoryPressureDto>(ok.Value);
        Assert.Contains(dto.Level, new[] { "Normal", "Elevated", "Critical" });
    }
}
