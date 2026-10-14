using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class SkillSecurityFindingCaptureTests
{
    [Fact]
    public void Capture_RetainsRevisionPinnedSkillFindingFieldsAndPromptOrigin()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        using var provider = new LogDiagnosticsProvider(buffer);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("BotNexus.Extensions.Skills.SkillDiscovery");

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["AgentId"] = "agent-a",
            ["SessionId"] = "session-a",
            ["DiagnosticTrigger"] = "PromptConstruction",
            ["ToolCallId"] = null
        }))
        {
            logger.LogWarning(
                "Skill {SkillName} ({SkillScope}) blocked by {RuleId} {FindingSeverity} finding {ScannerFindingId} at {RelativePath}:{FindingLine}; scanner {ScannerVersion}, file {FileSha256}, revision {FindingId}, evidence {EvidenceStatus} ({EvidenceReason}).",
                "sample-skill", "Workspace", "dangerous-exec", "Critical",
                new string('a', 64), "scripts/run.mjs", 2, "skill-security-scanner/1",
                new string('b', 64), new string('c', 64), "complete", "none");
        }

        var occurrence = buffer.GetPatterns(TimeSpan.FromHours(1)).Single().RecentOccurrences.Single();
        occurrence.Properties["SkillName"].ShouldBe("sample-skill");
        occurrence.Properties["SkillScope"].ShouldBe("Workspace");
        occurrence.Properties["RuleId"].ShouldBe("dangerous-exec");
        occurrence.Properties["FindingSeverity"].ShouldBe("Critical");
        occurrence.Properties["RelativePath"].ShouldBe("scripts/run.mjs");
        occurrence.Properties["FindingLine"].ShouldBe("2");
        occurrence.Properties["ScannerVersion"].ShouldBe("skill-security-scanner/1");
        occurrence.Properties["ScannerFindingId"].ShouldBe(new string('a', 64));
        occurrence.Properties["FileSha256"].ShouldBe(new string('b', 64));
        occurrence.Properties["FindingId"].ShouldBe(new string('c', 64));
        occurrence.Properties["EvidenceStatus"].ShouldBe("complete");
        occurrence.Properties["EvidenceReason"].ShouldBe("none");
        occurrence.Properties["DiagnosticTrigger"].ShouldBe("PromptConstruction");
        occurrence.Properties.ShouldContainKey("ToolCallId");
        occurrence.Properties["ToolCallId"].ShouldBeNull();
    }
}
