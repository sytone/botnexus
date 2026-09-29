using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills.Security;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class ExactSecurityAcknowledgementRepairTests
{
    private const string Source = "const { exec } = require('child_process');\nexec('git status');";

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void IncompleteAcknowledgement_NeverAuthorizes(bool includeHash, bool includeSeverity, bool includeFindingId)
    {
        var (fs, root, script, finding) = CreateSkill();
        var acknowledgement = ExactAcknowledgement(fs, script, finding);
        if (!includeHash) acknowledgement.Sha256 = null;
        if (!includeSeverity) acknowledgement.Severity = null;
        if (!includeFindingId) acknowledgement.FindingId = null;

        SkillDiscovery.Discover(root, null, null, fs, securityAcknowledgements: [acknowledgement])
            .ShouldNotContain(skill => skill.Name == "shelling-skill");
    }

    [Fact]
    public void FindingIdentity_IgnoresHostileEvidenceButBindsRuleSeverityAndMessage()
    {
        var first = new ScanFinding("rule", ScanSeverity.Critical, "a.ps1", 1, "stable message", "hostile one");
        var second = first with { File = "b.ps1", Line = 99, Evidence = "hostile two" };

        SkillSecurityScanner.ComputeFindingId(first).ShouldBe(SkillSecurityScanner.ComputeFindingId(second));
        SkillSecurityScanner.ComputeFindingId(first)
            .ShouldNotBe(SkillSecurityScanner.ComputeFindingId(first with { Message = "changed" }));
    }

    [Fact]
    public void Resolve_DefaultAcknowledgementsReachDiscoveryWithoutOverridingNamedSemantics()
    {
        var (fs, root, script, finding) = CreateSkill();
        var acknowledgement = ExactAcknowledgement(fs, script, finding);
        var descriptor = SkillsConfigResolutionTestData.Descriptor(
            "{\"enabled\":false,\"maxLoadedSkills\":7}",
            System.Text.Json.JsonSerializer.Serialize(new SkillsConfig { SecurityAcknowledgements = [acknowledgement] },
                BotNexus.Gateway.Abstractions.Models.ExtensionConfigBinder.Options));

        var config = SkillsConfigResolver.Resolve(descriptor);

        config.ShouldNotBeNull();
        config.Enabled.ShouldBeFalse();
        config.MaxLoadedSkills.ShouldBe(7);
        SkillDiscovery.Discover(root, null, null, fs, securityAcknowledgements: config.SecurityAcknowledgements)
            .ShouldContain(skill => skill.Name == "shelling-skill");
    }

    private static (MockFileSystem Fs, string Root, string Script, ScanFinding Finding) CreateSkill()
    {
        var root = Path.Combine(Path.GetTempPath(), "exact-ack-repair", "skills");
        var skillDir = Path.Combine(root, "shelling-skill");
        var script = Path.Combine(skillDir, "scripts", "run.mjs");
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(skillDir, "SKILL.md")] = new("---\nname: shelling-skill\ndescription: Shells out.\n---\n"),
            [script] = new(Source)
        });
        return (fs, root, script, SkillSecurityScanner.ScanSource(Source, script).Single());
    }

    private static SkillSecurityAcknowledgement ExactAcknowledgement(
        MockFileSystem fs, string script, ScanFinding finding) => new()
    {
        Skill = "shelling-skill",
        RuleId = finding.RuleId,
        File = "scripts/run.mjs",
        Severity = finding.Severity,
        FindingId = SkillSecurityScanner.ComputeFindingId(finding),
        Sha256 = SkillSecurityAcknowledgements.ComputeSha256(fs, script),
        Reason = "Reviewed.",
        OperatorPseudonym = "operator-pseudonym",
        AcknowledgedAtUtc = DateTimeOffset.UtcNow
    };
}

internal static class SkillsConfigResolutionTestData
{
    internal static BotNexus.Gateway.Abstractions.Models.AgentDescriptor Descriptor(string namedJson, string defaultsJson)
    {
        using var named = System.Text.Json.JsonDocument.Parse(namedJson);
        using var defaults = System.Text.Json.JsonDocument.Parse(defaultsJson);
        return new BotNexus.Gateway.Abstractions.Models.AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "model",
            ApiProvider = "provider",
            ExtensionConfig = new Dictionary<string, System.Text.Json.JsonElement>
            {
                [SkillsExtensionJson.ExtensionId] = named.RootElement.Clone()
            },
            DefaultExtensionConfig = new Dictionary<string, System.Text.Json.JsonElement>
            {
                [SkillsExtensionJson.ExtensionId] = defaults.RootElement.Clone()
            }
        };
    }
}
