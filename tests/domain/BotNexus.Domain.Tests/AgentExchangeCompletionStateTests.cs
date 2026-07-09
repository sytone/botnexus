using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Covers the typed <see cref="AgentExchangeCompletionState"/> promoted from the four loose
/// exchange-completion metadata keys (issue #612, CC-1): typed round-trip through the
/// <see cref="Session.Metadata"/> backing store, migrate-on-read from legacy loose keys, and the
/// invariant that writes always clear the legacy keys so a stale value can never shadow the blob.
/// </summary>
public sealed class AgentExchangeCompletionStateTests
{
    [Fact]
    public void ExchangeCompletion_TypedRoundTrip_SurvivesMetadataProjection()
    {
        var session = new Session
        {
            ExchangeCompletion = new AgentExchangeCompletionState
            {
                ActiveExchangeId = "ex-1",
                FinishedExchangeId = "ex-1",
                FinishedReason = "done",
                FinishedSummary = "all good"
            }
        };

        var read = session.ExchangeCompletion;

        read.ShouldNotBeNull();
        read!.ActiveExchangeId.ShouldBe("ex-1");
        read.FinishedExchangeId.ShouldBe("ex-1");
        read.FinishedReason.ShouldBe("done");
        read.FinishedSummary.ShouldBe("all good");
    }

    [Fact]
    public void ExchangeCompletion_MigratesOnRead_FromLegacyLooseKeys()
    {
        // Simulate a pre-CC-1 persisted row that still carries the four loose string keys.
        var session = new Session();
        session.Metadata[AgentExchangeCompletionState.LegacyActiveExchangeIdKey] = "legacy-active";
        session.Metadata[AgentExchangeCompletionState.LegacyFinishedExchangeIdKey] = "legacy-finished";
        session.Metadata[AgentExchangeCompletionState.LegacyFinishedReasonKey] = "legacy-reason";
        session.Metadata[AgentExchangeCompletionState.LegacyFinishedSummaryKey] = "legacy-summary";

        var read = session.ExchangeCompletion;

        read.ShouldNotBeNull();
        read!.ActiveExchangeId.ShouldBe("legacy-active");
        read.FinishedExchangeId.ShouldBe("legacy-finished");
        read.FinishedReason.ShouldBe("legacy-reason");
        read.FinishedSummary.ShouldBe("legacy-summary");
    }

    [Fact]
    public void ExchangeCompletion_MigratesOnRead_FromJsonElementLegacyKeys()
    {
        // Sqlite/File stores round-trip metadata values as JsonElement on read.
        var session = new Session();
        using var doc = JsonDocument.Parse("\"json-active\"");
        session.Metadata[AgentExchangeCompletionState.LegacyActiveExchangeIdKey] = doc.RootElement.Clone();

        var read = session.ExchangeCompletion;

        read.ShouldNotBeNull();
        read!.ActiveExchangeId.ShouldBe("json-active");
    }

    [Fact]
    public void ExchangeCompletion_Write_ClearsLegacyLooseKeys()
    {
        var session = new Session();
        session.Metadata[AgentExchangeCompletionState.LegacyActiveExchangeIdKey] = "stale";
        session.Metadata[AgentExchangeCompletionState.LegacyFinishedReasonKey] = "stale-reason";

        session.ExchangeCompletion = new AgentExchangeCompletionState { ActiveExchangeId = "fresh" };

        session.Metadata.ShouldNotContainKey(AgentExchangeCompletionState.LegacyActiveExchangeIdKey);
        session.Metadata.ShouldNotContainKey(AgentExchangeCompletionState.LegacyFinishedReasonKey);
        session.Metadata.ShouldContainKey(AgentExchangeCompletionState.MetadataKey);
        session.ExchangeCompletion!.ActiveExchangeId.ShouldBe("fresh");
    }

    [Fact]
    public void ExchangeCompletion_NoState_ReadsAsNull()
    {
        new Session().ExchangeCompletion.ShouldBeNull();
    }

    [Fact]
    public void ExchangeCompletion_SetNull_ClearsCanonicalAndLegacyKeys()
    {
        var session = new Session
        {
            ExchangeCompletion = new AgentExchangeCompletionState { ActiveExchangeId = "ex-1" }
        };
        session.Metadata.ShouldContainKey(AgentExchangeCompletionState.MetadataKey);

        session.ExchangeCompletion = null;

        session.Metadata.ShouldNotContainKey(AgentExchangeCompletionState.MetadataKey);
        session.ExchangeCompletion.ShouldBeNull();
    }

    [Fact]
    public void FromMetadata_PrefersCanonicalBlob_OverLegacyKeys()
    {
        var session = new Session
        {
            ExchangeCompletion = new AgentExchangeCompletionState { ActiveExchangeId = "canonical" }
        };
        // A stray legacy key should not shadow the canonical blob.
        session.Metadata[AgentExchangeCompletionState.LegacyActiveExchangeIdKey] = "legacy";

        AgentExchangeCompletionState.FromMetadata(session.Metadata)!.ActiveExchangeId.ShouldBe("canonical");
    }

    [Fact]
    public void IsEmpty_TrueOnlyWhenAllFieldsNull()
    {
        new AgentExchangeCompletionState().IsEmpty.ShouldBeTrue();
        new AgentExchangeCompletionState { FinishedSummary = "x" }.IsEmpty.ShouldBeFalse();
    }
}
