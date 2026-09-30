using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Thread-safe bounded in-memory capture for Warning+ structured log diagnostics.
/// </summary>
public sealed partial class LogDiagnosticsRingBuffer
{
    /// <summary>Maximum retained length of a structured property value before an ellipsis.</summary>
    public const int MaxPropertyValueLength = 256;

    /// <summary>Maximum retained template length before an ellipsis.</summary>
    public const int MaxTemplateLength = 500;

    internal const int MaxRenderedMessageLength = 500;
    internal const int MaxCategoryLength = 256;
    internal const int MaxEventNameLength = 128;
    private const int MaxDistinctValuesPerDimension = 1000;
    private const string RedactedValue = "[REDACTED]";

    /// <summary>Sentinel retained when a log entry has no structured message-template placeholders.</summary>
    public const string RedactedUnstructuredValue = "[REDACTED_UNSTRUCTURED]";

    /// <summary>Sentinel retained instead of truncating an oversized input at a potentially sensitive boundary.</summary>
    public const string RedactedOversizedValue = "[REDACTED_OVERSIZED]";
    private static readonly IReadOnlyDictionary<string, string> AllowedPropertyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AgentId"] = "AgentId",
            ["ConversationId"] = "ConversationId",
            ["SessionId"] = "SessionId",
            ["RunId"] = "RunId",
            ["FindingId"] = "FindingId",
            ["ToolCallId"] = "ToolCallId",
            ["Channel"] = "Channel",
            ["TraceId"] = "TraceId",
            ["SpanId"] = "SpanId",
            ["CorrelationId"] = "CorrelationId",
            ["DiagnosticSource"] = "DiagnosticSource",
            ["DiagnosticCategory"] = "DiagnosticCategory",
            ["DiagnosticOriginKind"] = "DiagnosticOriginKind",
            ["DiagnosticTrigger"] = "DiagnosticTrigger",
            ["ParentAgentId"] = "ParentAgentId",
            ["ParentSessionId"] = "ParentSessionId",
            ["InstanceId"] = "InstanceId"
        };

    private readonly object _gate = new();
    private readonly Dictionary<string, LogPatternEntry> _patterns = new(StringComparer.Ordinal);
    private readonly int _maxPatterns;
    private readonly int _maxRecentOccurrencesPerPattern;

    /// <summary>Creates a bounded diagnostic capture buffer.</summary>
    /// <param name="maxPatterns">Maximum number of aggregate patterns retained.</param>
    /// <param name="maxRecentOccurrencesPerPattern">Maximum recent occurrences retained per aggregate.</param>
    public LogDiagnosticsRingBuffer(int maxPatterns = 1000, int maxRecentOccurrencesPerPattern = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPatterns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecentOccurrencesPerPattern, 1);
        _maxPatterns = maxPatterns;
        _maxRecentOccurrencesPerPattern = maxRecentOccurrencesPerPattern;
    }

    /// <summary>Records an unstructured compatibility observation.</summary>
    public void Record(LogLevel level, string? messageTemplate, string renderedMessage) =>
        Record(level, string.Empty, default, messageTemplate, renderedMessage,
            new Dictionary<string, string?>(StringComparer.Ordinal));

    /// <summary>
    /// Records a structured observation after independently sanitising, bounding, and copying every retained value.
    /// </summary>
    public void Record(
        LogLevel level,
        string category,
        EventId eventId,
        string? messageTemplate,
        string renderedMessage,
        IReadOnlyDictionary<string, string?> properties)
    {
        if (level < LogLevel.Warning)
            return;

        var capturedProperties = CaptureProperties(properties);
        var boundedCategory = CaptureString(category, MaxCategoryLength);
        var templateIdentity = CaptureTemplateIdentity(messageTemplate);
        var boundedTemplate = RetainTemplate(templateIdentity);
        var boundedMessage = boundedTemplate == RedactedUnstructuredValue
            ? RedactedUnstructuredValue
            : CaptureString(renderedMessage, MaxRenderedMessageLength);
        var boundedEventName = eventId.Name is null ? null : CaptureString(eventId.Name, MaxEventNameLength);
        capturedProperties.TryGetValue("FindingId", out var findingId);
        var fingerprint = ComputeFingerprint(
            templateIdentity, level, boundedCategory, eventId.Id, boundedEventName, findingId);
        var timestamp = DateTimeOffset.UtcNow;
        var occurrence = new LogPatternOccurrence
        {
            Timestamp = timestamp,
            Category = boundedCategory,
            EventId = eventId.Id,
            EventName = boundedEventName,
            RenderedMessage = boundedMessage,
            Properties = capturedProperties
        };

        lock (_gate)
        {
            if (_patterns.TryGetValue(fingerprint, out var existing))
            {
                existing.AddOccurrence(occurrence, _maxRecentOccurrencesPerPattern, MaxDistinctValuesPerDimension);
            }
            else
            {
                var entry = new LogPatternEntry
                {
                    Fingerprint = fingerprint,
                    Template = boundedTemplate,
                    Severity = level,
                    FirstSeen = timestamp,
                    InitialLastSeen = timestamp,
                    SampleMessage = boundedMessage
                };
                entry.AddInitialOccurrence(occurrence, MaxDistinctValuesPerDimension);
                _patterns.Add(fingerprint, entry);
            }

            if (_patterns.Count > _maxPatterns)
            {
                var oldest = _patterns.Values.OrderBy(pattern => pattern.LastSeen).First();
                _patterns.Remove(oldest.Fingerprint);
            }
        }
    }

    /// <summary>Returns aggregates observed within a time window, newest first.</summary>
    public IReadOnlyList<LogPatternEntry> GetPatterns(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        lock (_gate)
        {
            return _patterns.Values.Where(pattern => pattern.LastSeen >= cutoff)
                .OrderByDescending(pattern => pattern.LastSeen).ToArray();
        }
    }

    /// <summary>Returns the current aggregate count.</summary>
    public int PatternCount { get { lock (_gate) return _patterns.Count; } }

    /// <summary>Removes all retained aggregates and occurrences.</summary>
    public void Clear()
    {
        lock (_gate)
            _patterns.Clear();
    }

    internal static bool TryGetCanonicalPropertyName(string name, out string canonicalName) =>
        AllowedPropertyNames.TryGetValue(name, out canonicalName!);

    internal static string CaptureValue(string value, int maxLength) => CaptureString(value, maxLength);

    internal static string CaptureTemplate(string? template) =>
        RetainTemplate(CaptureTemplateIdentity(template));

    private static string CaptureTemplateIdentity(string? template) =>
        template is null ? RedactedUnstructuredValue : CaptureString(template, MaxTemplateLength);

    private static string RetainTemplate(string templateIdentity) =>
        templateIdentity == RedactedOversizedValue || HasPlaceholder(templateIdentity)
            ? templateIdentity
            : RedactedUnstructuredValue;

    internal static string ComputeFingerprint(string template, LogLevel level) =>
        ComputeFingerprint(template, level, string.Empty, 0, null, null);

    private static string ComputeFingerprint(
        string template,
        LogLevel level,
        string category,
        int eventId,
        string? eventName,
        string? findingId)
    {
        var input = $"{level}:{category}:{eventId}:{eventName}:{template}:{findingId}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..16];
    }

    private static IReadOnlyDictionary<string, string?> CaptureProperties(
        IReadOnlyDictionary<string, string?> properties)
    {
        var captured = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            if (!TryGetCanonicalPropertyName(pair.Key, out var canonicalName))
                continue;

            captured[canonicalName] = pair.Value is null
                ? null
                : CaptureString(pair.Value, MaxPropertyValueLength);
        }

        return new ReadOnlyDictionary<string, string?>(captured);
    }

    private static string CaptureString(string value, int maxLength) =>
        Sanitise(PreCap(value, maxLength));

    private static string PreCap(string value, int maxLength) =>
        value.Length <= maxLength ? value : RedactedOversizedValue;

    private static string Sanitise(string boundedValue)
    {
        var sanitised = boundedValue.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        sanitised = EmbeddedUrlRegex().Replace(sanitised, static match =>
            Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ')', ']'), UriKind.Absolute, out var uri) &&
            (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
                ? RedactedValue + match.Value[match.Value.TrimEnd('.', ',', ';', ')', ']').Length..]
                : match.Value);
        return QuerySecretRegex().Replace(sanitised, static match => $"{match.Groups[1].Value}={RedactedValue}");
    }

    private static bool HasPlaceholder(string template)
    {
        var openBrace = template.IndexOf('{');
        return openBrace >= 0 && template.IndexOf('}', openBrace + 1) > openBrace + 1;
    }


    [GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUrlRegex();

    [GeneratedRegex(@"(?i)\b(token|access_token|api[_-]?key|sig|signature|secret|password|passwd|credential)=([^&\s;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();
}
