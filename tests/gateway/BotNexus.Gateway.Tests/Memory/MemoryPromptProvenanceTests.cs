using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Contracts.Memory;
using Shouldly;

namespace BotNexus.Gateway.Tests.Memory;

/// <summary>
/// Provenance rendering in the memory prompt context (#2480). The store recording origin is only
/// half the fix; if the assembled prompt drops it, the model still weighs laundered third-party
/// text as first-party knowledge.
/// </summary>
public sealed class MemoryPromptProvenanceTests
{
    [Fact]
    public void RenderNoteWithProvenance_IncludesTheProvenanceBanner()
    {
        var note = new AgentMemoryDailyNote(new DateOnly(2026, 8, 14), "the note body", "external-untrusted");

        var rendered = WorkspaceContextBuilder.RenderNoteWithProvenance(note);

        rendered.ShouldStartWith("> [memory provenance: external-untrusted]");
        rendered.ShouldContain("the note body");
    }

    [Fact]
    public void RenderNoteWithProvenance_WithNoProvenance_RendersUnknownRatherThanNothing()
    {
        // The banner is unconditional: its absence must not be readable as "verified first-party".
        var note = new AgentMemoryDailyNote(new DateOnly(2026, 8, 14), "the note body");

        WorkspaceContextBuilder.RenderNoteWithProvenance(note)
            .ShouldStartWith("> [memory provenance: unknown]");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RenderNoteWithProvenance_WithBlankProvenance_FailsSafeToUnknown(string provenance)
    {
        var note = new AgentMemoryDailyNote(new DateOnly(2026, 8, 14), "body", provenance);

        WorkspaceContextBuilder.RenderNoteWithProvenance(note)
            .ShouldStartWith("> [memory provenance: unknown]");
    }

    [Fact]
    public void AgentMemorySearchResult_DefaultsProvenanceToUnknown()
    {
        var result = new AgentMemorySearchResult(
            Id: "id",
            Content: "content",
            SourceType: "conversation",
            SessionId: null,
            CreatedAt: DateTimeOffset.UtcNow);

        result.Provenance.ShouldBe("unknown");
        result.OriginConversationId.ShouldBeNull();
        result.OriginSessionId.ShouldBeNull();
    }
}
