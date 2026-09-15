using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Pins the tool-catalogue fingerprint.
///
/// Its whole job is to make an invisible failure visible: tool definitions and their ordering are
/// documented cache invalidators on Anthropic and OpenAI, so a catalogue that reshuffles between
/// runs re-bills every cached prefix behind it and reports nothing. A fingerprint that failed to
/// move on a reorder would hide exactly the fault it exists to expose.
/// </summary>
public sealed class ToolCatalogFingerprintTests
{
    [Fact]
    public void SameCatalogue_SameFingerprint()
    {
        ToolCatalogFingerprint.Compute([Tool("alpha"), Tool("beta")])
            .ShouldBe(ToolCatalogFingerprint.Compute([Tool("alpha"), Tool("beta")]));
    }

    [Fact]
    public void ReorderedCatalogue_DifferentFingerprint()
    {
        // The same tools in a different order are a different cache prefix. This is the case the
        // fingerprint exists for.
        ToolCatalogFingerprint.Compute([Tool("alpha"), Tool("beta")])
            .ShouldNotBe(ToolCatalogFingerprint.Compute([Tool("beta"), Tool("alpha")]));
    }

    [Fact]
    public void ChangedDescription_DifferentFingerprint()
    {
        ToolCatalogFingerprint.Compute([Tool("alpha", description: "Does a thing.")])
            .ShouldNotBe(ToolCatalogFingerprint.Compute([Tool("alpha", description: "Does a thing, now.")]));
    }

    [Fact]
    public void ChangedSchema_DifferentFingerprint()
    {
        ToolCatalogFingerprint.Compute([Tool("alpha", schema: """{"type":"object","properties":{}}""")])
            .ShouldNotBe(ToolCatalogFingerprint.Compute([Tool("alpha", schema: """{"type":"object","properties":{"q":{"type":"string"}}}""")]));
    }

    [Fact]
    public void EmptyCatalogue_IsStableRatherThanThrowing()
    {
        ToolCatalogFingerprint.Compute([]).ShouldBe(ToolCatalogFingerprint.Compute([]));
    }

    [Fact]
    public void ToolWithNoSchema_DoesNotTakeDownTheRequest()
    {
        // A default JsonElement throws on GetRawText. The fingerprint only observes the request;
        // it must never be the reason one fails.
        Should.NotThrow(() => ToolCatalogFingerprint.Compute([new FakeTool("alpha", "Does a thing.", default)]));
    }

    private static IAgentTool Tool(
        string name,
        string description = "Does a thing.",
        string schema = """{"type":"object","properties":{}}""")
        => new FakeTool(name, description, JsonDocument.Parse(schema).RootElement);

    private sealed class FakeTool(string name, string description, JsonElement parameters) : IAgentTool
    {
        public string Name => name;

        public string Label => name;

        public Tool Definition { get; } = new(name, description, parameters);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => throw new NotSupportedException("The fingerprint never executes a tool.");
    }
}
