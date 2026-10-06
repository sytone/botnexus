using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// Bounded scanner evidence for one blocked skill finding. The record deliberately excludes source
/// text, rendered scanner evidence, absolute paths, and operator-authored acknowledgement data.
/// </summary>
public sealed record SkillSecurityFindingEvidence
{
    private const int MaxSkillLength = 64;
    private const int MaxRuleLength = 128;
    private const int MaxRelativePathLength = 512;

    /// <summary>Version of this evidence/revision contract.</summary>
    public const string CurrentScannerVersion = "skill-security-scanner/1";

    public required string Skill { get; init; }
    public required string Scope { get; init; }
    public required string RuleId { get; init; }
    public required string Severity { get; init; }
    public string? RelativePath { get; init; }
    public int? Line { get; init; }
    public required string ScannerVersion { get; init; }
    public required string ScannerFindingId { get; init; }
    public string? FileSha256 { get; init; }
    public string? RevisionId { get; init; }
    public required bool IsComplete { get; init; }
    public string? MissingReason { get; init; }

    /// <summary>
    /// Creates evidence from scanner-owned fields and the exact scanned file bytes. An invalid or
    /// unreadable location returns explicit incomplete evidence rather than a partially authorizable record.
    /// </summary>
    public static SkillSecurityFindingEvidence Create(
        string skillName,
        SkillSource source,
        string skillDirectory,
        ScanFinding finding,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var skill = Bound(skillName, MaxSkillLength);
        var rule = Bound(finding.RuleId, MaxRuleLength);
        var scannerFindingId = SkillSecurityScanner.ComputeFindingId(finding);
        var common = new SkillSecurityFindingEvidence
        {
            Skill = skill,
            Scope = source.ToString(),
            RuleId = rule,
            Severity = finding.Severity.ToString(),
            ScannerVersion = CurrentScannerVersion,
            ScannerFindingId = scannerFindingId,
            IsComplete = false,
            MissingReason = "unknown"
        };

        if (!TryCanonicalRelativePath(skillDirectory, finding.File, out var relativePath))
            return common with { MissingReason = "invalid-relative-path" };

        var sha256 = SkillSecurityAcknowledgements.ComputeSha256(fileSystem, finding.File);
        if (!SkillSecurityAcknowledgements.IsValidSha256(sha256))
            return common with
            {
                RelativePath = relativePath,
                Line = finding.Line > 0 ? finding.Line : null,
                MissingReason = "file-hash-unavailable"
            };

        if (finding.Line <= 0)
            return common with
            {
                RelativePath = relativePath,
                FileSha256 = sha256,
                MissingReason = "invalid-line"
            };

        var revisionInput = string.Join('\n',
            skill,
            source,
            rule,
            finding.Severity,
            relativePath,
            finding.Line,
            CurrentScannerVersion,
            scannerFindingId,
            sha256);
        var revisionId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput)));

        return common with
        {
            RelativePath = relativePath,
            Line = finding.Line,
            FileSha256 = sha256,
            RevisionId = revisionId,
            IsComplete = true,
            MissingReason = null
        };
    }

    private static bool TryCanonicalRelativePath(string root, string file, out string? relativePath)
    {
        relativePath = null;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFile = Path.GetFullPath(file);
            var prefix = fullRoot + Path.DirectorySeparatorChar;
            if (!fullFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var candidate = Path.GetRelativePath(fullRoot, fullFile).Replace('\\', '/');
            if (candidate.Length == 0 || candidate.Length > MaxRelativePathLength || Path.IsPathRooted(candidate) ||
                candidate.Split('/').Any(segment => segment is "" or "." or ".."))
                return false;

            relativePath = candidate;
            return true;
        }
        catch (Exception) when (root is not null && file is not null)
        {
            return false;
        }
    }

    private static string Bound(string value, int maximum)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }
}
