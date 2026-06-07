using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class LogDiagnosticsRingBufferTests
{
    [Fact]
    public void Record_WarningLevel_CapturesEntry()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "Something failed for {SessionId}", "Something failed for abc123");

        buffer.PatternCount.ShouldBe(1);
        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns.Count.ShouldBe(1);
        patterns[0].Template.ShouldBe("Something failed for {SessionId}");
        patterns[0].Severity.ShouldBe(LogLevel.Warning);
        patterns[0].Count.ShouldBe(1);
        patterns[0].SampleMessage.ShouldBe("Something failed for abc123");
    }

    [Fact]
    public void Record_BelowWarning_IsIgnored()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Information, "Info message {Id}", "Info message 42");
        buffer.Record(LogLevel.Debug, "Debug message", "Debug message");
        buffer.Record(LogLevel.Trace, "Trace message", "Trace message");

        buffer.PatternCount.ShouldBe(0);
    }

    [Fact]
    public void Record_SameTemplate_IncrementsCount()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "Compaction failed for {SessionId}", "Compaction failed for session1");
        buffer.Record(LogLevel.Warning, "Compaction failed for {SessionId}", "Compaction failed for session2");
        buffer.Record(LogLevel.Warning, "Compaction failed for {SessionId}", "Compaction failed for session3");

        buffer.PatternCount.ShouldBe(1);
        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns[0].Count.ShouldBe(3);
        // Sample message is from the first observation
        patterns[0].SampleMessage.ShouldBe("Compaction failed for session1");
    }

    [Fact]
    public void Record_DifferentTemplates_CreatesSeparateEntries()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "Template A {Id}", "Template A 1");
        buffer.Record(LogLevel.Error, "Template B {Name}", "Template B foo");

        buffer.PatternCount.ShouldBe(2);
    }

    [Fact]
    public void Record_SameTemplateButDifferentLevel_CreatesSeparateEntries()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "Something happened", "Something happened");
        buffer.Record(LogLevel.Error, "Something happened", "Something happened");

        buffer.PatternCount.ShouldBe(2);
    }

    [Fact]
    public void GetPatterns_RespectsTimeWindow()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "Recent warning", "Recent warning");

        // With a 1-hour window, should be visible
        buffer.GetPatterns(TimeSpan.FromHours(1)).Count.ShouldBe(1);

        // With zero window, nothing matches
        buffer.GetPatterns(TimeSpan.Zero).Count.ShouldBe(0);
    }

    [Fact]
    public void GetPatterns_SortsByLastSeenDescending()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "First template", "First template");
        buffer.Record(LogLevel.Error, "Second template", "Second template");

        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns.Count.ShouldBe(2);
        // Second template was recorded last, should appear first
        patterns[0].Template.ShouldBe("Second template");
        patterns[1].Template.ShouldBe("First template");
    }

    [Fact]
    public void Record_EvictsOldestWhenOverCapacity()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxPatterns: 3);

        buffer.Record(LogLevel.Warning, "Template 1", "Template 1");
        buffer.Record(LogLevel.Warning, "Template 2", "Template 2");
        buffer.Record(LogLevel.Warning, "Template 3", "Template 3");
        buffer.Record(LogLevel.Warning, "Template 4", "Template 4");

        // Capacity is 3, one should have been evicted
        buffer.PatternCount.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Clear_RemovesAllPatterns()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, "Warning 1", "Warning 1");
        buffer.Record(LogLevel.Error, "Error 1", "Error 1");

        buffer.Clear();

        buffer.PatternCount.ShouldBe(0);
        buffer.GetPatterns(TimeSpan.FromHours(24)).Count.ShouldBe(0);
    }

    [Fact]
    public void Record_NullTemplate_UsesRenderedMessage()
    {
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(LogLevel.Warning, null, "A rendered message without template");

        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns.Count.ShouldBe(1);
        patterns[0].Template.ShouldBe("A rendered message without template");
    }

    [Fact]
    public void ComputeFingerprint_SameInput_ProducesSameHash()
    {
        var fp1 = LogDiagnosticsRingBuffer.ComputeFingerprint("Template {X}", LogLevel.Warning);
        var fp2 = LogDiagnosticsRingBuffer.ComputeFingerprint("Template {X}", LogLevel.Warning);

        fp1.ShouldBe(fp2);
    }

    [Fact]
    public void ComputeFingerprint_DifferentLevel_ProducesDifferentHash()
    {
        var fp1 = LogDiagnosticsRingBuffer.ComputeFingerprint("Template {X}", LogLevel.Warning);
        var fp2 = LogDiagnosticsRingBuffer.ComputeFingerprint("Template {X}", LogLevel.Error);

        fp1.ShouldNotBe(fp2);
    }

    [Fact]
    public void Record_LongMessage_TruncatesTo500Chars()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        var longMessage = new string('x', 1000);

        buffer.Record(LogLevel.Warning, "Long message template", longMessage);

        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns[0].SampleMessage.Length.ShouldBe(503); // 500 + "..."
        patterns[0].SampleMessage.ShouldEndWith("...");
    }
}
