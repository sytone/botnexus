using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Extensions.Skills.Security;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillSecurityFindingsEndpointTests
{
    private const string SkillsRoot = "/home/user/.botnexus/skills";
    private const string ConfigPath = "/home/user/.botnexus/config.json";
    private const string DangerousSource = "const { exec } = require('child_process');\nexec('git status');";

    [Fact]
    public async Task GetSecurityFindings_Admin_ReturnsUnresolvedCriticalEvidence()
    {
        var fixture = CreateFixture(("shelling-skill", "scripts/run.mjs", DangerousSource));

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            AdminContext(), fixture.FileSystem, fixture.Writer, SkillsRoot);

        var response = result.ShouldBeOfType<Ok<SkillSecurityFindingsResponse>>().Value!;
        var finding = response.Findings.ShouldHaveSingleItem();
        finding.Skill.ShouldBe("shelling-skill");
        finding.RuleId.ShouldBe("dangerous-exec");
        finding.Severity.ShouldBe("Critical");
        finding.RelativePath.ShouldBe("scripts/run.mjs");
        finding.IsComplete.ShouldBeTrue();
        response.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task GetSecurityFindings_AdminWithNoCriticalFindings_ReturnsEmptyResponse()
    {
        var fixture = CreateFixture(("safe-skill", "scripts/run.mjs", "console.log('safe');"));

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            AdminContext(), fixture.FileSystem, fixture.Writer, SkillsRoot);

        var response = result.ShouldBeOfType<Ok<SkillSecurityFindingsResponse>>().Value!;
        response.Findings.ShouldBeEmpty();
        response.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task GetSecurityFindings_ExactDefaultAcknowledgement_FiltersFinding()
    {
        var fixture = CreateFixture(("shelling-skill", "scripts/run.mjs", DangerousSource));
        var file = $"{SkillsRoot}/shelling-skill/scripts/run.mjs";
        var hash = SkillSecurityAcknowledgements.ComputeSha256(fixture.FileSystem, file)!;
        var findingId = SkillSecurityScanner.ComputeFindingId("dangerous-exec", ScanSeverity.Critical,
            "Shell command execution detected (child_process)");
        fixture.FileSystem.File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            agents = new
            {
                defaults = new
                {
                    extensions = new Dictionary<string, object>
                    {
                        [SkillsExtensionJson.ExtensionId] = new
                        {
                            securityAcknowledgements = new[]
                            {
                                new
                                {
                                    skill = "shelling-skill",
                                    ruleId = "dangerous-exec",
                                    file = "scripts/run.mjs",
                                    severity = "Critical",
                                    findingId,
                                    sha256 = hash
                                }
                            }
                        }
                    }
                }
            }
        }, SkillsExtensionJson.Options));

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            AdminContext(), fixture.FileSystem, fixture.Writer, SkillsRoot);

        result.ShouldBeOfType<Ok<SkillSecurityFindingsResponse>>().Value!.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSecurityFindings_Viewer_IsForbidden()
    {
        var fixture = CreateFixture(("shelling-skill", "scripts/run.mjs", DangerousSource));

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            CallerContext(isAdmin: false), fixture.FileSystem, fixture.Writer, SkillsRoot);

        result.GetType().Name.ShouldContain("Forbid");
    }

    [Fact]
    public async Task GetSecurityFindings_ResponseContainsOnlyCanonicalRelativePathsAndNoSource()
    {
        var fixture = CreateFixture(
            ("shelling-skill", "scripts/run.mjs", DangerousSource),
            ("shelling-skill", "scripts/nested/evil.mjs", "eval('malicious source marker');"));

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            AdminContext(), fixture.FileSystem, fixture.Writer, SkillsRoot);

        var response = result.ShouldBeOfType<Ok<SkillSecurityFindingsResponse>>().Value!;
        response.Findings.Count.ShouldBe(2);
        foreach (var finding in response.Findings)
        {
            finding.RelativePath.ShouldNotBeNull();
            Path.IsPathRooted(finding.RelativePath).ShouldBeFalse();
            finding.RelativePath.Split('/').Any(segment => segment == "." || segment == "..").ShouldBeFalse();
        }
        var json = JsonSerializer.Serialize(response, SkillsExtensionJson.Options);
        json.ShouldNotContain(SkillsRoot);
        json.ShouldNotContain("malicious source marker");
        json.ShouldNotContain("evidence", Case.Insensitive);
    }

    [Fact]
    public async Task GetSecurityFindings_MoreThanMaximumSkillBudget_ReturnsBoundedTruncatedResponse()
    {
        var files = Enumerable.Range(0, SkillsEndpointContributor.MaximumScannedSkills + 1)
            .Select(index => ($"safe-skill-{index}", "scripts/run.mjs", "console.log('safe');"))
            .ToArray();
        var fixture = CreateFixture(files);

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            AdminContext(), fixture.FileSystem, fixture.Writer, SkillsRoot);

        var response = result.ShouldBeOfType<Ok<SkillSecurityFindingsResponse>>().Value!;
        response.Findings.ShouldBeEmpty();
        response.IsTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task GetSecurityFindings_MoreThanMaximum_ReturnsBoundedTruncatedResponse()
    {
        var files = Enumerable.Range(0, SkillsEndpointContributor.MaximumSecurityFindings + 1)
            .Select(index => ($"skill-{index}", "scripts/run.mjs", DangerousSource))
            .ToArray();
        var fixture = CreateFixture(files);

        var result = await SkillsEndpointContributor.GetSecurityFindings(
            AdminContext(), fixture.FileSystem, fixture.Writer, SkillsRoot);

        var response = result.ShouldBeOfType<Ok<SkillSecurityFindingsResponse>>().Value!;
        response.Findings.Count.ShouldBe(SkillsEndpointContributor.MaximumSecurityFindings);
        response.IsTruncated.ShouldBeTrue();
    }

    private static Fixture CreateFixture(params (string Skill, string File, string Source)[] files)
    {
        var data = new Dictionary<string, MockFileData>
        {
            [ConfigPath] = new MockFileData("{}")
        };
        foreach (var (skill, file, source) in files)
            data[$"{SkillsRoot}/{skill}/{file}"] = new MockFileData(source);

        var fs = new MockFileSystem(data);
        return new Fixture(fs, new PlatformConfigWriter(ConfigPath, fs));
    }

    private static DefaultHttpContext AdminContext() => CallerContext(isAdmin: true);

    private static DefaultHttpContext CallerContext(bool isAdmin)
    {
        var context = new DefaultHttpContext();
        context.Items["BotNexus.Gateway.CallerIdentity"] = new GatewayCallerIdentity
        {
            CallerId = "operator-1",
            IsAdmin = isAdmin
        };
        return context;
    }

    private sealed record Fixture(MockFileSystem FileSystem, PlatformConfigWriter Writer);
}
