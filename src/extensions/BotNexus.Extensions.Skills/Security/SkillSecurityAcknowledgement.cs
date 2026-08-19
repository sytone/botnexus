using System.IO.Abstractions;
using System.Security.Cryptography;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// An operator-recorded acknowledgement that ONE specific critical scan finding in ONE specific
/// file of ONE specific skill has been reviewed and accepted (#3355).
/// </summary>
/// <remarks>
/// This is deliberately not a "disable scanning" switch. The unit of trust is a single finding
/// identity — <c>skill + ruleId + relative file path</c> — because that is the granularity at
/// which a human actually reviewed something. Anything coarser would silently absorb findings the
/// operator never saw, which is the failure mode the issue was filed against.
/// </remarks>
public sealed class SkillSecurityAcknowledgement
{
    /// <summary>Skill directory name this acknowledgement applies to. Case-insensitive.</summary>
    public string Skill { get; set; } = string.Empty;

    /// <summary>Scanner rule id that was reviewed, e.g. <c>dangerous-exec</c>.</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>
    /// Path of the reviewed file RELATIVE to the skill directory. Authored with either slash
    /// style; comparison is normalised to forward slashes.
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Optional SHA-256 (hex) of the reviewed file content. When set, the acknowledgement applies
    /// only while the file still hashes to this value, so an edit to an already-approved file
    /// revokes the approval rather than inheriting it.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>Free-text operator justification. Not matched on; carried for audit only.</summary>
    public string? Reason { get; set; }
}

/// <summary>Matching logic for <see cref="SkillSecurityAcknowledgement"/>.</summary>
public static class SkillSecurityAcknowledgements
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="acknowledgement"/> covers exactly the finding
    /// identified by <paramref name="skillName"/>, <paramref name="relativeFile"/> and
    /// <paramref name="ruleId"/> — and, when the acknowledgement pins a hash, only while the file
    /// still has that content.
    /// </summary>
    public static bool IsAcknowledged(
        SkillSecurityAcknowledgement acknowledgement,
        string skillName,
        string relativeFile,
        string ruleId,
        IFileSystem fileSystem,
        string absoluteFilePath)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);

        if (!string.Equals(acknowledgement.Skill, skillName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(acknowledgement.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(Normalise(acknowledgement.File), Normalise(relativeFile), StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(acknowledgement.Sha256))
            return true;

        var actual = ComputeSha256(fileSystem, absoluteFilePath);
        return actual is not null
            && string.Equals(actual, acknowledgement.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lower-case hex SHA-256 of a file's bytes, or <c>null</c> when it cannot be read.</summary>
    public static string? ComputeSha256(IFileSystem fileSystem, string absoluteFilePath)
    {
        try
        {
            var bytes = fileSystem.File.ReadAllBytes(absoluteFilePath);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch
        {
            // An unreadable file cannot be proven to match a pin, so it must not be acknowledged.
            return null;
        }
    }

    private static string Normalise(string path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('.', '/');
}
