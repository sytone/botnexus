using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test.Tests;

/// <summary>
/// Tests for the in-memory log capture and its <see cref="ILoggerProvider"/>.
/// </summary>
public sealed class TestChannelLogCaptureTests
{
    [Fact]
    public void LoggerProvider_CapturesRenderedMessageAndStructuredProperties()
    {
        var capture = new TestChannelLogCapture();
        using var provider = new TestChannelLoggerProvider(capture);
        var logger = provider.CreateLogger("BotNexus.Gateway.Channels");

        logger.LogInformation(
            "fan-out delivered to {ChannelType}:{Address}",
            "telegram",
            "chan-A");

        var entry = capture.Snapshot().ShouldHaveSingleItem();
        entry.Level.ShouldBe("Information");
        entry.Category.ShouldBe("BotNexus.Gateway.Channels");
        entry.Message.ShouldBe("fan-out delivered to telegram:chan-A");

        // Property-level assertion is the point: a test that matches on rendered text breaks the
        // next time someone rewords the message, and then gets "fixed" by weakening the assertion.
        entry.Properties["ChannelType"].ShouldBe("telegram");
        entry.Properties["Address"].ShouldBe("chan-A");
    }

    [Fact]
    public void LoggerProvider_CapturesAttachedException()
    {
        var capture = new TestChannelLogCapture();
        using var provider = new TestChannelLoggerProvider(capture);
        var logger = provider.CreateLogger("cat");

        logger.LogError(new InvalidOperationException("boom"), "it failed");

        var entry = capture.Snapshot().ShouldHaveSingleItem();
        entry.Level.ShouldBe("Error");
        entry.Exception.ShouldNotBeNull();
        entry.Exception.ShouldContain("boom");
    }

    [Fact]
    public void Capture_EvictsOldestEntriesAndReportsTheDrop()
    {
        // A silently truncated buffer would let a test conclude "this was never logged" from a
        // window that simply scrolled past it. The counter is what makes that distinguishable.
        var capture = new TestChannelLogCapture(capacity: 2);

        for (var i = 1; i <= 5; i++)
            capture.Add(Entry($"entry-{i}"));

        var snapshot = capture.Snapshot();
        snapshot.Count.ShouldBe(2);
        snapshot.Select(entry => entry.Message).ShouldBe(["entry-4", "entry-5"]);
        capture.DroppedEntryCount.ShouldBe(3);
    }

    [Fact]
    public void Capture_ClearResetsEntriesAndDropCounter()
    {
        var capture = new TestChannelLogCapture(capacity: 1);
        capture.Add(Entry("a"));
        capture.Add(Entry("b"));
        capture.DroppedEntryCount.ShouldBe(1);

        capture.Clear();

        capture.Snapshot().ShouldBeEmpty();
        capture.DroppedEntryCount.ShouldBe(0);
    }

    [Fact]
    public void Capture_ClampsANonPositiveCapacityToOne()
    {
        var capture = new TestChannelLogCapture(capacity: 0);

        capture.Capacity.ShouldBe(1);
        capture.Add(Entry("a"));
        capture.Snapshot().ShouldHaveSingleItem();
    }

    private static TestChannelLogEntry Entry(string message) => new(
        DateTimeOffset.UtcNow,
        "Information",
        "cat",
        message,
        null,
        new Dictionary<string, string?>());
}
