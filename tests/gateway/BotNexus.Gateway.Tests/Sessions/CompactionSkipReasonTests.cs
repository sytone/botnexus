using System.Text.Json;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2489: <see cref="CompactionSkipReason"/> is a class-based smart enum whose <c>Value</c> strings
/// are a LOG AND TELEMETRY CONTRACT - production log queries match on these exact tokens. These
/// tests pin all eight literals EXPLICITLY (as string literals, never via the member itself) so a
/// rename of any member's wire value fails the build/tests rather than silently breaking log
/// queries, and assert the JSON round-trip and forward-compatible unknown-value resolution.
/// </summary>
public sealed class CompactionSkipReasonTests
{
    [Fact]
    public void EveryDeclaredReason_HasItsExactPinnedWireValue()
    {
        // Pinned literals: these are the eight codes merged in PR #2465 (#2460). Renaming any of
        // them breaks existing log/telemetry queries, so they are duplicated here on purpose.
        CompactionSkipReason.CircuitBreakerOpen.Value.ShouldBe("CircuitBreakerOpen");
        CompactionSkipReason.EmptyHistory.Value.ShouldBe("EmptyHistory");
        CompactionSkipReason.HistoryReadFailed.Value.ShouldBe("HistoryReadFailed");
        CompactionSkipReason.NoSummarizableTurns.Value.ShouldBe("NoSummarizableTurns");
        CompactionSkipReason.SummarizationTimeout.Value.ShouldBe("SummarizationTimeout");
        CompactionSkipReason.EmptySummary.Value.ShouldBe("EmptySummary");
        CompactionSkipReason.SummarizationFailed.Value.ShouldBe("SummarizationFailed");
        CompactionSkipReason.ConcurrentHistoryChange.Value.ShouldBe("ConcurrentHistoryChange");
        CompactionSkipReason.SessionRebound.Value.ShouldBe("SessionRebound");
    }

    [Fact]
    public void EveryDeclaredReason_ToStringMatchesItsPinnedWireValue()
    {
        // ToString() is what the coordinator's structured log line renders, so it is pinned too.
        CompactionSkipReason.CircuitBreakerOpen.ToString().ShouldBe("CircuitBreakerOpen");
        CompactionSkipReason.EmptyHistory.ToString().ShouldBe("EmptyHistory");
        CompactionSkipReason.HistoryReadFailed.ToString().ShouldBe("HistoryReadFailed");
        CompactionSkipReason.NoSummarizableTurns.ToString().ShouldBe("NoSummarizableTurns");
        CompactionSkipReason.SummarizationTimeout.ToString().ShouldBe("SummarizationTimeout");
        CompactionSkipReason.EmptySummary.ToString().ShouldBe("EmptySummary");
        CompactionSkipReason.SummarizationFailed.ToString().ShouldBe("SummarizationFailed");
        CompactionSkipReason.ConcurrentHistoryChange.ToString().ShouldBe("ConcurrentHistoryChange");
        CompactionSkipReason.SessionRebound.ToString().ShouldBe("SessionRebound");
    }

    [Theory]
    [InlineData("CircuitBreakerOpen")]
    [InlineData("EmptyHistory")]
    [InlineData("HistoryReadFailed")]
    [InlineData("NoSummarizableTurns")]
    [InlineData("SummarizationTimeout")]
    [InlineData("EmptySummary")]
    [InlineData("SummarizationFailed")]
    [InlineData("ConcurrentHistoryChange")]
    [InlineData("SessionRebound")]
    public void FromString_ResolvesEachPinnedWireValue_ToTheCanonicalDeclaredMember(string wireValue)
    {
        var resolved = CompactionSkipReason.FromString(wireValue);

        resolved.Value.ShouldBe(wireValue);
        // Reference equality proves it resolved to the DECLARED static member and did not silently
        // register a new lookalike entry.
        resolved.ShouldBeSameAs(DeclaredMemberFor(wireValue));
    }

    [Theory]
    [InlineData("CircuitBreakerOpen")]
    [InlineData("EmptyHistory")]
    [InlineData("HistoryReadFailed")]
    [InlineData("NoSummarizableTurns")]
    [InlineData("SummarizationTimeout")]
    [InlineData("EmptySummary")]
    [InlineData("SummarizationFailed")]
    [InlineData("ConcurrentHistoryChange")]
    [InlineData("SessionRebound")]
    public void JsonRoundTrip_PreservesTheExactWireValue(string wireValue)
    {
        var reason = DeclaredMemberFor(wireValue);

        var json = JsonSerializer.Serialize(reason);

        // Serialized form must be the bare quoted wire token - byte-identical to the const string
        // era, so persisted results and log/telemetry sinks are unchanged.
        json.ShouldBe($"\"{wireValue}\"");

        var deserialized = JsonSerializer.Deserialize<CompactionSkipReason>(json);
        deserialized.ShouldNotBeNull();
        deserialized.Value.ShouldBe(wireValue);
        deserialized.ShouldBeSameAs(reason);
    }

    [Fact]
    public void FromString_UnknownForwardValue_ResolvesInsteadOfThrowing()
    {
        // AC4: a newer gateway emitting a code this build has never seen must round-trip through an
        // older reader rather than blowing up.
        var forward = CompactionSkipReason.FromString("SomeFutureReasonFrom2999");

        forward.Value.ShouldBe("SomeFutureReasonFrom2999");
        forward.ShouldBe(CompactionSkipReason.FromString("SomeFutureReasonFrom2999"));
        JsonSerializer.Serialize(forward).ShouldBe("\"SomeFutureReasonFrom2999\"");
    }

    [Fact]
    public void JsonDeserialize_UnknownForwardValue_ResolvesInsteadOfThrowing()
    {
        var forward = JsonSerializer.Deserialize<CompactionSkipReason>("\"AnotherFutureCode\"");

        forward.ShouldNotBeNull();
        forward.Value.ShouldBe("AnotherFutureCode");
    }

    [Fact]
    public void FromString_IsCaseInsensitive_AndKeepsTheCanonicalPascalCaseWireValue()
    {
        // A differently-cased spelling must NOT create a second registry entry, and must NOT
        // rewrite the canonical PascalCase contract value to lower-case.
        var resolved = CompactionSkipReason.FromString("nosummarizableturns");

        resolved.ShouldBeSameAs(CompactionSkipReason.NoSummarizableTurns);
        resolved.Value.ShouldBe("NoSummarizableTurns");
    }

    [Fact]
    public void FromString_BlankValue_Throws()
    {
        Should.Throw<ArgumentException>(() => CompactionSkipReason.FromString(""));
        Should.Throw<ArgumentException>(() => CompactionSkipReason.FromString("   "));
    }

    [Fact]
    public void FromNullableString_MapsBlankToNull_AndNonBlankToTheDeclaredMember()
    {
        CompactionSkipReason.FromNullableString(null).ShouldBeNull();
        CompactionSkipReason.FromNullableString("").ShouldBeNull();
        CompactionSkipReason.FromNullableString("   ").ShouldBeNull();
        CompactionSkipReason.FromNullableString("EmptyHistory").ShouldBeSameAs(CompactionSkipReason.EmptyHistory);
    }

    [Fact]
    public void DeclaredMembers_AreDistinct_AndNumberExactlyNine()
    {
        var declared = new[]
        {
            CompactionSkipReason.CircuitBreakerOpen,
            CompactionSkipReason.EmptyHistory,
            CompactionSkipReason.HistoryReadFailed,
            CompactionSkipReason.NoSummarizableTurns,
            CompactionSkipReason.SummarizationTimeout,
            CompactionSkipReason.EmptySummary,
            CompactionSkipReason.SummarizationFailed,
            CompactionSkipReason.ConcurrentHistoryChange,
            CompactionSkipReason.SessionRebound,
        };

        // #3362 added HistoryReadFailed (eight -> nine). The count is pinned so a new member is a
        // deliberate, reviewed contract change rather than a silent addition.
        declared.Length.ShouldBe(9);
        declared.Select(r => r.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(9);
    }

    [Fact]
    public void SkipReason_FlowsThroughCompactionResult_AsTheTypedValue()
    {
        var result = CompactionResult.Skipped(skipReason: CompactionSkipReason.NoSummarizableTurns);

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldNotBeNull();
        result.SkipReason.ShouldBeSameAs(CompactionSkipReason.NoSummarizableTurns);
        result.SkipReason.Value.ShouldBe("NoSummarizableTurns");
    }

    private static CompactionSkipReason DeclaredMemberFor(string wireValue) => wireValue switch
    {
        "CircuitBreakerOpen" => CompactionSkipReason.CircuitBreakerOpen,
        "EmptyHistory" => CompactionSkipReason.EmptyHistory,
        "HistoryReadFailed" => CompactionSkipReason.HistoryReadFailed,
        "NoSummarizableTurns" => CompactionSkipReason.NoSummarizableTurns,
        "SummarizationTimeout" => CompactionSkipReason.SummarizationTimeout,
        "EmptySummary" => CompactionSkipReason.EmptySummary,
        "SummarizationFailed" => CompactionSkipReason.SummarizationFailed,
        "ConcurrentHistoryChange" => CompactionSkipReason.ConcurrentHistoryChange,
        "SessionRebound" => CompactionSkipReason.SessionRebound,
        _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue, "Not a declared skip reason."),
    };
}
