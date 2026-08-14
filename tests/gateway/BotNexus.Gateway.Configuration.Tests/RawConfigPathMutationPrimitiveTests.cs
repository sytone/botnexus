using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// The raw dotted-path traversal primitives, tested where they now live.
/// </summary>
/// <remarks>
/// <para>
/// These assertions came verbatim from <c>BotNexus.Cli.Tests.RawConfigPathMutationTests</c>. #2887
/// made <see cref="RawConfigPath"/> <c>internal</c> to this project, so the CLI test assembly can
/// no longer reference it - and that is the point: the primitive is not a consumer-facing surface.
/// The tests move rather than disappear, because the raw semantics (intermediate creation,
/// case-preserving key matching, patch-preserves-unsupplied, malformed-path rejection, entry keys
/// as literals) are exactly what the canonical <see cref="ConfigDocument"/> surface is built on.
/// </para>
/// </remarks>
public sealed class RawConfigPathMutationPrimitiveTests
{
    [Fact]
    public void RawConfigPath_set_creates_missing_intermediate_objects()
    {
        var root = new JsonObject();
        RawConfigPath.TrySet(root, "a.b.c", JsonValue.Create("v"), out var error).ShouldBeTrue();
        error.ShouldBeEmpty();
        root["a"]!["b"]!["c"]!.GetValue<string>().ShouldBe("v");
    }

    [Fact]
    public void RawConfigPath_set_matches_existing_key_casing_instead_of_creating_a_sibling()
    {
        var root = JsonNode.Parse("""{ "Gateway": { "ListenUrl": "old" } }""")!.AsObject();
        RawConfigPath.TrySet(root, "gateway.listenUrl", JsonValue.Create("new"), out _).ShouldBeTrue();
        root.Count.ShouldBe(1);
        root["Gateway"]!.AsObject().Count.ShouldBe(1);
        root["Gateway"]!["ListenUrl"]!.GetValue<string>().ShouldBe("new");
    }

    [Fact]
    public void RawConfigPath_patch_entry_leaves_unsupplied_properties_alone()
    {
        var root = JsonNode.Parse("""{ "providers": { "p": { "a": 1, "b": 2 } } }""")!.AsObject();
        RawConfigPath.TryPatchEntry(root, "providers", "p", new JsonObject { ["b"] = 9 }, out _).ShouldBeTrue();
        root["providers"]!["p"]!["a"]!.GetValue<int>().ShouldBe(1);
        root["providers"]!["p"]!["b"]!.GetValue<int>().ShouldBe(9);
    }

    [Fact]
    public void RawConfigPath_rejects_a_malformed_path()
    {
        RawConfigPath.TrySet(new JsonObject(), "a.[x]", JsonValue.Create(1), out var error).ShouldBeFalse();
        error.ShouldNotBeEmpty();
    }

    [Fact]
    public void RawConfigPath_treats_entry_keys_as_literals_not_paths()
    {
        var root = new JsonObject();
        RawConfigPath.TrySetEntry(root, "gateway.locations", "my.location", JsonValue.Create("v"), out _).ShouldBeTrue();
        root["gateway"]!["locations"]!.AsObject().ContainsKey("my.location").ShouldBeTrue();
    }

    /// <summary>
    /// <c>Exists</c> distinguishes an explicit JSON null from an absent key - the distinction
    /// <c>Get</c> cannot make, and the one <see cref="ConfigDocument.Exists"/> is built on.
    /// </summary>
    [Fact]
    public void RawConfigPath_exists_distinguishes_explicit_null_from_absent()
    {
        var root = JsonNode.Parse("""{ "gateway": { "listenUrl": null } }""")!.AsObject();

        RawConfigPath.Exists(root, "gateway.listenUrl").ShouldBeTrue();
        RawConfigPath.Get(root, "gateway.listenUrl").ShouldBeNull();
        RawConfigPath.Exists(root, "gateway.publicBaseUrl").ShouldBeFalse();
    }
}
