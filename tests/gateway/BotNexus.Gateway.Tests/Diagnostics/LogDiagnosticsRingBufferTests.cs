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

        buffer.Record(LogLevel.Warning, "First {Value}", "First template");
        buffer.Record(LogLevel.Error, "Second {Value}", "Second template");

        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns.Count.ShouldBe(2);
        // Second template was recorded last, should appear first
        patterns[0].Template.ShouldBe("Second {Value}");
        patterns[1].Template.ShouldBe("First {Value}");
    }

    [Fact]
    public void Record_EvictsOldestWhenOverCapacity()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxPatterns: 3);

        buffer.Record(LogLevel.Warning, "Template {Value}", "Template 1");
        buffer.Record(LogLevel.Warning, "Template {Value} two", "Template 2");
        buffer.Record(LogLevel.Warning, "Template {Value} three", "Template 3");
        buffer.Record(LogLevel.Warning, "Template {Value} four", "Template 4");

        // Capacity is 3, one should have been evicted
        buffer.PatternCount.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Record_UsesFindingIdentityToSeparateAggregatesAndCoalesceRepeats()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxRecentOccurrencesPerPattern: 2);
        var findingA = new Dictionary<string, string?>
        {
            ["AgentId"] = "agent-a", ["SessionId"] = "session-a", ["FindingId"] = "finding-a"
        };

        buffer.Record(LogLevel.Warning, "scanner", new EventId(7, "Finding"), "Finding {FindingId}", "Finding one", findingA);
        buffer.Record(LogLevel.Warning, "scanner", new EventId(7, "Finding"), "Finding {FindingId}", "Finding one repeated", findingA);
        buffer.Record(LogLevel.Warning, "scanner", new EventId(7, "Finding"), "Finding {FindingId}", "Finding two",
            new Dictionary<string, string?> { ["AgentId"] = "agent-b", ["SessionId"] = "session-b", ["FindingId"] = "finding-b" });

        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns.Count.ShouldBe(2);
        var repeated = patterns.Single(pattern => pattern.RecentOccurrences[0].Properties["FindingId"] == "finding-a");
        repeated.Count.ShouldBe(2);
        repeated.DistinctFindingCount.ShouldBe(1);
        repeated.RecentOccurrences.Count.ShouldBe(2);
        patterns.Single(pattern => pattern.RecentOccurrences[0].Properties["FindingId"] == "finding-b").Count.ShouldBe(1);
    }

    [Fact]
    public void Record_SnapshotsCanonicalAllowedBoundedPropertiesAtRingBoundary()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        var source = new Dictionary<string, string?>
        {
            ["findingid"] = "finding-a",
            ["SESSIONID"] = new string('s', LogDiagnosticsRingBuffer.MaxPropertyValueLength + 20),
            ["Secret"] = "must-not-be-retained"
        };

        buffer.Record(LogLevel.Warning, "scanner", new EventId(7, "Finding"), "Finding {FindingId}", "Finding one", source);
        source["findingid"] = "mutated";
        source["AgentId"] = "added-later";

        var properties = buffer.GetPatterns(TimeSpan.FromHours(1)).Single().RecentOccurrences.Single().Properties;
        properties.Keys.ShouldBe(["FindingId", "SessionId"], ignoreOrder: true);
        properties["FindingId"].ShouldBe("finding-a");
        properties["SessionId"].ShouldBe(LogDiagnosticsRingBuffer.RedactedOversizedValue);
        properties.ShouldNotContainKey("AgentId");
        properties.Values.ShouldNotContain("must-not-be-retained");
        var mutableView = properties.ShouldBeAssignableTo<IDictionary<string, string?>>();
        Should.Throw<NotSupportedException>(() => mutableView["FindingId"] = "changed");
    }

    [Fact]
    public void Record_SanitisesEveryRetainedStringAtRingBoundary()
    {
        const string credential = "user:password";
        const string querySecret = "token=abc123";
        const string apiSecret = "api_key=xyz789";
        var unsafeUrl = $"https://{credential}@example.test/path?{querySecret}";
        var buffer = new LogDiagnosticsRingBuffer();

        buffer.Record(
            LogLevel.Error,
            $"category {unsafeUrl}",
            new EventId(9, $"event https://example.test/?{querySecret}"),
            $"Template {unsafeUrl} and {apiSecret} {{FindingId}}",
            $"Sample {unsafeUrl} and {apiSecret}",
            new Dictionary<string, string?>
            {
                ["findingid"] = $"finding {unsafeUrl}",
                ["DiagnosticCategory"] = $"category {apiSecret}",
                ["NotAllowed"] = credential
            });

        var pattern = buffer.GetPatterns(TimeSpan.FromHours(1)).Single();
        var occurrence = pattern.RecentOccurrences.Single();
        var completeRetainedPattern = string.Join('|',
            pattern.Fingerprint,
            pattern.Template,
            pattern.SampleMessage,
            occurrence.Category,
            occurrence.EventName,
            occurrence.RenderedMessage,
            string.Join(';', occurrence.Properties.Select(pair => $"{pair.Key}={pair.Value}")));

        completeRetainedPattern.ShouldNotContain(credential);
        completeRetainedPattern.ShouldNotContain(querySecret);
        completeRetainedPattern.ShouldNotContain(apiSecret);
        occurrence.Properties.Keys.ShouldBe(["FindingId", "DiagnosticCategory"], ignoreOrder: true);
        pattern.Template.Length.ShouldBeLessThanOrEqualTo(LogDiagnosticsRingBuffer.MaxTemplateLength + 3);
    }

    [Fact]
    public async Task Record_ConcurrentReadersObserveSynchronizedAggregateState()
    {
        const int writerCount = 4;
        const int recordsPerWriter = 250;
        var buffer = new LogDiagnosticsRingBuffer();
        var properties = new Dictionary<string, string?> { ["FindingId"] = "finding-a" };

        var writers = Enumerable.Range(0, writerCount).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < recordsPerWriter; index++)
                buffer.Record(LogLevel.Warning, "scanner", new EventId(7, "Finding"), "Finding {FindingId}", "Finding one", properties);
        }));
        var readers = Enumerable.Range(0, writerCount).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < recordsPerWriter; index++)
            {
                foreach (var pattern in buffer.GetPatterns(TimeSpan.FromHours(1)))
                {
                    pattern.Count.ShouldBeGreaterThan(0);
                    pattern.LastSeen.ShouldBeGreaterThanOrEqualTo(pattern.FirstSeen);
                    pattern.RecentOccurrences.Count.ShouldBeLessThanOrEqualTo(20);
                }
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        buffer.GetPatterns(TimeSpan.FromHours(1)).Single().Count.ShouldBe(writerCount * recordsPerWriter);
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
        patterns[0].Template.ShouldBe(LogDiagnosticsRingBuffer.RedactedUnstructuredValue);
        patterns[0].SampleMessage.ShouldBe(LogDiagnosticsRingBuffer.RedactedUnstructuredValue);
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

        buffer.Record(LogLevel.Warning, "Long message {Value}", longMessage);

        var patterns = buffer.GetPatterns(TimeSpan.FromHours(1));
        patterns[0].SampleMessage.ShouldBe(LogDiagnosticsRingBuffer.RedactedOversizedValue);
    }
}
