using System.Text.Json.Nodes;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

public sealed class ConfigChecksTests
{
    // ── ExtensionsBlockCheck ──────────────────────────────────────────────────

    [Fact]
    public void ExtensionsBlockCheck_ApplicableWhenGatewayAbsent()
    {
        var root = new JsonObject();
        new ExtensionsBlockCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void ExtensionsBlockCheck_ApplicableWhenExtensionsAbsent()
    {
        var root = JsonNode.Parse("{\"gateway\":{}}") !.AsObject();
        new ExtensionsBlockCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void ExtensionsBlockCheck_NotApplicableWhenEnabled()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"extensions\":{\"enabled\":true}}}") !.AsObject();
        new ExtensionsBlockCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void ExtensionsBlockCheck_ApplicableWhenExplicitlyDisabled()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"extensions\":{\"enabled\":false}}}") !.AsObject();
        new ExtensionsBlockCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void ExtensionsBlockCheck_Apply_SetsEnabled()
    {
        var root = new JsonObject();
        new ExtensionsBlockCheck().Apply(root);
        var enabled = root["gateway"]!["extensions"]!["enabled"]!.GetValue<bool>();
        enabled.ShouldBeTrue();
    }

    // ── SkillsWorldDefaultCheck ───────────────────────────────────────────────

    [Fact]
    public void SkillsWorldDefaultCheck_ApplicableWhenDefaultsAbsent()
    {
        var root = new JsonObject();
        new SkillsWorldDefaultCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_ApplicableWhenSkillsKeyAbsent()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"extensions\":{\"defaults\":{}}}}") !.AsObject();
        new SkillsWorldDefaultCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_NotApplicableWhenPresent()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"extensions\":{\"defaults\":{\"botnexus-skills\":{\"enabled\":true}}}}}") !.AsObject();
        new SkillsWorldDefaultCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_ApplicableWhenExplicitlyDisabled()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"extensions\":{\"defaults\":{\"botnexus-skills\":{\"enabled\":false}}}}}") !.AsObject();
        new SkillsWorldDefaultCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_Apply_SetsFullPath()
    {
        var root = new JsonObject();
        new SkillsWorldDefaultCheck().Apply(root);
        var enabled = root["gateway"]!["extensions"]!["defaults"]!["botnexus-skills"]!["enabled"]!.GetValue<bool>();
        enabled.ShouldBeTrue();
        // extensions block should also be enabled
        root["gateway"]!["extensions"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_Apply_PreservesExistingDefaults()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"extensions\":{\"enabled\":true,\"defaults\":{\"other-ext\":{\"enabled\":true}}}}}") !.AsObject();
        new SkillsWorldDefaultCheck().Apply(root);
        var defaults = root["gateway"]!["extensions"]!["defaults"]!.AsObject();
        defaults.ContainsKey("other-ext").ShouldBeTrue();
        defaults.ContainsKey("botnexus-skills").ShouldBeTrue();
    }

    // ── CronCheck ─────────────────────────────────────────────────────────────

    [Fact]
    public void CronCheck_ApplicableWhenAbsent()
    {
        var root = new JsonObject();
        new CronCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void CronCheck_NotApplicableWhenPresent()
    {
        var root = JsonNode.Parse("{\"cron\":{\"enabled\":true}}") !.AsObject();
        new CronCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void CronCheck_Apply_AddsCronBlock()
    {
        var root = new JsonObject();
        new CronCheck().Apply(root);
        root["cron"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        root["cron"]!["tickIntervalSeconds"]!.GetValue<int>().ShouldBe(60);
    }

    [Fact]
    public void CronCheck_Apply_DoesNotOverwriteExisting()
    {
        var root = JsonNode.Parse("{\"cron\":{\"enabled\":true,\"tickIntervalSeconds\":30}}") !.AsObject();
        new CronCheck().Apply(root);
        // already present — Apply is a no-op
        root["cron"]!["tickIntervalSeconds"]!.GetValue<int>().ShouldBe(30);
    }

    // ── MemoryAgentDefaultCheck ───────────────────────────────────────────────

    [Fact]
    public void MemoryAgentDefaultCheck_NotApplicableWhenNoAgentsBlock()
    {
        var root = new JsonObject();
        // no agents at all — check should be silent
        new MemoryAgentDefaultCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void MemoryAgentDefaultCheck_ApplicableWhenDefaultsMemoryAbsent()
    {
        var root = JsonNode.Parse("{\"agents\":{\"defaults\":{}}}") !.AsObject();
        new MemoryAgentDefaultCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void MemoryAgentDefaultCheck_NotApplicableWhenPresent()
    {
        var root = JsonNode.Parse("{\"agents\":{\"defaults\":{\"memory\":{\"enabled\":true,\"indexing\":\"auto\"}}}}") !.AsObject();
        new MemoryAgentDefaultCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void MemoryAgentDefaultCheck_Apply_SetsMemoryBlock()
    {
        var root = JsonNode.Parse("{\"agents\":{\"defaults\":{}}}") !.AsObject();
        new MemoryAgentDefaultCheck().Apply(root);
        var memory = root["agents"]!["defaults"]!["memory"]!.AsObject();
        memory["enabled"]!.GetValue<bool>().ShouldBeTrue();
        memory["indexing"]!.GetValue<string>().ShouldBe("auto");
    }

    // ── DevOriginEnforcementCheck ─────────────────────────────────────────────

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenKeylessAndFlagAbsent()
    {
        // #1946: the guard is ON by default, so an absent flag already protects the keyless
        // gateway - nothing to recommend.
        var root = new JsonObject();
        new DevOriginEnforcementCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenLegacyApiKeySet()
    {
        var root = JsonNode.Parse("{\"apiKey\":\"secret\"}") !.AsObject();
        new DevOriginEnforcementCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenGatewayApiKeysSet()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"apiKeys\":{\"k1\":{\"apiKey\":\"x\"}}}}") !.AsObject();
        new DevOriginEnforcementCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenFlagAlreadyEnabled()
    {
        var root = JsonNode.Parse("{\"FeatureManagement\":{\"GatewayDevOriginEnforcement\":true}}") !.AsObject();
        new DevOriginEnforcementCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_ApplicableWhenFlagExplicitlyDisabled()
    {
        var root = JsonNode.Parse("{\"FeatureManagement\":{\"GatewayDevOriginEnforcement\":false}}") !.AsObject();
        new DevOriginEnforcementCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void DevOriginEnforcementCheck_Apply_EnablesFlagAndSeedsLocalhostOrigin()
    {
        var root = new JsonObject();
        new DevOriginEnforcementCheck().Apply(root);

        root["FeatureManagement"]!["GatewayDevOriginEnforcement"]!.GetValue<bool>().ShouldBeTrue();
        var origins = root["gateway"]!["cors"]!["allowedOrigins"]!.AsArray();
        origins.Count.ShouldBe(1);
        origins[0]!.GetValue<string>().ShouldBe("http://localhost:5005");
    }

    [Fact]
    public void DevOriginEnforcementCheck_Apply_PreservesExistingAllowedOrigins()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"cors\":{\"allowedOrigins\":[\"https://portal.example.com\"]}}}") !.AsObject();
        new DevOriginEnforcementCheck().Apply(root);

        var origins = root["gateway"]!["cors"]!["allowedOrigins"]!.AsArray();
        origins.Count.ShouldBe(1);
        origins[0]!.GetValue<string>().ShouldBe("https://portal.example.com");
        root["FeatureManagement"]!["GatewayDevOriginEnforcement"]!.GetValue<bool>().ShouldBeTrue();
    }
}
