using System.Net;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public sealed record SkillSecurityFindingsDto(
    [property: JsonPropertyName("findings")] IReadOnlyList<SkillSecurityFindingDto> Findings,
    [property: JsonPropertyName("isTruncated")] bool IsTruncated);

public sealed record SkillSecurityFindingDto(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("relativePath")] string? RelativePath,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("scannerVersion")] string ScannerVersion,
    [property: JsonPropertyName("scannerFindingId")] string ScannerFindingId,
    [property: JsonPropertyName("fileSha256")] string? FileSha256,
    [property: JsonPropertyName("revisionId")] string? RevisionId,
    [property: JsonPropertyName("isComplete")] bool IsComplete,
    [property: JsonPropertyName("missingReason")] string? MissingReason);

public sealed record SkillSecurityAcknowledgementDto(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("findingId")] string FindingId,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record SkillSecurityAcknowledgementResult(HttpStatusCode StatusCode, string? ErrorText)
{
    public bool IsSuccess => StatusCode is HttpStatusCode.OK or HttpStatusCode.Created;
}
