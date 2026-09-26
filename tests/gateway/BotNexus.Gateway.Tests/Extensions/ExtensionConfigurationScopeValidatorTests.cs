using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;

namespace BotNexus.Gateway.Tests.Extensions;

public sealed class ExtensionConfigurationScopeValidatorTests
{
    [Fact]
    public void FindViolations_EnumeratesAllConcretePaths()
    {
        var document = JsonNode.Parse("""
        {
          "world": { "extensions": { "agent-only": {} } },
          "gateway": { "extensions": { "unknown-gateway": {} } },
          "agents": {
            "defaults": { "extensions": { "gateway-only": {} } },
            "alpha": { "extensions": { "unknown-agent": {} } }
          }
        }
        """)!.AsObject();

        var violations = ExtensionConfigurationScopeValidator.FindViolations(document,
        [
            Extension("agent-only", ExtensionConfigurationScope.Agent),
            Extension("gateway-only", ExtensionConfigurationScope.Gateway)
        ]);

        violations.Select(item => item.Path).ShouldBe(
        [
            "world.extensions.agent-only",
            "gateway.extensions.unknown-gateway",
            "agents.defaults.extensions.gateway-only",
            "agents.alpha.extensions.unknown-agent"
        ]);
    }

    [Fact]
    public void ValidateChanges_AllowsUnchangedLegacyAndDeletion_ButRejectsChangedOrNewViolations()
    {
        var before = JsonNode.Parse("""
        {
          "world": { "extensions": { "agent-only": { "value": 1 } } },
          "agents": { "alpha": { "extensions": { "unknown": { "value": 1 } } } }
        }
        """)!.AsObject();
        var candidate = before.DeepClone().AsObject();
        candidate["agents"]!["alpha"]!["extensions"]!.AsObject().Remove("unknown");
        candidate["world"]!["extensions"]!["agent-only"]!["value"] = 2;
        candidate["gateway"] = JsonNode.Parse("""{ "extensions": { "missing": {} } }""");

        var errors = ExtensionConfigurationScopeValidator.ValidateChanges(
            before,
            candidate,
            [Extension("agent-only", ExtensionConfigurationScope.Agent)]);

        errors.Count.ShouldBe(2);
        errors.ShouldContain(error => error.Contains("world.extensions.agent-only", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("gateway.extensions.missing", StringComparison.Ordinal));
        errors.ShouldNotContain(error => error.Contains("agents.alpha.extensions.unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateChanges_ExtensionIdContainingDot_UsesExactBagKey()
    {
        var before = JsonNode.Parse("""{ "gateway": { "extensions": { "example.ext": { "value": 1 } } } }""")!.AsObject();
        var unchanged = before.DeepClone().AsObject();

        ExtensionConfigurationScopeValidator.ValidateChanges(before, unchanged, []).ShouldBeEmpty();
    }

    private static LoadedExtension Extension(string id, params ExtensionConfigurationScope[] scopes) => new()
    {
        ExtensionId = id,
        Name = id,
        Version = "1.0.0",
        DirectoryPath = id,
        EntryAssemblyPath = id,
        LoadedAtUtc = DateTimeOffset.UnixEpoch,
        ConfigurationScopes = scopes
    };
}
