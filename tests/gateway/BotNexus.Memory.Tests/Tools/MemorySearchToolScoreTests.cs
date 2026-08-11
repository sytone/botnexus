using System.Globalization;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;
using BotNexus.Memory.Tools;
using Moq;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests.Tools;

/// <summary>
/// Covers #2781: the tool used to render the loop ordinal under a <c>Score:</c> label, so a caller
/// could not tell a strong match from the best row of an irrelevant set.
/// </summary>
/// <remarks>
/// The assertions deliberately parse the emitted number rather than string-matching a formatted
/// literal. An ordinal and a fused score are both "a number on the Score line" - only comparing the
/// parsed values across two results of different relevance can prove which one is being emitted.
/// </remarks>
public sealed class MemorySearchToolScoreTests
{
    private const string AgentId = "agent-a";

    /// <summary>Matches the numeric payload of a rendered score line.</summary>
    private static readonly Regex ScoreLine = new(@"Score:\s*(?<value>[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    private static MemorySearchTool CreateTool(params AgentMemorySearchResult[] results)
    {
        var agentMemory = new Mock<IAgentMemory>();
        agentMemory
            .Setup(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results.ToList());
        return new MemorySearchTool(agentMemory.Object, AgentId);
    }

    private static AgentMemorySearchResult Result(string id, double score)
        => new(id, $"content of {id}", "conversation", null, DateTimeOffset.UtcNow, score);

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;

    private static IReadOnlyList<double> ParsedScores(string text)
        => ScoreLine.Matches(text)
            .Select(match => double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture))
            .ToList();

    // ---- Clause 1: the numeric fused score is present and reflects relevance ----

    [Fact]
    public async Task ExecuteAsync_RendersNumericFusedScoreForEachResult()
    {
        var tool = CreateTool(Result("high", 0.8125d), Result("low", 0.1875d));

        var text = GetText(await tool.ExecuteAsync("call", new Dictionary<string, object?> { ["query"] = "anything" }));

        var scores = ParsedScores(text);
        scores.Count.ShouldBe(2);
        scores[0].ShouldBe(0.8125d, tolerance: 0.0001d);
        scores[1].ShouldBe(0.1875d, tolerance: 0.0001d);
    }

    // ---- Clause 2: the emitted value is NOT the ordinal ----

    [Fact]
    public async Task ExecuteAsync_TwoResultsWithDifferentFusedScores_RenderDifferentNumbers()
    {
        var tool = CreateTool(Result("high", 0.9d), Result("low", 0.25d));

        var text = GetText(await tool.ExecuteAsync("call", new Dictionary<string, object?> { ["query"] = "anything" }));

        var scores = ParsedScores(text);
        scores.Count.ShouldBe(2);

        // The ordinal rendering emits 1 then 2. The fused rendering emits a descending pair of
        // fractions. Both assertions are needed: the first kills "#1/#2", the second kills any
        // rendering that happens to be numeric but constant.
        scores.ShouldNotBe([1d, 2d]);
        scores[0].ShouldBeGreaterThan(scores[1]);
        scores[0].ShouldBe(0.9d, tolerance: 0.0001d);
        scores[1].ShouldBe(0.25d, tolerance: 0.0001d);
    }

    [Fact]
    public async Task ExecuteAsync_EqualOrdinalPositions_StillCarryDistinctScoresAcrossQueries()
    {
        // The top result of a strong set and the top result of a weak set share ordinal #1.
        // Under the defect they rendered identically; the score must separate them.
        var strong = CreateTool(Result("strong", 0.95d));
        var weak = CreateTool(Result("weak", 0.05d));

        var strongText = GetText(await strong.ExecuteAsync("c1", new Dictionary<string, object?> { ["query"] = "q" }));
        var weakText = GetText(await weak.ExecuteAsync("c2", new Dictionary<string, object?> { ["query"] = "q" }));

        ParsedScores(strongText).Single().ShouldBeGreaterThan(ParsedScores(weakText).Single());
    }

    // ---- Clause 3: minScore is accepted, documented in the schema, and excludes low rows ----

    [Fact]
    public void Definition_DeclaresOptionalMinScoreParameter()
    {
        var tool = CreateTool();

        var schema = tool.Definition.Parameters;
        var properties = schema.GetProperty("properties");

        properties.TryGetProperty("minScore", out var minScore).ShouldBeTrue();
        minScore.GetProperty("type").GetString().ShouldBe("number");
        minScore.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();

        // Optional: it must not be added to the required set.
        schema.GetProperty("required").EnumerateArray()
            .Select(element => element.GetString())
            .ShouldNotContain("minScore");
    }

    [Fact]
    public async Task ExecuteAsync_WithMinScore_ExcludesResultsBelowTheFloor()
    {
        var tool = CreateTool(Result("keep", 0.7d), Result("drop", 0.2d));

        var text = GetText(await tool.ExecuteAsync(
            "call",
            new Dictionary<string, object?> { ["query"] = "anything", ["minScore"] = 0.5d }));

        text.ShouldContain("ID: keep");
        text.ShouldNotContain("ID: drop");
        ParsedScores(text).Single().ShouldBe(0.7d, tolerance: 0.0001d);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_AcceptsMinScore()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["query"] = "anything",
            ["minScore"] = 0.42d
        });

        prepared.ShouldContainKey("minScore");
        Convert.ToDouble(prepared["minScore"], CultureInfo.InvariantCulture).ShouldBe(0.42d, tolerance: 0.0001d);
    }

    // ---- Clause 4: everything below the floor yields an empty result set ----

    [Fact]
    public async Task ExecuteAsync_WithMinScoreAboveEveryCandidate_ReturnsEmptyResultSet()
    {
        var tool = CreateTool(Result("a", 0.3d), Result("b", 0.2d), Result("c", 0.1d));

        var text = GetText(await tool.ExecuteAsync(
            "call",
            new Dictionary<string, object?> { ["query"] = "anything", ["minScore"] = 0.9d }));

        // Not a truncated ranked list: no entries at all.
        text.ShouldBe("No matching memories found.");
        text.ShouldNotContain("ID:");
        ParsedScores(text).ShouldBeEmpty();
    }

    // ---- End-to-end: the score is the store's, not one recomputed in the display path ----

    [Fact]
    public async Task ExecuteAsync_OverRealStore_EmitsNonOrdinalScoreFromTheRanker()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        // The corpus shape here is load-bearing, and the arithmetic is worth writing down because a
        // naive fixture silently yields a zero score for a genuine match:
        //
        //   SQLite's bm25() weights each term by IDF = log((N - n + 0.5) / (n + 0.5)), where N is the
        //   number of indexed rows and n the number containing the term. When a term is COMMON the
        //   ratio drops below 1, IDF goes negative, bm25() flips sign, and the store's
        //   `Math.Max(0d, -bm25(...))` clamp - which is correct, a negative lexical magnitude is
        //   meaningless - floors the candidate at 0.
        //
        // So the query term must be RARE: here it appears in 2 of 12 rows, giving
        // log(10.5 / 2.5) > 0 and a genuinely positive lexical signal. The two matching rows carry
        // different term frequencies so the ranker must also separate them.
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "entry-1", AgentId, "searchablememorytext searchablememorytext searchablememorytext deployment rollback"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "entry-2", AgentId, "searchablememorytext buried among a great deal of unrelated filler prose"));
        for (var i = 0; i < 10; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"filler-{i}", AgentId, $"wholly unrelated content about gardening and weather number {i}"));
        }

        var agentMemory = new MarkdownAgentMemory(AgentId, new StubWorkspaceManager(), context.Store, new FileSystem());
        var tool = new MemorySearchTool(agentMemory, AgentId);

        var text = GetText(await tool.ExecuteAsync(
            "call",
            new Dictionary<string, object?> { ["query"] = "searchablememorytext" }));

        text.ShouldContain("ID: entry-1");
        var scores = ParsedScores(text);

        // A real fused magnitude from the ranker, not the ordinal the defect emitted.
        scores.Count.ShouldBe(2);
        scores[0].ShouldBeGreaterThan(0d);
        scores.ShouldNotBe([1d, 2d]);
        scores[0].ShouldBeGreaterThan(scores[1]);
    }

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: "", Identity: "", User: "", Memory: ""));
        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;
        public string GetWorkspacePath(string agentName) => $@"C:\agents\{agentName}\workspace";
    }
}
