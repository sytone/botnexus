using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Pins the bracket-balance rejection added for #2605 on the RAW document write path - the one
/// that actually touches config.json. The dotted-path splitter here carried the same
/// <c>Math.Max(0, depth - 1)</c> clamp as the typed resolver, so a stray ']' was absorbed into the
/// key name and written as a JSON key the operator never named.
/// </summary>
public sealed class RawConfigPathBracketValidationTests
{
    [Theory]
    [InlineData("a]b", "unmatched ']'", 2)]
    [InlineData("agents.my]agent.model", "unmatched ']'", 10)]
    [InlineData("a[0", "unclosed '['", 2)]
    [InlineData("a.b[0.c", "unclosed '['", 4)]
    public void TrySet_MalformedPath_IsRejectedAndDocumentUnchanged(string path, string expected, int position)
    {
        var root = JsonNode.Parse("""{"gateway":{"listenUrl":"http://localhost:5005"}}""")!.AsObject();
        var before = root.ToJsonString();

        var ok = RawConfigPath.TrySet(root, path, JsonValue.Create("x"), out var error);

        ok.ShouldBeFalse();
        error.ShouldContain(expected);
        error.ShouldContain($"position {position}");
        error.ShouldContain(path);
        root.ToJsonString().ShouldBe(before);
    }

    [Fact]
    public void TrySet_UnmatchedCloser_DoesNotCreateMisnamedKey()
    {
        var root = JsonNode.Parse("""{"agents":{}}""")!.AsObject();

        var ok = RawConfigPath.TrySet(root, "agents.my]agent.model", JsonValue.Create("gpt-4"), out _);

        ok.ShouldBeFalse();
        root["agents"]!.AsObject().ContainsKey("my]agent").ShouldBeFalse();
        root["agents"]!.AsObject().Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("gateway.listenUrl")]
    [InlineData("agents.assistant.model")]
    [InlineData("gateway.defaultAgentId")]
    [InlineData("gateway.cors.allowedOrigins[0]")]
    [InlineData("world.id")]
    public void TrySet_ValidPaths_StillSucceed(string path)
    {
        var root = JsonNode.Parse("{}")!.AsObject();

        var ok = RawConfigPath.TrySet(root, path, JsonValue.Create("v"), out var error);

        ok.ShouldBeTrue(error);
        error.ShouldBeEmpty();
    }
}
