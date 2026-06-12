using BotNexus.Memory.Learning;

namespace BotNexus.Memory.Tests.Learning;

public sealed class TurnClassifierTests
{
    [Fact]
    public void Classify_ShortGreeting_ReturnsTransient()
    {
        var result = TurnClassifier.Classify("hi", "hello! how can I help?");
        Assert.False(result.IsDurable);
        Assert.Null(result.Category);
    }

    [Fact]
    public void Classify_ShortExchange_ReturnsTransient()
    {
        var result = TurnClassifier.Classify("ok", "got it");
        Assert.False(result.IsDurable);
        Assert.Null(result.Category);
    }

    [Fact]
    public void Classify_DecisionContent_ReturnsDurableDecision()
    {
        var user = "Should we use PostgreSQL or SQLite for the session store?";
        var assistant = "We decided to go with SQLite for the session store because it requires no external dependencies and performs well for single-node deployments. This decision means we trade off horizontal scaling.";

        var result = TurnClassifier.Classify(user, assistant);

        Assert.True(result.IsDurable);
        Assert.Equal(KnowledgeCategory.Decision, result.Category);
        Assert.True(result.Confidence > 0.3);
    }

    [Fact]
    public void Classify_PatternContent_ReturnsDurablePattern()
    {
        var user = "What's the convention for branch names?";
        var assistant = "The standard convention is to always use feat/<slug> or fix/<slug> for branch naming. We never put issue numbers in branch names. This pattern applies consistently across all repos.";

        var result = TurnClassifier.Classify(user, assistant);

        Assert.True(result.IsDurable);
        Assert.Equal(KnowledgeCategory.Pattern, result.Category);
    }

    [Fact]
    public void Classify_FactContent_ReturnsDurableFact()
    {
        var user = "Where is the BotNexus config file?";
        var assistant = "The config file is located at ~/.botnexus/config.json. This is the only platform config file. Agent descriptors live at ~/.botnexus/agents/{id}.json and workspaces at ~/.botnexus/agents/{id}/workspace/.";

        var result = TurnClassifier.Classify(user, assistant);

        Assert.True(result.IsDurable);
        Assert.Equal(KnowledgeCategory.Fact, result.Category);
    }

    [Fact]
    public void Classify_ProcedureContent_ReturnsDurableProcedure()
    {
        var user = "How do I deploy a new version?";
        var assistant = "To do this, first run the build script. Then execute dotnet publish to create the artifacts. Finally, use the deploy command to push to production. Step 1 generates the binaries, step 2 packages them.";

        var result = TurnClassifier.Classify(user, assistant);

        Assert.True(result.IsDurable);
        Assert.Equal(KnowledgeCategory.Procedure, result.Category);
    }

    [Fact]
    public void Classify_PreferenceContent_ReturnsDurablePreference()
    {
        var user = "How should status updates look?";
        var assistant = "I prefer bullet points over paragraphs for status updates. I'd rather have terse, direct communication instead of verbose explanations. Avoid long-winded summaries.";

        var result = TurnClassifier.Classify(user, assistant);

        Assert.True(result.IsDurable);
        Assert.Equal(KnowledgeCategory.Preference, result.Category);
    }

    [Fact]
    public void Classify_LongContentNoKeywords_ReturnsDurableWithLowConfidence()
    {
        var user = "Tell me about the architecture of the system and how all the pieces connect together in detail.";
        var assistant = new string('x', 250); // Long but no keywords

        var result = TurnClassifier.Classify(user, assistant);

        Assert.True(result.IsDurable);
        Assert.True(result.Confidence <= 0.5);
    }

    [Fact]
    public void Classify_NullUserContent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TurnClassifier.Classify(null!, "response"));
    }

    [Fact]
    public void Classify_NullAssistantContent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TurnClassifier.Classify("question", null!));
    }
}
