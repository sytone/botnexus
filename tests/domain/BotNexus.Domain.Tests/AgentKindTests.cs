using System.Text.Json;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Round-trip and contract tests for <see cref="AgentKind"/>. The enum carries
/// <c>[JsonConverter(typeof(JsonStringEnumConverter&lt;AgentKind&gt;))]</c> so it
/// serializes as a string (e.g. <c>"Named"</c> / <c>"SubAgent"</c>) — never as a number.
/// String form is mandatory because numeric values can be silently shifted by a future
/// enum reorder (e.g. adding a value between Named and SubAgent), which would break
/// persisted JSON and stored config. String form is also more readable in REST traffic
/// and config files.
/// </summary>
public sealed class AgentKindTests
{
    [Fact]
    public void AgentDescriptor_KindDefault_IsNamed()
    {
        // Back-compat: any descriptor created without setting Kind must default to Named.
        // Pre-existing JSON payloads and config files omit "kind"; they must continue to
        // round-trip as Named so we do not silently regress the spawn-tool gate for
        // legitimate named agents.
        var descriptor = new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("alpha"),
            DisplayName = "Alpha",
            ModelId = "m",
            ApiProvider = "p"
        };

        descriptor.Kind.ShouldBe(AgentKind.Named,
            "AgentDescriptor.Kind must default to AgentKind.Named so that descriptors " +
            "constructed without an explicit Kind are treated as first-class named agents.");
    }

    [Fact]
    public void AgentDescriptor_SerializesKind_AsStringForm()
    {
        // Wire-contract pin: Kind serializes as a STRING, not a number. If a future change
        // strips the [JsonConverter(JsonStringEnumConverter<AgentKind>)] attribute or
        // changes the global JsonSerializerOptions to drop string-enum conversion, this
        // assertion catches it before persisted documents break.
        var descriptor = new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("alpha"),
            DisplayName = "Alpha",
            ModelId = "m",
            ApiProvider = "p",
            Kind = AgentKind.SubAgent
        };

        var json = JsonSerializer.Serialize(descriptor);

        json.ShouldContain("\"SubAgent\"");
        json.ShouldNotContain("\"kind\":1");
        json.ShouldNotContain("\"Kind\":1");
    }

    [Fact]
    public void AgentDescriptor_DeserializesMissingKind_AsNamed()
    {
        // Critical back-compat pin: a JSON payload that does NOT include a "kind" field
        // (every pre-Phase-5 payload, every legacy config file) must deserialize with
        // Kind = Named. Any regression here would silently down-grade every existing named
        // agent to lose spawn_subagent capability on next load.
        const string json = """
            {
              "agentId": "alpha",
              "displayName": "Alpha",
              "modelId": "m",
              "apiProvider": "p"
            }
            """;

        var descriptor = JsonSerializer.Deserialize<AgentDescriptor>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        descriptor.ShouldNotBeNull();
        descriptor!.Kind.ShouldBe(AgentKind.Named,
            "Descriptors deserialized from JSON without an explicit 'kind' field must default " +
            "to AgentKind.Named. Any regression silently breaks back-compat for every legacy " +
            "config file and persisted REST payload.");
    }

    [Fact]
    public void AgentDescriptor_DeserializesKindSubAgent_FromStringForm()
    {
        // Round-trip pin: the symmetric read of the SerializesKind_AsStringForm test.
        // String-form deserialization must succeed (case-insensitive — STJ enum converters
        // accept the camelCase / PascalCase variant by default).
        const string json = """
            {
              "agentId": "beta",
              "displayName": "Beta",
              "modelId": "m",
              "apiProvider": "p",
              "kind": "SubAgent"
            }
            """;

        var descriptor = JsonSerializer.Deserialize<AgentDescriptor>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        descriptor.ShouldNotBeNull();
        descriptor!.Kind.ShouldBe(AgentKind.SubAgent);
    }

    [Theory]
    [InlineData(AgentKind.Named)]
    [InlineData(AgentKind.SubAgent)]
    public void AgentDescriptor_KindRoundTrips_ThroughJson(AgentKind kind)
    {
        var original = new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("gamma"),
            DisplayName = "Gamma",
            ModelId = "m",
            ApiProvider = "p",
            Kind = kind
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AgentDescriptor>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        restored.ShouldNotBeNull();
        restored!.Kind.ShouldBe(kind);
    }

    [Fact]
    public void AgentDescriptor_WithExpression_PreservesKind()
    {
        // AgentDescriptor is a sealed record with init-only properties. The DefaultSubAgentManager
        // spawn path uses `baseDescriptor with { ..., Kind = AgentKind.SubAgent }` to derive the
        // child registration. If `Kind` ever drops the init setter or is removed from the record
        // copy-constructor, the `with` clone would not preserve Kind correctly. Pin the semantic
        // explicitly so a future refactor cannot silently regress it.
        var baseDescriptor = new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("delta"),
            DisplayName = "Delta",
            ModelId = "m",
            ApiProvider = "p"
        };

        var spawned = baseDescriptor with { Kind = AgentKind.SubAgent };

        spawned.Kind.ShouldBe(AgentKind.SubAgent,
            "`with` clone must propagate Kind = SubAgent to the new instance — this is the " +
            "exact shape DefaultSubAgentManager.SpawnAsync uses to register child sub-agents.");
        baseDescriptor.Kind.ShouldBe(AgentKind.Named,
            "`with` clone must NOT mutate the source descriptor. Records are immutable; the " +
            "source must remain Named.");
    }
}
