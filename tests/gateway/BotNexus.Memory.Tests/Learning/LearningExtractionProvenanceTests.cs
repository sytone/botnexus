using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Tests.Learning;

/// <summary>
/// Provenance inheritance through knowledge extraction (#3232 AC3).
/// </summary>
/// <remarks>
/// Summarisation is where provenance is most easily lost: a session distilled into one row mixes
/// several origins into a single value, and picking that value naively erases the untrusted
/// contribution. These tests assert the distilled item carries its source's origin forward.
/// </remarks>
public sealed class LearningExtractionProvenanceTests
{
    private static readonly LearningExtractionPipeline Pipeline = new(
        [new KnowledgeRoutingRule { TargetStore = "shared", MinConfidence = 0d }],
        NullLogger.Instance);

    private static MemoryEntry Turn(string provenance, string? contentOverride = null) => new()
    {
        Id = "e1",
        AgentId = "agent",
        SessionId = "s1",
        TurnIndex = 1,
        SourceType = "conversation",
        Content = contentOverride ?? TranscriptTurnFormat.Encode(
            "how do we deploy the gateway?",
            "We always deploy the gateway by running the release pipeline against main."),
        CreatedAt = DateTimeOffset.UtcNow,
        Provenance = provenance,
    };

    [Fact]
    public async Task ExtractAsync_RecordsTheSourceProvenance_OnTheExtractedItem()
    {
        var extracted = await Pipeline.ExtractAsync([Turn(MemoryProvenance.Agent)]);

        var item = Assert.Single(extracted);
        Assert.Equal([MemoryProvenance.Agent], item.ContributingProvenances);
        Assert.Equal(MemoryProvenance.Agent, item.Provenance);
        Assert.True(item.IsPromotable);
    }

    [Fact]
    public async Task ExtractAsync_CarriesAnUntrustedSourceProvenance_ForwardIntoTheExtractedItem()
    {
        // The laundering step this closes: distilling an external-untrusted row must not produce a
        // promotable item. Without inheritance the extracted item would default to promotable.
        var extracted = await Pipeline.ExtractAsync([Turn(MemoryProvenance.ExternalUntrusted)]);

        var item = Assert.Single(extracted);
        Assert.Equal(MemoryProvenance.ExternalUntrusted, item.Provenance);
        Assert.False(item.IsPromotable);
    }

    [Fact]
    public async Task ExtractAsync_CarriesAPreProvenanceSourceForward_AsUnknown()
    {
        // AC9 through the extraction path: a NULL-provenance legacy row is still extractable, but
        // what comes out of it is unestablished rather than first-party.
        var extracted = await Pipeline.ExtractAsync([Turn(provenance: null!)]);

        var item = Assert.Single(extracted);
        Assert.Equal(MemoryProvenance.Unknown, item.Provenance);
        Assert.False(item.IsPromotable);
    }

    [Fact]
    public async Task ExtractAsync_HonoursTheQuarantineMarker_OverAFirstPartyProvenanceColumn()
    {
        // Content and column disagree; the marker must win, or a row could shed its quarantine by
        // being re-inserted with a fresh provenance value and then distilled.
        var quarantined = MemoryQuarantine.ApplyMarker(
            TranscriptTurnFormat.Encode(
                "how do we deploy the gateway?",
                "We always deploy the gateway by running the release pipeline against main."),
            "web_fetch");

        var extracted = await Pipeline.ExtractAsync([Turn(MemoryProvenance.Agent, quarantined)]);

        // The marker may or may not leave the row parseable as a turn pair; what must never happen
        // is a promotable item emerging from quarantined content.
        Assert.All(extracted, item => Assert.False(item.IsPromotable));
    }
}
