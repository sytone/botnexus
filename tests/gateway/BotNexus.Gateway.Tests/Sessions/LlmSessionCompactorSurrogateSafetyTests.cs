using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3187: <c>LlmSessionCompactor</c> bounded the LLM-generated summary with a raw UTF-16 range
/// slice at <c>MaxSummaryChars</c>. That cut can land between the high and the low surrogate of an
/// astral-plane character - an emoji in a model-written summary is entirely ordinary - and the
/// result is then <em>persisted</em> into session history. A lone surrogate that reaches storage is
/// unrepairable, because the untruncated summary is discarded at the same moment (#2883).
/// </summary>
public sealed class LlmSessionCompactorSurrogateSafetyTests
{
    /// <summary>U+1F600 GRINNING FACE - two UTF-16 code units, the smallest astral test case.</summary>
    private const string Grinning = "\U0001F600";

    private const int MaxSummaryChars = 40;

    [Fact]
    public async Task CompactAsync_AstralCharacterStraddlingTheLimit_PersistsNoLoneSurrogate()
    {
        // The emoji starts at index MaxSummaryChars - 1, so a raw slice at MaxSummaryChars keeps
        // its high surrogate and drops its low surrogate. This is the exact defect shape.
        var summary = new string('a', MaxSummaryChars - 1) + Grinning + new string('b', 50);
        char.IsHighSurrogate(summary[MaxSummaryChars - 1]).ShouldBeTrue();
        char.IsLowSurrogate(summary[MaxSummaryChars]).ShouldBeTrue();

        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));
        var compactor = CreateCompactor(summary);

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            MaxSummaryChars = MaxSummaryChars,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.Summary.ShouldNotBeNull();
        HasUnpairedSurrogate(result.Summary!).ShouldBeFalse(
            "#3187: the truncated compaction summary must not contain a lone surrogate.");
        result.Summary!.Length.ShouldBeLessThan(summary.Length);

        // The persisted entry is the copy that survives; assert the invariant there too, since the
        // original summary is gone once history is replaced.
        session.ReplaceHistory(result.CompactedHistory!);
        var persisted = session.GetHistorySnapshot().Single(e => e.IsCompactionSummary);
        HasUnpairedSurrogate(persisted.Content).ShouldBeFalse(
            "#3187: the summary persisted into session history must not contain a lone surrogate.");
    }

    [Fact]
    public async Task CompactAsync_SummaryAtTheLimit_IsUnchangedAndCarriesNoMarker()
    {
        var summary = new string('a', MaxSummaryChars);

        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));
        var compactor = CreateCompactor(summary);

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            MaxSummaryChars = MaxSummaryChars,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.Summary.ShouldBe(summary);
    }

    /// <summary>
    /// Scans for a surrogate that is not part of a well-formed pair. This is the direct expression
    /// of the invariant; a substring check for the emoji would pass on a string that also contained
    /// an unpaired surrogate elsewhere.
    /// </summary>
    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return true;
                i++;
                continue;
            }

            if (char.IsLowSurrogate(value[i]))
                return true;
        }

        return false;
    }

    private static readonly LlmModel TestModel = new(
        Id: "test-model",
        Name: "Test Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    private static GatewaySession CreateSession(params (string role, string content)[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = AgentId.From("agent")
        };

        session.AddEntries(entries.Select(entry => new SessionEntry
        {
            Role = entry.role,
            Content = entry.content
        }));

        return session;
    }

    private static LlmSessionCompactor CreateCompactor(string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns(() => CreateStream(summary));

        providers.Register(provider.Object);

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }

    private static LlmStream CreateStream(string summary)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(summary)],
            Api: TestModel.Api,
            Provider: TestModel.Provider,
            ModelId: TestModel.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }
}
