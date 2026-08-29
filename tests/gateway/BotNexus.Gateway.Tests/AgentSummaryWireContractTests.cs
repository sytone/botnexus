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
        var parameters = typeof(AgentSummary)
            .GetConstructors()
            .Single(c => c.GetParameters().Length > 1)
            .GetParameters()
            .Select(p => p.Name)
            .ToArray();

        parameters.ShouldBe(["AgentId", "DisplayName", "Emoji", "Description", "Summary"]);
    }
}
