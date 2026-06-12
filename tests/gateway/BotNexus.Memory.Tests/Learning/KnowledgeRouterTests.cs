using BotNexus.Memory.Learning;

namespace BotNexus.Memory.Tests.Learning;

public sealed class KnowledgeRouterTests
{
    [Fact]
    public void Route_MatchingCategoryAndConfidence_ReturnsTargetStore()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = KnowledgeCategory.Decision, MinConfidence = 0.6, TargetStore = "platform-decisions" },
        };
        var router = new KnowledgeRouter(rules);

        var knowledge = new ExtractedKnowledge
        {
            Content = "We decided X",
            Category = KnowledgeCategory.Decision,
            Confidence = 0.8,
            SourceSessionId = "s1",
            SourceTurnIndex = 0,
        };

        Assert.Equal("platform-decisions", router.Route(knowledge));
    }

    [Fact]
    public void Route_ConfidenceBelowThreshold_ReturnsNull()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = KnowledgeCategory.Decision, MinConfidence = 0.9, TargetStore = "decisions" },
        };
        var router = new KnowledgeRouter(rules);

        var knowledge = new ExtractedKnowledge
        {
            Content = "We decided X",
            Category = KnowledgeCategory.Decision,
            Confidence = 0.7,
            SourceSessionId = "s1",
            SourceTurnIndex = 0,
        };

        Assert.Null(router.Route(knowledge));
    }

    [Fact]
    public void Route_WrongCategory_ReturnsNull()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = KnowledgeCategory.Decision, MinConfidence = 0.5, TargetStore = "decisions" },
        };
        var router = new KnowledgeRouter(rules);

        var knowledge = new ExtractedKnowledge
        {
            Content = "The path is /usr/local",
            Category = KnowledgeCategory.Fact,
            Confidence = 0.8,
            SourceSessionId = "s1",
            SourceTurnIndex = 0,
        };

        Assert.Null(router.Route(knowledge));
    }

    [Fact]
    public void Route_NullCategoryRule_MatchesAnyCategory()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = null, MinConfidence = 0.5, TargetStore = "catch-all" },
        };
        var router = new KnowledgeRouter(rules);

        var knowledge = new ExtractedKnowledge
        {
            Content = "Something",
            Category = KnowledgeCategory.Pattern,
            Confidence = 0.7,
            SourceSessionId = "s1",
            SourceTurnIndex = 0,
        };

        Assert.Equal("catch-all", router.Route(knowledge));
    }

    [Fact]
    public void Route_FirstMatchingRuleWins()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = KnowledgeCategory.Decision, MinConfidence = 0.5, TargetStore = "first" },
            new() { Category = null, MinConfidence = 0.3, TargetStore = "second" },
        };
        var router = new KnowledgeRouter(rules);

        var knowledge = new ExtractedKnowledge
        {
            Content = "Decided",
            Category = KnowledgeCategory.Decision,
            Confidence = 0.6,
            SourceSessionId = "s1",
            SourceTurnIndex = 0,
        };

        Assert.Equal("first", router.Route(knowledge));
    }

    [Fact]
    public void RouteAll_AppliesRoutingToAllItems()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = KnowledgeCategory.Decision, MinConfidence = 0.5, TargetStore = "decisions" },
        };
        var router = new KnowledgeRouter(rules);

        var items = new[]
        {
            new ExtractedKnowledge { Content = "A", Category = KnowledgeCategory.Decision, Confidence = 0.8, SourceSessionId = "s1", SourceTurnIndex = 0 },
            new ExtractedKnowledge { Content = "B", Category = KnowledgeCategory.Fact, Confidence = 0.8, SourceSessionId = "s1", SourceTurnIndex = 1 },
        };

        var results = router.RouteAll(items);
        Assert.Equal("decisions", results[0].TargetStore);
        Assert.Null(results[1].TargetStore);
    }

    [Fact]
    public void Route_NullKnowledge_ThrowsArgumentNullException()
    {
        var router = new KnowledgeRouter([]);
        Assert.Throws<ArgumentNullException>(() => router.Route(null!));
    }
}
