using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Issue #3596 AC3: the agent-maintained <c>summary</c> is an <b>additive optional</b> field on both
/// <c>AgentSummary</c> wire records, so a client built against the previous shape keeps binding.
/// </summary>
/// <remarks>
/// Compatibility here is bidirectional and both directions matter in a live upgrade, where server
/// and client versions are never in lockstep: an old client must tolerate a payload that carries
/// <c>summary</c>, and a new client must tolerate a payload from an old server that omits it.
/// </remarks>
public sealed class AgentSummaryWireContractTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The shape a client built before #3596 declares - deliberately without <c>summary</c>.</summary>
    private sealed record LegacyAgentSummary(
        string AgentId,
        string DisplayName,
        string? Emoji,
        string? Description);

    [Fact]
    public void LegacyClientShape_DeserialisesPayloadCarryingSummary_WithoutError()
    {
        var payload = JsonSerializer.Serialize(
            new AgentSummary("farnsworth", "Farnsworth", "🔬", "Platform engineer", "Shipping fixes."),
            Options);

        // Non-vacuity: the payload really does carry the new field.
        payload.ShouldContain("\"summary\"");

        var legacy = JsonSerializer.Deserialize<LegacyAgentSummary>(payload, Options);

        legacy.ShouldNotBeNull();
        legacy.AgentId.ShouldBe("farnsworth");
        legacy.Description.ShouldBe("Platform engineer");
    }

    [Fact]
    public void CurrentShape_DeserialisesLegacyPayloadOmittingSummary_AsNull()
    {
        const string legacyPayload = """
            {"agentId":"farnsworth","displayName":"Farnsworth","emoji":"🔬","description":"Platform engineer"}
            """;

        var current = JsonSerializer.Deserialize<AgentSummary>(legacyPayload, Options);

        current.ShouldNotBeNull();
        current.AgentId.ShouldBe("farnsworth");
        current.Summary.ShouldBeNull(
            "an omitted summary must bind as null, not throw or produce a placeholder (#3596 AC3).");
    }

    [Fact]
    public void SummaryIsTheLastParameter_SoPositionalConstructionIsUnbroken()
    {
        // A field inserted anywhere but last would silently re-bind existing positional call sites
        // to the wrong argument. Pin the order rather than trusting review to catch it.
        //
        // The pin is deliberately EXACT, and updating it is the point rather than a chore. The
        // compiler already rejects the two obvious mistakes - an insertion that shifts a bool into
        // a string slot is CS1503, and appending without a default is CS1737 - so a test that only
        // restated those would add nothing. What the compiler cannot see is an insertion whose
        // types happen to line up: it compiles, and the wire order changes underneath every client
        // that binds positionally. Freezing the whole list means ANY change to this record fails
        // here and a person has to decide whether it is safe, which is the only check that covers
        // that case.
        //
        // So: if this fails, do not simply paste in the new list. Confirm the new field is LAST and
        // OPTIONAL - additive, so an old client still binds and an old payload still deserialises -
        // and only then update the expectation. canDelegate (2026-08-30) was appended that way.
        var parameters = typeof(AgentSummary)
            .GetConstructors()
            .Single(c => c.GetParameters().Length > 1)
            .GetParameters()
            .Select(p => p.Name)
            .ToArray();

        // avatarHue / responsibility / boundaries (2026-09-08) were appended the same way as
        // canDelegate before them: last, optional, null-defaulted. An older client binding this
        // payload is unaffected, and an older payload still deserialises with the persona unset,
        // which is exactly what an agent nobody has personalised looks like.
        parameters.ShouldBe([
            "AgentId", "DisplayName", "Emoji", "Description", "Summary", "CanDelegate",
            "AvatarHue", "Responsibility", "Boundaries",
        ]);
    }
}
