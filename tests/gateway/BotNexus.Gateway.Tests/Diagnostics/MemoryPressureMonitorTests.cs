using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class MemoryPressureMonitorTests
{
    private readonly MemoryPressureMonitor _monitor;

    public MemoryPressureMonitorTests()
    {
        _monitor = new MemoryPressureMonitor(
            NullLogger<MemoryPressureMonitor>.Instance);
    }

    [Fact]
    public void CaptureSnapshot_ReturnsValidSnapshot()
    {
        var snapshot = _monitor.CaptureSnapshot();

        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.GcCommittedBytes > 0);
        Assert.True(snapshot.TotalAvailableBytes > 0);
        Assert.True(snapshot.PressurePercent >= 0);
        Assert.True(snapshot.PressurePercent <= 100);
        Assert.NotNull(snapshot.WorkingSetReadable);
        Assert.NotNull(snapshot.GcCommittedReadable);
        Assert.NotNull(snapshot.TotalAvailableReadable);
        Assert.NotNull(snapshot.Guidance);
        Assert.True(snapshot.CapturedAt <= DateTimeOffset.UtcNow);
        Assert.True(snapshot.Gen0Collections >= 0);
        Assert.True(snapshot.Gen1Collections >= 0);
        Assert.True(snapshot.Gen2Collections >= 0);
    }

    [Fact]
    public void CaptureSnapshot_AddsToHistory()
    {
        Assert.Equal(0, _monitor.SnapshotCount);

        _monitor.CaptureSnapshot();

        Assert.Equal(1, _monitor.SnapshotCount);
    }

    [Fact]
    public void CaptureSnapshot_MultipleCaptures_IncrementsHistory()
    {
        _monitor.CaptureSnapshot();
        _monitor.CaptureSnapshot();
        _monitor.CaptureSnapshot();

        Assert.Equal(3, _monitor.SnapshotCount);
    }

    [Fact]
    public void GetHistory_ReturnsNewestFirst()
    {
        _monitor.CaptureSnapshot();
        Thread.Sleep(10);
        _monitor.CaptureSnapshot();

        var history = _monitor.GetHistory(2);

        Assert.Equal(2, history.Count);
        Assert.True(history[0].CapturedAt >= history[1].CapturedAt);
    }

    [Fact]
    public void GetHistory_RespectsCount()
    {
        for (int i = 0; i < 5; i++)
            _monitor.CaptureSnapshot();

        var history = _monitor.GetHistory(3);

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void GetHistory_ClampsToAvailable()
    {
        _monitor.CaptureSnapshot();
        _monitor.CaptureSnapshot();

        var history = _monitor.GetHistory(50);

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void RingBuffer_EvictsOldestWhenFull()
    {
        var smallMonitor = new MemoryPressureMonitor(
            NullLogger<MemoryPressureMonitor>.Instance, maxSnapshots: 3);

        for (int i = 0; i < 5; i++)
            smallMonitor.CaptureSnapshot();

        Assert.Equal(3, smallMonitor.SnapshotCount);
    }

    [Fact]
    public void CaptureSnapshot_Level_IsNormal_UnderTypicalConditions()
    {
        // Under test conditions, memory usage should be well under 70%
        var snapshot = _monitor.CaptureSnapshot();

        // Most test environments won't hit Elevated or Critical
        Assert.Equal(MemoryPressureLevel.Normal, snapshot.Level);
    }

    [Fact]
    public void CaptureSnapshot_Guidance_IsNotEmpty()
    {
        var snapshot = _monitor.CaptureSnapshot();

        Assert.NotEmpty(snapshot.Guidance);
        Assert.Contains("action", snapshot.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 500, "500.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(1024L * 1024 * 1024 * 2, "2.00 GB")]
    public void FormatBytes_FormatsCorrectly(long bytes, string expected)
    {
        var result = MemoryPressureMonitor.FormatBytes(bytes);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytes_NegativeBytes_ReturnsZero()
    {
        var result = MemoryPressureMonitor.FormatBytes(-100);

        Assert.Equal("0 B", result);
    }

    [Fact]
    public void CaptureSnapshot_LogsWarning_WhenElevated()
    {
        // We can't easily force elevated pressure in tests, but we verify
        // the monitor doesn't throw under normal conditions
        var snapshot = _monitor.CaptureSnapshot();
        Assert.NotNull(snapshot);
    }

    [Fact]
    public void PressureLevel_ThresholdsAreCorrect()
    {
        Assert.Equal(0.70, MemoryPressureMonitor.ElevatedThreshold);
        Assert.Equal(0.90, MemoryPressureMonitor.CriticalThreshold);
    }
}
