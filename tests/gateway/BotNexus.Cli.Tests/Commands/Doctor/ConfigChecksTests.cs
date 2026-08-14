using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Behaviour-parity tests for the <c>doctor config</c> checks. #2887 moved the checks off raw
/// <c>JsonObject</c> indexing onto the canonical-path <see cref="ConfigDocument"/> surface; the
/// assertions here are unchanged in meaning - each seeds the same document and asserts the same
/// applicability and post-fix state - so a divergence in behaviour would surface as a failure here.
/// </summary>
public sealed class ConfigChecksTests
{
    private static ConfigDocument Parse(string json) => ConfigDocument.Parse(json);

    // ── ExtensionsBlockCheck ──────────────────────────────────────────────────

    [Fact]
    public void ExtensionsBlockCheck_ApplicableWhenGatewayAbsent()
    {
        new ExtensionsBlockCheck().IsApplicable(ConfigDocument.Empty()).ShouldBeTrue();
    }

    [Fact]
    public void ExtensionsBlockCheck_ApplicableWhenExtensionsAbsent()
    {
        new ExtensionsBlockCheck().IsApplicable(Parse("{\"gateway\":{}}")).ShouldBeTrue();
    }

    [Fact]
    public void ExtensionsBlockCheck_NotApplicableWhenEnabled()
    {
        new ExtensionsBlockCheck()
            .IsApplicable(Parse("{\"gateway\":{\"extensions\":{\"enabled\":true}}}"))
            .ShouldBeFalse();
    }

    [Fact]
    public void ExtensionsBlockCheck_ApplicableWhenExplicitlyDisabled()
    {
        new ExtensionsBlockCheck()
            .IsApplicable(Parse("{\"gateway\":{\"extensions\":{\"enabled\":false}}}"))
            .ShouldBeTrue();
    }

    [Fact]
    public void ExtensionsBlockCheck_Apply_SetsEnabled()
    {
        var config = ConfigDocument.Empty();
        new ExtensionsBlockCheck().Apply(config);

        config.GetBool("gateway.extensions.enabled").ShouldBe(true);
    }

    // ── SkillsWorldDefaultCheck ───────────────────────────────────────────────

    [Fact]
    public void SkillsWorldDefaultCheck_ApplicableWhenDefaultsAbsent()
    {
        new SkillsWorldDefaultCheck().IsApplicable(ConfigDocument.Empty()).ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_ApplicableWhenSkillsKeyAbsent()
    {
        new SkillsWorldDefaultCheck()
            .IsApplicable(Parse("{\"gateway\":{\"extensions\":{\"defaults\":{}}}}"))
            .ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_NotApplicableWhenPresent()
    {
        new SkillsWorldDefaultCheck()
            .IsApplicable(Parse("{\"gateway\":{\"extensions\":{\"defaults\":{\"botnexus-skills\":{\"enabled\":true}}}}}"))
            .ShouldBeFalse();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_ApplicableWhenExplicitlyDisabled()
    {
        new SkillsWorldDefaultCheck()
            .IsApplicable(Parse("{\"gateway\":{\"extensions\":{\"defaults\":{\"botnexus-skills\":{\"enabled\":false}}}}}"))
            .ShouldBeTrue();
    }

    [Fact]
    public void SkillsWorldDefaultCheck_Apply_SetsFullPath()
    {
        var config = ConfigDocument.Empty();
        new SkillsWorldDefaultCheck().Apply(config);

        config.GetBool("gateway.extensions.defaults.botnexus-skills.enabled").ShouldBe(true);
        // extensions block should also be enabled
        config.GetBool("gateway.extensions.enabled").ShouldBe(true);
    }

    [Fact]
    public void SkillsWorldDefaultCheck_Apply_PreservesExistingDefaults()
    {
        var config = Parse("{\"gateway\":{\"extensions\":{\"enabled\":true,\"defaults\":{\"other-ext\":{\"enabled\":true}}}}}");
        new SkillsWorldDefaultCheck().Apply(config);

        var defaults = config.GetEntryKeys("gateway.extensions.defaults");
        defaults.ShouldContain("other-ext");
        defaults.ShouldContain("botnexus-skills");
    }

    // ── CronCheck ─────────────────────────────────────────────────────────────

    [Fact]
    public void CronCheck_ApplicableWhenAbsent()
    {
        new CronCheck().IsApplicable(ConfigDocument.Empty()).ShouldBeTrue();
    }

    [Fact]
    public void CronCheck_NotApplicableWhenPresent()
    {
        new CronCheck().IsApplicable(Parse("{\"cron\":{\"enabled\":true}}")).ShouldBeFalse();
    }

    [Fact]
    public void CronCheck_Apply_AddsCronBlock()
    {
        var config = ConfigDocument.Empty();
        new CronCheck().Apply(config);

        config.GetBool("cron.enabled").ShouldBe(true);
        config.GetInt("cron.tickIntervalSeconds").ShouldBe(60);
    }

    [Fact]
    public void CronCheck_Apply_DoesNotOverwriteExisting()
    {
        var config = Parse("{\"cron\":{\"enabled\":true,\"tickIntervalSeconds\":30}}");
        new CronCheck().Apply(config);

        // already present - Apply is a no-op
        config.GetInt("cron.tickIntervalSeconds").ShouldBe(30);
    }

    // ── MemoryAgentDefaultCheck ───────────────────────────────────────────────

    [Fact]
    public void MemoryAgentDefaultCheck_NotApplicableWhenNoAgentsBlock()
    {
        // no agents at all - check should be silent
        new MemoryAgentDefaultCheck().IsApplicable(ConfigDocument.Empty()).ShouldBeFalse();
    }

    [Fact]
    public void MemoryAgentDefaultCheck_ApplicableWhenDefaultsMemoryAbsent()
    {
        new MemoryAgentDefaultCheck().IsApplicable(Parse("{\"agents\":{\"defaults\":{}}}")).ShouldBeTrue();
    }

    [Fact]
    public void MemoryAgentDefaultCheck_NotApplicableWhenPresent()
    {
        new MemoryAgentDefaultCheck()
            .IsApplicable(Parse("{\"agents\":{\"defaults\":{\"memory\":{\"enabled\":true,\"indexing\":\"auto\"}}}}"))
            .ShouldBeFalse();
    }

    [Fact]
    public void MemoryAgentDefaultCheck_Apply_SetsMemoryBlock()
    {
        var config = Parse("{\"agents\":{\"defaults\":{}}}");
        new MemoryAgentDefaultCheck().Apply(config);

        config.GetBool("agents.defaults.memory.enabled").ShouldBe(true);
        config.TryGetString("agents.defaults.memory.indexing", out var indexing).ShouldBeTrue();
        indexing.ShouldBe("auto");
    }

    // ── DevOriginEnforcementCheck ─────────────────────────────────────────────

    [Fact]
    public void DevOriginEnforcementCheck_ApplicableWhenKeylessAndFlagAbsent()
    {
        // Empty config == keyless dev mode, no flag -> recommend enabling.
        new DevOriginEnforcementCheck().IsApplicable(ConfigDocument.Empty()).ShouldBeTrue();
    }

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenLegacyApiKeySet()
    {
        new DevOriginEnforcementCheck().IsApplicable(Parse("{\"apiKey\":\"secret\"}")).ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenGatewayApiKeysSet()
    {
        new DevOriginEnforcementCheck()
            .IsApplicable(Parse("{\"gateway\":{\"apiKeys\":{\"k1\":{\"apiKey\":\"x\"}}}}"))
            .ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_NotApplicableWhenFlagAlreadyEnabled()
    {
        new DevOriginEnforcementCheck()
            .IsApplicable(Parse("{\"FeatureManagement\":{\"GatewayDevOriginEnforcement\":true}}"))
            .ShouldBeFalse();
    }

    [Fact]
    public void DevOriginEnforcementCheck_ApplicableWhenFlagExplicitlyDisabled()
    {
        new DevOriginEnforcementCheck()
            .IsApplicable(Parse("{\"FeatureManagement\":{\"GatewayDevOriginEnforcement\":false}}"))
            .ShouldBeTrue();
    }

    [Fact]
    public void DevOriginEnforcementCheck_Apply_EnablesFlagAndSeedsLocalhostOrigin()
    {
        var config = ConfigDocument.Empty();
        new DevOriginEnforcementCheck().Apply(config);

        config.GetBool("FeatureManagement.GatewayDevOriginEnforcement").ShouldBe(true);

        var origins = config.GetStringList("gateway.cors.allowedOrigins");
        origins.Count.ShouldBe(1);
        origins[0].ShouldBe("http://localhost:5005");
    }

    [Fact]
    public void DevOriginEnforcementCheck_Apply_PreservesExistingAllowedOrigins()
    {
        var config = Parse("{\"gateway\":{\"cors\":{\"allowedOrigins\":[\"https://portal.example.com\"]}}}");
        new DevOriginEnforcementCheck().Apply(config);

        var origins = config.GetStringList("gateway.cors.allowedOrigins");
        origins.Count.ShouldBe(1);
        origins[0].ShouldBe("https://portal.example.com");
        config.GetBool("FeatureManagement.GatewayDevOriginEnforcement").ShouldBe(true);
    }
}
