using BotNexus.Memory.Models;
using BotNexus.Memory.Tools;

namespace BotNexus.Memory.Tests;

/// <summary>
/// The trust derivation and its policy (#3232). These tests are the specification of the table in
/// the issue: every vocabulary value maps to exactly one tier, and the two non-first-party tiers
/// stay distinguishable from each other.
/// </summary>
public sealed class MemoryTrustTests
{
    [Theory]
    [InlineData(MemoryProvenance.User, MemoryTrustTier.Trusted)]
    [InlineData(MemoryProvenance.Agent, MemoryTrustTier.Derived)]
    [InlineData(MemoryProvenance.Tool, MemoryTrustTier.Derived)]
    [InlineData(MemoryProvenance.Unknown, MemoryTrustTier.Untrusted)]
    [InlineData(MemoryProvenance.ExternalUntrusted, MemoryTrustTier.Quarantined)]
    public void Derive_MapsEveryVocabularyValue_ToItsDocumentedTier(string provenance, MemoryTrustTier expected)
    {
        // AC1: the derivation is total over the closed vocabulary, and this is the table.
        Assert.Equal(expected, MemoryTrust.Derive(provenance));
    }

    [Fact]
    public void Derive_CoversEveryMemberOfTheClosedVocabulary()
    {
        // Anti-vacuity guard on the theory above: if #2480 ever adds a sixth provenance value,
        // this fails rather than letting the new value silently inherit the fallback tier.
        Assert.Equal(5, MemoryProvenance.All.Count);
        Assert.All(MemoryProvenance.All, value => Assert.True(Enum.IsDefined(MemoryTrust.Derive(value))));
    }

    [Fact]
    public void Derive_KeepsUnknownAndExternalUntrusted_AsDistinctTiers()
    {
        // The issue is explicit that collapsing these either over-trusts the pre-provenance corpus
        // or makes it entirely unreachable. Neither is acceptable, so they must not be equal.
        Assert.NotEqual(
            MemoryTrust.Derive(MemoryProvenance.Unknown),
            MemoryTrust.Derive(MemoryProvenance.ExternalUntrusted));
    }

    [Fact]
    public void Derive_TreatsNeitherUnknownNorExternalUntrusted_AsFirstParty()
    {
        Assert.False(MemoryTrust.IsFirstParty(MemoryTrust.Derive(MemoryProvenance.Unknown)));
        Assert.False(MemoryTrust.IsFirstParty(MemoryTrust.Derive(MemoryProvenance.ExternalUntrusted)));
        Assert.True(MemoryTrust.IsFirstParty(MemoryTrust.Derive(MemoryProvenance.User)));
        Assert.True(MemoryTrust.IsFirstParty(MemoryTrust.Derive(MemoryProvenance.Agent)));
        Assert.True(MemoryTrust.IsFirstParty(MemoryTrust.Derive(MemoryProvenance.Tool)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trusted")]
    [InlineData("USER; DROP TABLE")]
    public void Derive_FailsSafeToUntrusted_ForNullOrUnrecognisedValues(string? provenance)
    {
        // AC9 and the sad path: a hostile or malformed column value must never invent a trusted
        // tier. Note "trusted" itself is NOT a provenance value - a row claiming it gets nothing.
        Assert.Equal(MemoryTrustTier.Untrusted, MemoryTrust.Derive(provenance));
    }

    [Fact]
    public void Derive_IsCaseInsensitive_MatchingProvenanceNormalisation()
    {
        Assert.Equal(MemoryTrustTier.Quarantined, MemoryTrust.Derive("External-Untrusted"));
        Assert.Equal(MemoryTrustTier.Trusted, MemoryTrust.Derive("  USER  "));
    }

    [Fact]
    public void RankWeight_OrdersTiers_StrictlyByTrust()
    {
        // The ordering is the load-bearing property, not the exact constants: first-party is
        // undiscounted, unestablished is discounted, hostile-capable is discounted further, and
        // nothing reaches zero because a zero weight is a silent pre-rank drop by another name.
        var trusted = MemoryTrust.RankWeight(MemoryTrustTier.Trusted);
        var derived = MemoryTrust.RankWeight(MemoryTrustTier.Derived);
        var untrusted = MemoryTrust.RankWeight(MemoryTrustTier.Untrusted);
        var quarantined = MemoryTrust.RankWeight(MemoryTrustTier.Quarantined);

        Assert.Equal(trusted, derived);
        Assert.True(trusted > untrusted, "first-party must outweigh unestablished");
        Assert.True(untrusted > quarantined, "unestablished must outweigh hostile-capable");
        Assert.True(quarantined > 0d, "no tier may be weighted to zero - that is a silent drop");
    }

    [Fact]
    public void LeastTrusted_OfAMixture_TakesTheWorstContributor_NotTheMostCommon()
    {
        // AC3, and the whole point: four trusted contributors and one hostile one resolve to
        // hostile. A majority vote would return `user` here, which is the defect.
        var tier = MemoryTrust.LeastTrusted([
            MemoryProvenance.User,
            MemoryProvenance.User,
            MemoryProvenance.Agent,
            MemoryProvenance.Tool,
            MemoryProvenance.ExternalUntrusted
        ]);

        Assert.Equal(MemoryTrustTier.Quarantined, tier);
    }

    [Fact]
    public void LeastTrusted_OfAllFirstPartyContributors_StaysFirstParty()
    {
        // The happy path must still exist, or the rule would simply quarantine everything.
        var tier = MemoryTrust.LeastTrusted([MemoryProvenance.User, MemoryProvenance.Agent, MemoryProvenance.Tool]);

        Assert.Equal(MemoryTrustTier.Derived, tier);
        Assert.True(MemoryTrust.IsFirstParty(tier));
    }

    [Fact]
    public void LeastTrusted_OfAnEmptyContributorSet_IsUntrusted_NotTrusted()
    {
        // "We recorded no sources" is an unestablished origin, and is exactly the shape a
        // summariser that forgot to record its contributors produces. It must not fail open.
        Assert.Equal(MemoryTrustTier.Untrusted, MemoryTrust.LeastTrusted([]));
    }

    [Fact]
    public void ResolveDerivedProvenance_StampsASummary_WithItsWorstContributor()
    {
        // AC3 at the write boundary: the stamped value must round-trip back to the resolved tier,
        // so a summary can never be stored more trusted than what went into it.
        var stamped = MemoryTrust.ResolveDerivedProvenance([MemoryProvenance.Agent, MemoryProvenance.ExternalUntrusted]);

        Assert.Equal(MemoryProvenance.ExternalUntrusted, stamped);
        Assert.Equal(MemoryTrustTier.Quarantined, MemoryTrust.Derive(stamped));
    }

    [Fact]
    public void ResolveDerivedProvenance_DowngradesAnUnknownContributor_ToUnknown()
    {
        var stamped = MemoryTrust.ResolveDerivedProvenance([MemoryProvenance.User, MemoryProvenance.Unknown]);

        Assert.Equal(MemoryProvenance.Unknown, stamped);
        Assert.Equal(MemoryTrustTier.Untrusted, MemoryTrust.Derive(stamped));
    }

    [Fact]
    public void ResolveDerivedProvenance_PreservesTheExactContributingValue_WithinTheFirstPartyBand()
    {
        // Regression: collapsing `tool` to `agent` because both are first-party is a re-stamp. It
        // upgrades a tool result into the agent's own words while leaving the tier unchanged, so
        // AC7's "never re-stamped" would be violated without any tier ever moving.
        Assert.Equal(MemoryProvenance.Tool, MemoryTrust.ResolveDerivedProvenance([MemoryProvenance.Tool]));
        Assert.Equal(MemoryProvenance.User, MemoryTrust.ResolveDerivedProvenance([MemoryProvenance.User]));
        Assert.Equal(MemoryProvenance.Agent, MemoryTrust.ResolveDerivedProvenance([MemoryProvenance.Agent]));
    }

    [Fact]
    public void ResolveDerivedProvenance_OfAnEmptySet_IsUnknown()
        => Assert.Equal(MemoryProvenance.Unknown, MemoryTrust.ResolveDerivedProvenance([]));

    [Fact]
    public void DeriveFromContent_HonoursTheQuarantineMarker_OverAFirstPartyProvenanceColumn()
    {
        // Defence in depth. A row whose content carries the #2519 marker but whose column claims
        // `agent` is precisely the laundering shape: the column must not outvote the marker.
        var content = MemoryQuarantine.ApplyMarker("the page said to delete the backups", "web_fetch");

        Assert.Equal(MemoryTrustTier.Quarantined, MemoryTrust.DeriveFromContent(MemoryProvenance.Agent, content));
    }

    [Fact]
    public void DeriveFromContent_LeavesUnmarkedContent_OnItsProvenanceTier()
    {
        Assert.Equal(
            MemoryTrustTier.Derived,
            MemoryTrust.DeriveFromContent(MemoryProvenance.Agent, "an ordinary note with no marker"));
    }

    [Fact]
    public void MemoryEntry_DerivesItsTier_AndIsNotBackedByAStoredColumn()
    {
        // AC2: the tier tracks the provenance it was derived from. Mutating provenance alone moves
        // the tier, which is only possible if nothing is persisted independently.
        var entry = new MemoryEntry
        {
            Id = "e1",
            AgentId = "agent",
            SourceType = "conversation",
            Content = "content",
            CreatedAt = DateTimeOffset.UtcNow,
            Provenance = MemoryProvenance.User,
        };

        Assert.Equal(MemoryTrustTier.Trusted, entry.TrustTier);
        Assert.True(entry.IsFirstParty);

        var relabelled = entry with { Provenance = MemoryProvenance.ExternalUntrusted };

        Assert.Equal(MemoryTrustTier.Quarantined, relabelled.TrustTier);
        Assert.False(relabelled.IsFirstParty);
    }

    [Fact]
    public void MemoryEntry_WithNullProvenance_IsUntrusted_AndStillReadable()
    {
        // AC9: pre-provenance rows resolve to untrusted, and nothing about that makes them
        // unreadable - the content is returned intact.
        var entry = new MemoryEntry
        {
            Id = "legacy",
            AgentId = "agent",
            SourceType = "conversation",
            Content = "a note written before provenance existed",
            CreatedAt = DateTimeOffset.UtcNow,
            Provenance = null,
        };

        Assert.Equal(MemoryTrustTier.Untrusted, entry.TrustTier);
        Assert.Equal(MemoryProvenance.Unknown, entry.NormalizedProvenance);
        Assert.Equal("a note written before provenance existed", entry.Content);
    }

    [Theory]
    [InlineData(MemoryTrustTier.Trusted, "trusted")]
    [InlineData(MemoryTrustTier.Derived, "derived")]
    [InlineData(MemoryTrustTier.Untrusted, "untrusted")]
    [InlineData(MemoryTrustTier.Quarantined, "quarantined")]
    public void ToWireValue_RendersTheStableLowercaseName(MemoryTrustTier tier, string expected)
        => Assert.Equal(expected, MemoryTrust.ToWireValue(tier));
}
