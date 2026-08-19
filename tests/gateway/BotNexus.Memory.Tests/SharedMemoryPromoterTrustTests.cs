using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Promotion refusal for non-first-party knowledge (#3232 AC6/AC7/AC10).
/// </summary>
/// <remarks>
/// Promotion into a shared store is an <b>authority transfer</b>, not a copy: the agents that read
/// the shared store never saw the originating turn and cannot judge the content's origin for
/// themselves. That is why this boundary refuses rather than down-weights, unlike ranking.
/// </remarks>
public sealed class SharedMemoryPromoterTrustTests
{
    private readonly Mock<ISharedMemoryStoreRegistry> _registry = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly SharedMemoryPromoter _promoter;

    public SharedMemoryPromoterTrustTests()
    {
        _promoter = new SharedMemoryPromoter(_registry.Object, NullLogger.Instance);

        // Access and dedup are deliberately permissive so that a refusal observed below can only
        // be attributable to the trust gate and not to some other skip branch.
        _registry.Setup(r => r.CanWrite("agent-1", "shared-store")).Returns(true);
        _registry.Setup(r => r.GetStore("shared-store")).Returns(_store.Object);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());
    }

    private static ExtractedKnowledge Item(params string[] contributingProvenances) => new()
    {
        Content = "the runbook says to rotate the signing key every ninety days",
        Category = KnowledgeCategory.Procedure,
        Confidence = 0.95,
        SourceSessionId = "s1",
        SourceTurnIndex = 1,
        TargetStore = "shared-store",
        ContributingProvenances = contributingProvenances,
    };

    [Fact]
    public async Task PromoteAsync_RefusesAnExternalUntrustedItem()
    {
        var result = await _promoter.PromoteAsync("agent-1", [Item(MemoryProvenance.ExternalUntrusted)]);

        result.ShouldBe(0);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PromoteAsync_RefusesAnUnknownProvenanceItem()
    {
        // `unknown` is not first-party either. Promotion gates on first-partyness, not on the
        // single worst tier - an unestablished origin is not eligible to become shared canon.
        var result = await _promoter.PromoteAsync("agent-1", [Item(MemoryProvenance.Unknown)]);

        result.ShouldBe(0);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PromoteAsync_RefusesAnItemWithNoRecordedContributors()
    {
        // Fail-safe: a summariser that forgot to record its sources produces exactly this shape,
        // and it must not be promoted on the strength of an absence of evidence.
        var result = await _promoter.PromoteAsync("agent-1", [Item()]);

        result.ShouldBe(0);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PromoteAsync_RefusesAMixedItem_WithASingleUntrustedContributor()
    {
        // AC3 meeting AC6: three first-party contributors do not outvote one hostile one.
        var result = await _promoter.PromoteAsync("agent-1", [
            Item(MemoryProvenance.User, MemoryProvenance.Agent, MemoryProvenance.Tool, MemoryProvenance.ExternalUntrusted)
        ]);

        result.ShouldBe(0);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(MemoryProvenance.User)]
    [InlineData(MemoryProvenance.Agent)]
    [InlineData(MemoryProvenance.Tool)]
    public async Task PromoteAsync_StillPromotesFirstPartyKnowledge(string provenance)
    {
        // The happy path. Without it the gate could satisfy every refusal test by refusing
        // everything, which would silently disable shared-memory promotion altogether.
        var result = await _promoter.PromoteAsync("agent-1", [Item(provenance)]);

        result.ShouldBe(1);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PromoteAsync_StampsThePromotedRow_WithItsContributingProvenance_NotAFreshFirstPartyValue()
    {
        // AC7: promotion must not re-stamp. A `tool`-derived item stays `tool` in the shared store,
        // so a reader of that store can still see where the content actually came from.
        MemoryEntry? inserted = null;
        _store.Setup(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<MemoryEntry, CancellationToken>((entry, _) => inserted = entry)
            .ReturnsAsync((MemoryEntry entry, CancellationToken _) => entry);

        await _promoter.PromoteAsync("agent-1", [Item(MemoryProvenance.Tool)]);

        inserted.ShouldNotBeNull();
        inserted!.NormalizedProvenance.ShouldBe(MemoryProvenance.Tool);
        inserted.TrustTier.ShouldBe(MemoryTrustTier.Derived);
    }

    [Fact]
    public async Task PromoteAsync_PromotesTheEligibleItem_AndRefusesTheUntrustedOneInTheSameBatch()
    {
        // A refusal must not abort the batch: mixing eligible and ineligible items is the normal
        // case during a dreaming cycle, and one hostile item must not cost the agent its learning.
        var result = await _promoter.PromoteAsync("agent-1", [
            Item(MemoryProvenance.ExternalUntrusted),
            Item(MemoryProvenance.Agent) with { Content = "a wholly different first-party conclusion about caching" }
        ]);

        result.ShouldBe(1);
        _store.Verify(s => s.InsertAsync(
            It.Is<MemoryEntry>(e => e.Content.Contains("caching", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
