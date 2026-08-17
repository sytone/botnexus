using BotNexus.Memory.Models;
using BotNexus.Memory.Tools;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Covers the recall half of #2519: a note quarantined at write time must not be handed back as
/// first-party content on a later session. Markdown notes carry no provenance column, so the
/// embedded marker IS the provenance record and must be honoured wherever a note is projected.
/// </summary>
public sealed class QuarantinedNoteRecallTests
{
    [Fact]
    public void QuarantinedNoteBody_DerivesNonFirstPartyProvenance()
    {
        var body = MemoryQuarantine.ApplyMarker("the page claimed the limit is 500", "web_fetch (network)");

        var provenance = MemoryQuarantine.IsQuarantined(body)
            ? MemoryProvenance.ExternalUntrusted
            : MemoryProvenance.Agent;

        provenance.ShouldBe(MemoryProvenance.ExternalUntrusted);
        MemoryProvenance.IsFirstParty(provenance).ShouldBeFalse();
    }

    [Fact]
    public void CleanNoteBody_RemainsFirstParty()
    {
        const string body = "our own conclusion from reading the source";

        var provenance = MemoryQuarantine.IsQuarantined(body)
            ? MemoryProvenance.ExternalUntrusted
            : MemoryProvenance.Agent;

        provenance.ShouldBe(MemoryProvenance.Agent);
        MemoryProvenance.IsFirstParty(provenance).ShouldBeTrue();
    }

    /// <summary>
    /// The marker must survive a round trip through storage as literal text, because recall
    /// re-reads it to reconstruct the trust level.
    /// </summary>
    [Fact]
    public void Marker_SurvivesRoundTripAsLiteralText()
    {
        var written = MemoryQuarantine.ApplyMarker("claim", "web_fetch (network)");

        var readBack = string.Join('\n', written.Split('\n'));

        MemoryQuarantine.IsQuarantined(readBack).ShouldBeTrue();
        readBack.ShouldContain("claim");
    }

    /// <summary>
    /// A quarantined entry retrieved as a <see cref="MemoryEntry"/> must normalise to a
    /// non-first-party provenance even if the stored column were absent or malformed - the #2480
    /// fail-safe and the #2519 marker must agree rather than contradict each other.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense-value")]
    public void QuarantinedEntry_WithMissingOrMalformedProvenanceColumn_StillNotFirstParty(string? stored)
    {
        var entry = new MemoryEntry
        {
            Id = "e1",
            AgentId = "a1",
            SourceType = "tool",
            CreatedAt = DateTimeOffset.UtcNow,
            Provenance = stored,
            Content = MemoryQuarantine.ApplyMarker("claim", "web_fetch (network)")
        };

        MemoryProvenance.IsFirstParty(entry.NormalizedProvenance).ShouldBeFalse();
        MemoryQuarantine.IsQuarantined(entry.Content).ShouldBeTrue();
    }
}
