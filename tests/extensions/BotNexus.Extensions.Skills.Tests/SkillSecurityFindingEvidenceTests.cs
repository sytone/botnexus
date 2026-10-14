using BotNexus.Extensions.Skills.Security;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillSecurityFindingEvidenceTests
{
    private static readonly string SkillRoot = Path.Combine(Path.GetTempPath(), "finding-evidence", "skills", "sample-skill");

    [Fact]
    public void Create_ProducesRevisionPinnedBoundedEvidenceWithoutSourceContent()
    {
        var fs = CreateSkill("exec('TOP-SECRET');");
        var file = Path.Combine(SkillRoot, "scripts", "run.mjs");
        var finding = SkillSecurityScanner.ScanSource(fs.File.ReadAllText(file), file)
            .Single(candidate => candidate.RuleId == "dangerous-exec");

        var evidence = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot, finding, fs);

        evidence.IsComplete.ShouldBeTrue();
        evidence.MissingReason.ShouldBeNull();
        evidence.Skill.ShouldBe("sample-skill");
        evidence.Scope.ShouldBe(nameof(SkillSource.Workspace));
        evidence.RuleId.ShouldBe("dangerous-exec");
        evidence.Severity.ShouldBe(nameof(ScanSeverity.Critical));
        evidence.RelativePath.ShouldBe("scripts/run.mjs");
        evidence.ScannerVersion.ShouldNotBeNullOrWhiteSpace();
        evidence.ScannerFindingId.ShouldBe(SkillSecurityScanner.ComputeFindingId(finding));
        evidence.FileSha256.ShouldBe(SkillSecurityAcknowledgements.ComputeSha256(fs, file));
        var revisionId = evidence.RevisionId;
        revisionId.ShouldNotBeNull();
        revisionId.ShouldMatch("^[0-9a-f]{64}$");
        evidence.ToString().ShouldNotContain("TOP-SECRET");
        evidence.ToString().ShouldNotContain(SkillRoot);
    }

    [Fact]
    public void Create_RejectsTraversalAndMakesMissingRevisionEvidenceExplicit()
    {
        var fs = CreateSkill("exec('safe');");
        var outside = Path.Combine(Path.GetTempPath(), "finding-evidence", "outside.mjs");
        fs.File.WriteAllText(outside, "const { exec } = require('child_process');\nexec('outside');");
        var finding = SkillSecurityScanner.ScanSource(fs.File.ReadAllText(outside), outside)
            .Single(candidate => candidate.RuleId == "dangerous-exec");

        var evidence = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot, finding, fs);

        evidence.IsComplete.ShouldBeFalse();
        evidence.MissingReason.ShouldBe("invalid-relative-path");
        evidence.RelativePath.ShouldBeNull();
        evidence.RevisionId.ShouldBeNull();
        evidence.FileSha256.ShouldBeNull();
    }

    [Fact]
    public void Discovery_EmitsOneStructuredRevisionPerOutstandingFinding()
    {
        var fs = CreateSkill("exec('TOP-SECRET');");
        var logger = new StructuredLogger();

        SkillDiscovery.Discover(
            Path.GetDirectoryName(SkillRoot), null, null, fs, logger);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Template.ShouldBe(
            "Skill {SkillName} ({SkillScope}) blocked by {RuleId} {FindingSeverity} finding {ScannerFindingId} at {RelativePath}:{FindingLine}; scanner {ScannerVersion}, file {FileSha256}, revision {FindingId}, evidence {EvidenceStatus} ({EvidenceReason}).");
        warning.Properties["SkillName"].ShouldBe("sample-skill");
        warning.Properties["SkillScope"].ShouldBe(nameof(SkillSource.Global));
        warning.Properties["RuleId"].ShouldBe("dangerous-exec");
        warning.Properties["FindingSeverity"].ShouldBe(nameof(ScanSeverity.Critical));
        warning.Properties["RelativePath"].ShouldBe("scripts/run.mjs");
        warning.Properties["ScannerVersion"].ShouldNotBeNull();
        warning.Properties["ScannerFindingId"].ShouldNotBeNull();
        warning.Properties["FileSha256"].ShouldNotBeNull();
        var revisionId = warning.Properties["FindingId"];
        revisionId.ShouldNotBeNull();
        revisionId.ShouldMatch("^[0-9a-f]{64}$");
        warning.Properties["EvidenceStatus"].ShouldBe("complete");
        warning.Properties["EvidenceReason"].ShouldBe("none");
        warning.Properties.Values.Any(value => value is not null && value.Contains("TOP-SECRET", StringComparison.Ordinal)).ShouldBeFalse();
        warning.Properties.Values.Any(value => value is not null && value.Contains(SkillRoot, StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public void RevisionChangesWhenAnyAuthorizingFindingFieldChanges()
    {
        var fs = CreateSkill("exec('safe');");
        var file = Path.Combine(SkillRoot, "scripts", "run.mjs");
        var finding = SkillSecurityScanner.ScanSource(fs.File.ReadAllText(file), file)
            .Single(candidate => candidate.RuleId == "dangerous-exec");
        var baseline = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot, finding, fs);

        fs.File.WriteAllText(file, "exec('changed');");
        var changedHash = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot, finding, fs);
        var changedRule = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot,
            finding with { RuleId = "different-rule" }, fs);
        var changedSeverity = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot,
            finding with { Severity = ScanSeverity.Warn }, fs);
        var otherFile = Path.Combine(SkillRoot, "scripts", "other.mjs");
        fs.File.WriteAllText(otherFile, "exec('safe');");
        var changedFile = SkillSecurityFindingEvidence.Create(
            "sample-skill", SkillSource.Workspace, SkillRoot,
            finding with { File = otherFile }, fs);

        new[] { changedHash, changedRule, changedSeverity, changedFile }
            .ShouldAllBe(candidate => candidate.RevisionId != baseline.RevisionId);
    }

    private static MockFileSystem CreateSkill(string invocation)
    {
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(Path.Combine(SkillRoot, "scripts"));
        fs.File.WriteAllText(Path.Combine(SkillRoot, "SKILL.md"), """
            ---
            name: sample-skill
            description: Security evidence test skill.
            ---
            # Sample
            """);
        fs.File.WriteAllText(
            Path.Combine(SkillRoot, "scripts", "run.mjs"),
            "const { exec } = require('child_process');\n" + invocation);
        return fs;
    }

    private sealed class StructuredLogger : ILogger
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning || state is not IEnumerable<KeyValuePair<string, object?>> values)
                return;

            var properties = values.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString(), StringComparer.Ordinal);
            Entries.Add(new Entry(properties["{OriginalFormat}"]!, properties));
        }
    }

    private sealed record Entry(string Template, IReadOnlyDictionary<string, string?> Properties);
}
