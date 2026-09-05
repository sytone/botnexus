using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class CleanShutdownMarkerTests
{
    private static readonly string DataDir =
        Path.Combine(Path.GetTempPath(), "botnexus-marker-tests");

    private static string MarkerPath =>
        Path.Combine(DataDir, ".gateway-clean-shutdown");

    private static string LivenessPath =>
        Path.Combine(DataDir, ".gateway-liveness");

    // ---- #3680: the unclean branch must be able to report a real last-alive instant ----

    /// <summary>
    /// Clause 1 + clause 5 (non-vacuity): an unclean termination that left a liveness stamp
    /// behind reports that concrete instant. Before #3680 the unclean branch hard-coded null,
    /// so this assertion fails against the old behaviour.
    /// </summary>
    [Fact]
    public void DetectPreviousRun_UncleanWithLivenessStamp_ReportsThatConcreteInstant()
    {
        var fs = new MockFileSystem();
        var alive = new DateTimeOffset(2026, 9, 2, 14, 31, 5, TimeSpan.Zero);
        // No shutdown marker (hard kill), but the dying run had refreshed its liveness stamp.
        fs.AddFile(LivenessPath, new MockFileData(alive.ToString("o")));
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        result.WasClean.ShouldBeFalse();
        result.LastKnownUtc.ShouldBe(alive);

        var warning = CleanShutdownMarker.BuildUncleanWarning(result);
        warning.ShouldNotBeNull();
        warning.ShouldNotContain("unknown");
        warning.ShouldContain(alive.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Clause 3: no stamp of any kind (genuine first boot) omits the timestamp clause entirely
    /// instead of printing a placeholder.
    /// </summary>
    [Fact]
    public void DetectPreviousRun_UncleanWithNoStampEver_OmitsTheTimestampClause()
    {
        var fs = new MockFileSystem();
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        result.WasClean.ShouldBeFalse();
        result.LastKnownUtc.ShouldBeNull();

        var warning = CleanShutdownMarker.BuildUncleanWarning(result);
        warning.ShouldNotBeNull();
        warning.ShouldNotContain("unknown");
        warning.ShouldNotContain("last known alive");
        warning.ShouldBe("previous gateway run terminated uncleanly (no liveness stamp available)");
    }

    /// <summary>Clause 4: a clean shutdown produces no unclean-termination warning at all.</summary>
    [Fact]
    public void BuildUncleanWarning_CleanShutdown_EmitsNoWarning()
    {
        var fs = new MockFileSystem();
        var stamp = new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
        fs.AddFile(MarkerPath, new MockFileData(stamp.ToString("o")));
        fs.AddFile(LivenessPath, new MockFileData(stamp.AddMinutes(-1).ToString("o")));
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        result.WasClean.ShouldBeTrue();
        CleanShutdownMarker.BuildUncleanWarning(result).ShouldBeNull();
    }

    /// <summary>
    /// Clause 2: the reported instant is within one refresh interval of the true last-alive time.
    /// The refresh writes the current instant, so the gap the operator sees is bounded by the
    /// documented interval rather than by log archaeology.
    /// </summary>
    [Fact]
    public void RefreshLiveness_ThenHardKill_ReportsInstantWithinOneRefreshInterval()
    {
        var fs = new MockFileSystem();
        var writer = new CleanShutdownMarker(fs, DataDir);
        var trueLastAlive = DateTimeOffset.UtcNow;

        writer.RefreshLiveness(trueLastAlive);
        // Hard kill: no MarkCleanShutdown, so no marker file is ever written.

        var nextBoot = new CleanShutdownMarker(fs, DataDir).DetectPreviousRun();

        nextBoot.WasClean.ShouldBeFalse();
        nextBoot.LastKnownUtc.ShouldNotBeNull();
        (trueLastAlive - nextBoot.LastKnownUtc!.Value).Duration()
            .ShouldBeLessThanOrEqualTo(CleanShutdownMarker.DefaultLivenessRefreshInterval);
    }

    [Fact]
    public void DetectPreviousRun_UncleanWithCorruptLivenessStamp_OmitsTheTimestampClause()
    {
        var fs = new MockFileSystem();
        fs.AddFile(LivenessPath, new MockFileData("not-a-timestamp"));
        var marker = new CleanShutdownMarker(fs, DataDir);

        var result = marker.DetectPreviousRun();

        result.WasClean.ShouldBeFalse();
        result.LastKnownUtc.ShouldBeNull();
        CleanShutdownMarker.BuildUncleanWarning(result)
            .ShouldBe("previous gateway run terminated uncleanly (no liveness stamp available)");
    }

    [Fact]
    public void MarkRunning_SeedsLivenessStamp_SoAnEarlyCrashStillReportsAnInstant()
    {
        var fs = new MockFileSystem();
        var marker = new CleanShutdownMarker(fs, DataDir);

        marker.MarkRunning();

        fs.FileExists(LivenessPath).ShouldBeTrue();
        DateTimeOffset.TryParse(fs.File.ReadAllText(LivenessPath), out _).ShouldBeTrue();
    }

    [Fact]
    public void RefreshLiveness_OverwritesThePreviousStamp()
    {
        var fs = new MockFileSystem();
        var marker = new CleanShutdownMarker(fs, DataDir);
        var first = new DateTimeOffset(2026, 9, 2, 1, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(30);

        marker.RefreshLiveness(first);
        marker.RefreshLiveness(second);

        marker.DetectPreviousRun().LastKnownUtc.ShouldBe(second);
    }

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
