using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Api.Controllers;

// Validation is separate from capture-time redaction: even allowlisted logger fields are untrusted.
internal static partial class StructuredDiagnosticPreview
{
    private static readonly string[] IdentifierFields =
    [
        "AgentId", "ConversationId", "SessionId", "RunId", "FindingId", "SkillName", "RuleId",
        "ScannerFindingId", "ToolCallId", "Channel", "CorrelationId", "DiagnosticSource",
        "DiagnosticCategory", "ParentAgentId", "ParentSessionId", "InstanceId"
    ];

    internal static StructuredDiagnosticOccurrence Occurrence(LogPatternOccurrence occurrence)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? Read(string key) => occurrence.Properties.GetValueOrDefault(key);
        foreach (var field in IdentifierFields) values[field] = Identifier(Read(field));
        values["RelativePath"] = RelativePath(Read("RelativePath"));
        values["FileSha256"] = Hex(Read("FileSha256"), 64);
        values["TraceId"] = NonzeroHex(Read("TraceId"), 32);
        values["SpanId"] = NonzeroHex(Read("SpanId"), 16);
        values["FindingLine"] = int.TryParse(Read("FindingLine"), NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line > 0 ? line : null;
        values["ScannerVersion"] = Version(Read("ScannerVersion"));
        values["FindingSeverity"] = Choice(Read("FindingSeverity"), "Info", "Warn", "Critical");
        values["SkillScope"] = Choice(Read("SkillScope"), "Workspace", "Agent", "Global", "Plugin");
        values["DiagnosticOriginKind"] = Choice(Read("DiagnosticOriginKind"), "DescriptorOnly", "Execution");
        values["DiagnosticTrigger"] = Choice(Read("DiagnosticTrigger"), "PromptConstruction", "ToolInvocation", "prompt-discovery");
        var status = Choice(Read("EvidenceStatus"), "complete", "incomplete");
        values["EvidenceStatus"] = status;
        // Never echo producer text. Preserve an explicit missing-evidence state with a closed vocabulary.
        values["MissingReason"] = status == "incomplete"
            ? Choice(Read("EvidenceReason"), "invalid-relative-path", "file-hash-unavailable", "invalid-line") ?? "unknown"
            : null;
        return new(occurrence.Timestamp, Identifier(occurrence.Category), occurrence.EventId,
            Identifier(occurrence.EventName), new ReadOnlyDictionary<string, object?>(values));
    }

    internal static string? Identifier(string? value) => value is { Length: > 0 and <= 128 } &&
        IdentifierRegex().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal) ? value : null;

    internal static string? Hex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit) ? value : null;

    private static string? NonzeroHex(string? value, int length) => Hex(value, length) is { } hex && hex.Any(character => character != '0') ? hex : null;

    private static string? Choice(string? value, params string[] choices) => choices.FirstOrDefault(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase));

    private static string? Version(string? value) => value is { Length: > 0 and <= 64 } &&
        (VersionRegex().IsMatch(value) || value == "skill-security-scanner/1") ? value : null;

    private static string? RelativePath(string? value)
    {
        // Canonical wire paths use '/' on every OS. Reject encoding instead of decoding an attacker path.
        if (value is not { Length: > 0 and <= 256 } || value.Contains('%') || value.Contains('\\')) return null;
        return value.Split('/').All(segment => segment is not ("" or "." or "..") &&
            segment.Length <= 128 && PathSegmentRegex().IsMatch(segment) &&
            !segment.EndsWith('.') && !segment.Contains("..", StringComparison.Ordinal)) ? value : null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.:-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegmentRegex();
}
