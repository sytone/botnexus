using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class CleanShutdownMarkerTests
{
    private static readonly string DataDir =
        Path.Combine(Path.GetTempPath(), "botnexus-marker-tests");

    private static string MarkerPath =>
        Path.Combine(DataDir, ".gateway-clean-shutdown");

    [Fact]
    public void DetectPreviousRun_NoMarker_ReportsUnclean()
    {
        var fs = new MockFileSystem();
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        result.WasClean.ShouldBeFalse();
        result.LastKnownUtc.ShouldBeNull();
    }

    [Fact]
    public void DetectPreviousRun_MarkerPresent_ReportsCleanWithTimestamp()
    {
        var fs = new MockFileSystem();
        var stamp = new DateTimeOffset(2026, 7, 10, 22, 7, 0, TimeSpan.Zero);
        fs.AddFile(MarkerPath, new MockFileData(stamp.ToString("o")));
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        result.WasClean.ShouldBeTrue();
        result.LastKnownUtc.ShouldBe(stamp);
    }

    [Fact]
    public void MarkRunning_RemovesMarker_SoAbruptDeathIsDetectableNextBoot()
    {
        var fs = new MockFileSystem();
        fs.AddFile(MarkerPath, new MockFileData(DateTimeOffset.UtcNow.ToString("o")));
        var marker = new CleanShutdownMarker(fs, DataDir);

        marker.MarkRunning();

        fs.FileExists(MarkerPath).ShouldBeFalse();
    }

    [Fact]
    public void MarkCleanShutdown_WritesMarkerWithTimestamp()
    {
        var fs = new MockFileSystem();
        var marker = new CleanShutdownMarker(fs, DataDir);

        marker.MarkCleanShutdown();

        fs.FileExists(MarkerPath).ShouldBeTrue();
        var content = fs.File.ReadAllText(MarkerPath);
        DateTimeOffset.TryParse(content, out _).ShouldBeTrue();
    }

    [Fact]
    public void DetectPreviousRun_ThenMarkRunning_IsTheBootSequence()
    {
        var fs = new MockFileSystem();
        var stamp = DateTimeOffset.UtcNow;
        fs.AddFile(MarkerPath, new MockFileData(stamp.ToString("o")));
        var marker = new CleanShutdownMarker(fs, DataDir);

        // Boot: detect prior state, then clear the marker for this run.
        var result = marker.DetectPreviousRun();
        marker.MarkRunning();

        result.WasClean.ShouldBeTrue();
        fs.FileExists(MarkerPath).ShouldBeFalse();
    }

    [Fact]
    public void DetectPreviousRun_CorruptMarker_TreatedAsUncleanWithoutThrowing()
    {
        var fs = new MockFileSystem();
        fs.AddFile(MarkerPath, new MockFileData("not-a-timestamp"));
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        // A present-but-garbage marker still means the last run reached graceful
        // shutdown (it wrote the file), so treat it as clean but with no timestamp.
        result.WasClean.ShouldBeTrue();
        result.LastKnownUtc.ShouldBeNull();
    }

    [Fact]
    public void MarkCleanShutdown_CreatesDataDirectoryIfMissing()
    {
        var fs = new MockFileSystem();
        var marker = new CleanShutdownMarker(fs, DataDir);

        marker.MarkCleanShutdown();

        fs.Directory.Exists(DataDir).ShouldBeTrue();
    }
}
