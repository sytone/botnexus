using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

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

    /// <summary>Scanner severity reviewed by the operator. Required for authorization.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ScanSeverity>))]
    public ScanSeverity? Severity { get; set; }

    /// <summary>Stable scanner-owned identity of the exact finding that was reviewed.</summary>
    public string? FindingId { get; set; }

    /// <summary>
    /// Optional SHA-256 (hex) of the reviewed file content. When set, the acknowledgement applies
    /// only while the file still hashes to this value, so an edit to an already-approved file
    /// revokes the approval rather than inheriting it.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>Free-text operator justification. Not matched on; carried for audit only.</summary>
    public string? Reason { get; set; }

    /// <summary>Pseudonymized identity of the operator who acknowledged the finding.</summary>
    public string? OperatorPseudonym { get; set; }

    /// <summary>UTC instant at which the acknowledgement was persisted.</summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
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
        ScanSeverity severity,
        string findingId,
        IFileSystem fileSystem,
        string absoluteFilePath)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);

        if (acknowledgement.Severity is null
            || string.IsNullOrWhiteSpace(acknowledgement.FindingId)
            || !IsValidSha256(acknowledgement.Sha256))
            return false;

        if (!string.Equals(acknowledgement.Skill, skillName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(acknowledgement.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Normalise(acknowledgement.File), Normalise(relativeFile), StringComparison.OrdinalIgnoreCase)
            || acknowledgement.Severity != severity
            || !string.Equals(acknowledgement.FindingId, findingId, StringComparison.Ordinal))
            return false;

        var actual = ComputeSha256(fileSystem, absoluteFilePath);
        return actual is not null
            && string.Equals(actual, acknowledgement.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether a value is exactly a hexadecimal SHA-256 digest.</summary>
    public static bool IsValidSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

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
