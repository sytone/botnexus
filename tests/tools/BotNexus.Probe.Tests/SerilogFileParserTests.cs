using BotNexus.Probe.LogIngestion;

namespace BotNexus.Probe.Tests;

public sealed class SerilogFileParserTests
{
    private readonly SerilogFileParser _parser = new();

    [Fact]
    public async Task ParseFileAsync_WellFormedLine_MapsCoreFields()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("app.log", "[10:15:30 INF] Started probe");
        var filePath = temp.File("app.log");
        File.SetLastWriteTime(filePath, new DateTime(2025, 01, 15, 0, 0, 0, DateTimeKind.Local));

        var entries = await CollectAsync(_parser.ParseFileAsync(filePath, new LogQuery()));

        entries.ShouldHaveSingleItem();
        var entry = entries[0];
        entry.Level.ShouldBe("INF");
        entry.Message.ShouldBe("Started probe");
        entry.SourceFile.ShouldBe("app.log");
        entry.LineNumber.ShouldBe(1);
        entry.Exception.ShouldBeNull();
        entry.Timestamp.Date.ShouldBe(new DateTime(2025, 1, 15));
    }

    [Fact]
    public async Task ParseFileAsync_WithProperties_ExtractsProperties()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("props.log", "[00:00:01 WRN] Message {Channel=cli, UserId=42}");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("props.log"), new LogQuery()));

        entries.ShouldHaveSingleItem();
        entries[0].Properties.ShouldContain(new KeyValuePair<string, string>("Channel", "cli"));
        entries[0].Properties.ShouldContain(new KeyValuePair<string, string>("UserId", "42"));
        entries[0].Channel.ShouldBe("cli");
    }

    [Fact]
    public async Task ParseFileAsync_MultiLineException_CapturesExceptionText()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("error.log", """
[01:02:03 ERR] Failed
System.InvalidOperationException: bad
   at stack
""");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("error.log"), new LogQuery()));

        entries.ShouldHaveSingleItem();
        entries[0].Exception!.Replace("\r\n", "\n").ShouldBe("System.InvalidOperationException: bad\n   at stack");
    }

    [Fact]
    public async Task ParseFileAsync_WithIdsInProperties_PopulatesIdentityFields()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("ids.log", "[02:03:04 INF] Work {CorrelationId=corr-1, SessionId=s-1, AgentId=a-1}");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("ids.log"), new LogQuery()));

        entries.ShouldHaveSingleItem();
        entries[0].CorrelationId.ShouldBe("corr-1");
        entries[0].SessionId.ShouldBe("s-1");
        entries[0].AgentId.ShouldBe("a-1");
    }

    [Fact]
    public async Task ParseFileAsync_MalformedLines_AreIgnoredWithoutFailure()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("bad.log", """
this line does not match
still invalid
[11:11:11 INF] valid line
""");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("bad.log"), new LogQuery()));

        entries.ShouldHaveSingleItem();
        entries[0].Message.ShouldBe("valid line");
    }

    [Fact]
    public async Task ParseFileAsync_EmptyFile_ReturnsNoEntries()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("empty.log", string.Empty);

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("empty.log"), new LogQuery()));

        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseFileAsync_FilterByLevel_ReturnsMatchingEntriesOnly()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("levels.log", """
[09:00:00 INF] info
[09:00:01 ERR] error
""");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("levels.log"), new LogQuery(Level: "ERR")));

        entries.ShouldHaveSingleItem();
        entries[0].Level.ShouldBe("ERR");
    }

    [Fact]
    public async Task ParseFileAsync_FilterByTimeRange_ReturnsEntriesWithinRange()
    {
        using var temp = new TestTempDirectory();
        var filePath = temp.File("time.log");
        temp.WriteFile("time.log", """
[09:59:59 INF] before
[10:00:00 INF] start
[10:30:00 INF] middle
[11:00:01 INF] after
""");
        File.SetLastWriteTime(filePath, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Local));
        var from = new DateTimeOffset(new DateTime(2025, 2, 1, 10, 0, 0, DateTimeKind.Local));
        var to = new DateTimeOffset(new DateTime(2025, 2, 1, 11, 0, 0, DateTimeKind.Local));

        var entries = await CollectAsync(_parser.ParseFileAsync(filePath, new LogQuery(From: from, To: to)));

        entries.Select(e => e.Message).ShouldBe(["start", "middle"]);
    }

    [Fact]
    public async Task ParseFileAsync_FilterBySearchText_MatchesMessageOrException()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("search.log", """
[01:00:00 INF] Started
[01:01:00 ERR] Failed to start
TimeoutException
""");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("search.log"), new LogQuery(SearchText: "timeout")));

        entries.ShouldHaveSingleItem();
        entries[0].Level.ShouldBe("ERR");
    }

    [Fact]
    public async Task ParseFileAsync_FilterBySessionAndCorrelation_MatchesIdentifiers()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("filter-ids.log", """
[12:00:00 INF] A {CorrelationId=c1, SessionId=s1}
[12:00:01 INF] B {CorrelationId=c2, SessionId=s2}
""");

        var entries = await CollectAsync(_parser.ParseFileAsync(
            temp.File("filter-ids.log"),
            new LogQuery(CorrelationId: "c2", SessionId: "s2")));

        entries.ShouldHaveSingleItem();
        entries[0].Message.ShouldBe("B");
    }

    [Fact]
    public async Task ParseDirectoryAsync_ReadsFromMultipleFiles()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("a.log", "[03:00:00 INF] from-a");
        temp.WriteFile("b.log", "[03:00:01 INF] from-b");

        var entries = await CollectAsync(_parser.ParseDirectoryAsync(temp.Path, new LogQuery()));

        entries.Count().ShouldBe(2);
        entries.Select(e => e.SourceFile).ShouldBe(["a.log", "b.log"], ignoreOrder: true);
    }

    [Fact]
    public async Task ParseFileAsync_NoTrailingNewline_StillParsesLastEntry()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("nonewline.log", "[08:08:08 INF] last-line");

        var entries = await CollectAsync(_parser.ParseFileAsync(temp.File("nonewline.log"), new LogQuery()));

        entries.ShouldHaveSingleItem();
        entries[0].Message.ShouldBe("last-line");
    }

    private static async Task<List<LogEntry>> CollectAsync(IAsyncEnumerable<LogEntry> source)
    {
        var results = new List<LogEntry>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }
}
