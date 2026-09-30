using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Extensions.Skills.Security;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillSecurityAcknowledgementEndpointTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "skill-ack-api");
    private static readonly string SkillsRoot = Path.Combine(Home, "skills");
    private static readonly string ConfigPath = Path.Combine(Home, "config.json");
    private const string Source = "const { exec } = require('child_process');\nexec('git status');";

    [Fact]
    public async Task Acknowledge_CurrentCompleteEvidence_AsAdmin_PersistsPinnedAcknowledgementAndAuditEvent()
    {
        var fixture = CreateFixture();
        var request = ValidRequest(fixture.Hash);

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            request, AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("Created");
        var root = JsonNode.Parse(fixture.FileSystem.File.ReadAllText(ConfigPath))!.AsObject();
        var acknowledgement = root["agents"]!["defaults"]!["extensions"]!["botnexus-skills"]!["securityAcknowledgements"]!
            .AsArray().ShouldHaveSingleItem()!.AsObject();
        acknowledgement["skill"]!.GetValue<string>().ShouldBe("shelling-skill");
        acknowledgement["ruleId"]!.GetValue<string>().ShouldBe("dangerous-exec");
        acknowledgement["file"]!.GetValue<string>().ShouldBe("scripts/run.mjs");
        acknowledgement["severity"]!.GetValue<string>().ShouldBe("Critical");
        acknowledgement["findingId"]!.GetValue<string>().ShouldBe(request.FindingId);
        acknowledgement["operatorPseudonym"]!.GetValue<string>().ShouldBe(ActorPseudonym.For("operator-1"));
        acknowledgement["acknowledgedAtUtc"]!.GetValue<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
        acknowledgement["sha256"]!.GetValue<string>().ShouldBe(fixture.Hash);
        acknowledgement["reason"]!.GetValue<string>().ShouldBe("Reviewed: this skill intentionally invokes git.");

        var audit = fixture.Sink.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe("skill.security-finding.acknowledged");
        audit.Actor.ShouldNotBeNull();
        audit.Actor.Id.ShouldBe(ActorPseudonym.For("operator-1"));
        audit.Target.ShouldNotBeNull();
        audit.Target.Reference.ShouldBe(fixture.Hash);
        audit.TimestampUtc.ShouldNotBe(default);
    }

    [Fact]
    public async Task Acknowledge_Viewer_IsDeniedWithoutWritingOrAuditing()
    {
        var fixture = CreateFixture();

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            ValidRequest(fixture.Hash), CallerContext(isAdmin: false), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("Forbid");
        fixture.FileSystem.File.ReadAllText(ConfigPath).ShouldBe("{}");
        fixture.Sink.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, "Reviewed for this deployment.")]
    [InlineData(true, "   ")]
    public async Task Acknowledge_MissingConfirmationOrReason_FailsClosed(bool confirmed, string reason)
    {
        var fixture = CreateFixture();
        var request = ValidRequest(fixture.Hash) with { Confirmed = confirmed, Reason = reason };

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            request, AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("BadRequest");
        fixture.FileSystem.File.ReadAllText(ConfigPath).ShouldBe("{}");
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("severity")]
    [InlineData("findingId")]
    public async Task Acknowledge_MissingExactEvidence_FailsClosed(string missing)
    {
        var fixture = CreateFixture();
        var valid = ValidRequest(fixture.Hash);
        var request = missing switch
        {
            "hash" => valid with { Sha256 = string.Empty },
            "severity" => valid with { Severity = string.Empty },
            _ => valid with { FindingId = string.Empty }
        };

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            request, AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("BadRequest");
        fixture.FileSystem.File.ReadAllText(ConfigPath).ShouldBe("{}");
        fixture.Sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Acknowledge_IdenticalRetry_DoesNotDuplicateEntryOrAuditEvent()
    {
        var fixture = CreateFixture();
        var request = ValidRequest(fixture.Hash);

        await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            request, AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);
        var retry = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            request, AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        retry.GetType().Name.ShouldContain("Ok");
        var root = JsonNode.Parse(fixture.FileSystem.File.ReadAllText(ConfigPath))!.AsObject();
        root["agents"]!["defaults"]!["extensions"]!["botnexus-skills"]!["securityAcknowledgements"]!
            .AsArray().Count.ShouldBe(1);
        fixture.Sink.Events.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("{\"agents\":true}")]
    [InlineData("{\"agents\":{\"defaults\":{\"extensions\":{\"botnexus-skills\":{\"securityAcknowledgements\":{}}}}}}")]
    public async Task Acknowledge_MalformedCanonicalPath_FailsClosed(string config)
    {
        var fixture = CreateFixture(config);

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            ValidRequest(fixture.Hash), AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("Conflict");
        fixture.FileSystem.File.ReadAllText(ConfigPath).ShouldBe(config);
        fixture.Sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Acknowledge_WhenCurrentHashNoLongerMatchesEvidence_RejectsRace()
    {
        var fixture = CreateFixture();
        var staleHash = fixture.Hash;
        fixture.FileSystem.File.WriteAllText(Path.Combine(SkillsRoot, "shelling-skill", "scripts", "run.mjs"), Source + "\n// changed");

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            ValidRequest(staleHash), AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("Conflict");
        fixture.FileSystem.File.ReadAllText(ConfigPath).ShouldBe("{}");
        fixture.Sink.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("different-rule", "Critical")]
    [InlineData("dangerous-exec", "Warn")]
    public async Task Acknowledge_WhenCurrentScannerEvidenceDoesNotMatch_Rejects(string ruleId, string severity)
    {
        var fixture = CreateFixture();
        var request = ValidRequest(fixture.Hash) with { RuleId = ruleId, Severity = severity };

        var result = await SkillsEndpointContributor.AcknowledgeSecurityFinding(
            request, AdminContext(), fixture.FileSystem, fixture.Writer, fixture.Sink, SkillsRoot);

        result.GetType().Name.ShouldContain("Conflict");
        fixture.FileSystem.File.ReadAllText(ConfigPath).ShouldBe("{}");
    }

    [Fact]
    public void Acknowledgement_ChangedSeverity_RemainsUnresolved()
    {
        var fixture = CreateFixture();
        var acknowledgement = new SkillSecurityAcknowledgement
        {
            Skill = "shelling-skill",
            RuleId = "dangerous-exec",
            File = "scripts/run.mjs",
            Severity = ScanSeverity.Warn,
            FindingId = SkillSecurityScanner.ComputeFindingId("dangerous-exec", ScanSeverity.Warn,
                "Shell command execution detected (child_process)"),
            Sha256 = fixture.Hash,
            Reason = "Reviewed."
        };

        SkillSecurityAcknowledgements.IsAcknowledged(
            acknowledgement, "shelling-skill", "scripts/run.mjs", "dangerous-exec",
            ScanSeverity.Critical, acknowledgement.FindingId!, fixture.FileSystem,
            Path.Combine(SkillsRoot, "shelling-skill", "scripts", "run.mjs")).ShouldBeFalse();
    }

    private static Fixture CreateFixture(string config = "{}")
    {
        var script = Path.Combine(SkillsRoot, "shelling-skill", "scripts", "run.mjs");
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [ConfigPath] = new MockFileData(config),
            [script] = new MockFileData(Source)
        });
        var hash = SkillSecurityAcknowledgements.ComputeSha256(fs, script)!;
        return new Fixture(fs, new PlatformConfigWriter(ConfigPath, fs), new RecordingSink(), hash);
    }

    private static SkillSecurityAcknowledgementRequest ValidRequest(string hash) => new()
    {
        Skill = "shelling-skill",
        RuleId = "dangerous-exec",
        File = "scripts/run.mjs",
        Severity = "Critical",
        FindingId = SkillSecurityScanner.ComputeFindingId("dangerous-exec", ScanSeverity.Critical,
            "Shell command execution detected (child_process)"),
        Sha256 = hash,
        Confirmed = true,
        Reason = "Reviewed: this skill intentionally invokes git."
    };

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

    private sealed record Fixture(
        MockFileSystem FileSystem,
        PlatformConfigWriter Writer,
        RecordingSink Sink,
        string Hash);

    private sealed class RecordingSink : ISecurityEventSink
    {
        public List<SecurityEvent> Events { get; } = [];
        public int Count => Events.Count;
        public void Record(SecurityEvent securityEvent) => Events.Add(securityEvent);
        public IReadOnlyList<SecurityEvent> Snapshot() => Events;
        public void Clear() => Events.Clear();
    }
}
