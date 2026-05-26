using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Pins the contract for <see cref="SubAgentSpawnMode"/> and its two cases
/// (<see cref="Embody"/>, <see cref="Mirror"/>) introduced by Phase 5 / F-6
/// step 3 (#562). These tests target the new types in isolation; the broader
/// <see cref="SubAgentSpawnRequest"/> migration is exercised separately.
/// </summary>
public sealed class SubAgentSpawnModeTests
{
    // ---- EmbodyCustomizations.Default --------------------------------------

    [Fact]
    public void EmbodyCustomizations_Default_HasAllFieldsNull()
    {
        var d = EmbodyCustomizations.Default;

        d.Name.ShouldBeNull();
        d.SystemPromptOverride.ShouldBeNull();
        d.ModelOverride.ShouldBeNull();
        d.ApiProviderOverride.ShouldBeNull();
        d.ToolIds.ShouldBeNull();
    }

    [Fact]
    public void EmbodyCustomizations_Default_IsSingleton()
    {
        EmbodyCustomizations.Default.ShouldBeSameAs(EmbodyCustomizations.Default);
    }

    [Fact]
    public void EmbodyCustomizations_DefaultEqualsExplicitlyEmpty_ByRecordEquality()
    {
        // Pin record equality so callers can compare without juggling reference
        // identity. If someone changes Default to non-empty, this breaks.
        EmbodyCustomizations.Default.ShouldBe(new EmbodyCustomizations());
    }

    // ---- Embody construction & validation ----------------------------------

    [Theory]
    [InlineData("researcher")]
    [InlineData("coder")]
    [InlineData("planner")]
    [InlineData("reviewer")]
    [InlineData("writer")]
    [InlineData("general")]
    public void Embody_WithKnownRole_ConstructsCleanly(string role)
    {
        var archetype = SubAgentArchetype.FromString(role);

        var embody = new Embody(archetype);

        embody.Role.ShouldBe(archetype);
        embody.Customizations.ShouldBe(EmbodyCustomizations.Default);
    }

    [Fact]
    public void Embody_WithCustomizations_PreservesThem()
    {
        var customizations = new EmbodyCustomizations
        {
            Name = "my-researcher",
            ModelOverride = "gpt-5-mini",
            ToolIds = new[] { "web_search", "memory_search" }
        };

        var embody = new Embody(SubAgentArchetype.Researcher, customizations);

        embody.Customizations.ShouldBeSameAs(customizations);
        embody.Customizations.Name.ShouldBe("my-researcher");
    }

    [Fact]
    public void Embody_WithUnknownRole_ThrowsArgumentException()
    {
        // SubAgentArchetype is an open smart-enum (FromString accepts anything);
        // this guard pins that Embody.Role is constrained to the 6 known statics
        // so the spawn contract cannot smuggle arbitrary role names.
        var unknownRole = SubAgentArchetype.FromString("admin");

        Should.Throw<ArgumentException>(() => new Embody(unknownRole))
            .ParamName.ShouldBe("Role");
    }

    [Fact]
    public void Embody_WithNullRole_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new Embody(null!));
    }

    [Fact]
    public void Embody_WithNullCustomizations_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(
            () => new Embody(SubAgentArchetype.Coder, null!));
    }

    // ---- Mirror construction -----------------------------------------------

    [Fact]
    public void Mirror_WithTargetAgentId_ConstructsCleanly()
    {
        var targetId = AgentId.From("named-agent-a");

        var mirror = new Mirror(targetId);

        mirror.TargetAgentId.ShouldBe(targetId);
    }

    // ---- Polymorphic JSON round-trip ---------------------------------------

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false
    };

    [Fact]
    public void Mode_JsonRoundTrip_Embody_PreservesShape()
    {
        SubAgentSpawnMode original = new Embody(SubAgentArchetype.Researcher);

        var json = JsonSerializer.Serialize(original, s_jsonOptions);
        var roundTripped = JsonSerializer.Deserialize<SubAgentSpawnMode>(json, s_jsonOptions);

        roundTripped.ShouldBeOfType<Embody>()
            .Role.Value.ShouldBe("researcher");
        json.ShouldContain("\"mode\":\"embody\"");
    }

    [Fact]
    public void Mode_JsonRoundTrip_Mirror_PreservesShape()
    {
        SubAgentSpawnMode original = new Mirror(AgentId.From("target-x"));

        var json = JsonSerializer.Serialize(original, s_jsonOptions);
        var roundTripped = JsonSerializer.Deserialize<SubAgentSpawnMode>(json, s_jsonOptions);

        roundTripped.ShouldBeOfType<Mirror>()
            .TargetAgentId.ShouldBe(AgentId.From("target-x"));
        json.ShouldContain("\"mode\":\"mirror\"");
    }

    [Fact]
    public void Mode_JsonDeserialize_UnknownDiscriminator_Throws()
    {
        var bogus = "{\"mode\":\"telekinesis\",\"TargetAgentId\":\"x\"}";

        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<SubAgentSpawnMode>(bogus, s_jsonOptions));
    }

    [Fact]
    public void Mode_JsonDeserialize_MissingDiscriminator_Throws()
    {
        var noDiscriminator = "{\"TargetAgentId\":\"x\"}";

        // STJ raises NotSupportedException (wrapping the underlying invariant) when a payload
        // for a polymorphic abstract base type omits the discriminator property.
        Should.Throw<NotSupportedException>(() =>
            JsonSerializer.Deserialize<SubAgentSpawnMode>(noDiscriminator, s_jsonOptions));
    }

    [Fact]
    public void Mode_JsonRoundTrip_EmbodyWithCustomizations_PreservesAllFields()
    {
        SubAgentSpawnMode original = new Embody(
            SubAgentArchetype.Coder,
            new EmbodyCustomizations
            {
                Name = "my-coder",
                ModelOverride = "claude-opus-4.7",
                ApiProviderOverride = "anthropic",
                ToolIds = new[] { "read", "write", "shell" }
            });

        var json = JsonSerializer.Serialize(original, s_jsonOptions);
        var roundTripped = JsonSerializer.Deserialize<SubAgentSpawnMode>(json, s_jsonOptions);

        var embody = roundTripped.ShouldBeOfType<Embody>();
        embody.Role.Value.ShouldBe("coder");
        embody.Customizations.Name.ShouldBe("my-coder");
        embody.Customizations.ModelOverride.ShouldBe("claude-opus-4.7");
        embody.Customizations.ApiProviderOverride.ShouldBe("anthropic");
        embody.Customizations.ToolIds.ShouldBe(new[] { "read", "write", "shell" });
    }
}
