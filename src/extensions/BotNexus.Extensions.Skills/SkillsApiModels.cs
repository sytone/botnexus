using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Describes a single file-system entry within the skills directory tree.
/// </summary>
public sealed class SkillsEntryDto
{
    /// <summary>Entry name relative to its parent directory.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Skills-relative path for this entry.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Entry type: <c>file</c> or <c>directory</c>.</summary>
    public string Type { get; set; } = "file";

    /// <summary>File size in bytes when <see cref="Type"/> is <c>file</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; set; }

    /// <summary>Child entries for directory nodes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SkillsEntryDto>? Children { get; set; }
}

/// <summary>
/// Response body for skills directory tree reads.
/// </summary>
public sealed class SkillsDirectoryResponse
{
    /// <summary>Response type discriminator.</summary>
    public string Type { get; set; } = "directory";

    /// <summary>Skills-relative path represented by the response root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Maximum traversal depth applied.</summary>
    public int DepthLimit { get; set; }

    /// <summary>Entries discovered under the response root.</summary>
    public IReadOnlyList<SkillsEntryDto> Entries { get; set; } = [];
}

/// <summary>
/// Request body for skills file writes.
/// </summary>
public sealed class SkillsWriteRequest
{
    /// <summary>Text content to write to the file.</summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Response body for skills file reads.
/// </summary>
public sealed class SkillsFileResponse
{
    /// <summary>Skills-relative file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>File type: <c>text</c> or <c>binary</c>.</summary>
    public string Type { get; set; } = "text";

    /// <summary>Decoded text content when <see cref="Type"/> is <c>text</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>File size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Content encoding when textual content is returned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Encoding { get; set; }

    /// <summary>Whether content was truncated to the API read cap.</summary>
    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; set; }
}

/// <summary>Complete scanner evidence submitted for an explicit operator acknowledgement.</summary>
public sealed record SkillSecurityAcknowledgementRequest
{
    /// <summary>Skill directory name under the shared skills root.</summary>
    public string Skill { get; init; } = string.Empty;
    /// <summary>Current scanner rule identifier.</summary>
    public string RuleId { get; init; } = string.Empty;
    /// <summary>Skill-relative file containing the finding.</summary>
    public string File { get; init; } = string.Empty;
    /// <summary>Current scanner severity.</summary>
    public string Severity { get; init; } = string.Empty;
    /// <summary>Stable identity emitted for the exact scanner result.</summary>
    public string FindingId { get; init; } = string.Empty;
    /// <summary>Exact lowercase or uppercase hexadecimal SHA-256 of the current file bytes.</summary>
    public string Sha256 { get; init; } = string.Empty;
    /// <summary>Explicit confirmation that the operator reviewed this exact evidence.</summary>
    public bool Confirmed { get; init; }
    /// <summary>Bounded, nonblank operator justification retained in configuration.</summary>
    public string Reason { get; init; } = string.Empty;
}
