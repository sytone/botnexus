using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Bounded run-local guard for known non-progress tool outcomes. It observes completed results;
/// it never blocks, retries, or assumes that an arbitrary successful write can be replayed.
/// Only hashes of targets/results are retained and diagnostic text contains no tool payloads.
/// </summary>
internal sealed class ToolNonProgressGuard
{
    internal const int WarningThreshold = 3;
    internal const int StopThreshold = 6;

    private readonly Dictionary<string, string> _observed = new(StringComparer.Ordinal);
    private int _count;
    private bool _warned;

    internal ToolNonProgressObservation Observe(
        IReadOnlyList<ToolCallContent> calls,
        IReadOnlyList<ToolResultAgentMessage> results)
    {
        var byId = calls.ToDictionary(call => call.Id, StringComparer.Ordinal);
        var warning = false;
        string? kind = null;
        foreach (var result in results)
        {
            if (!byId.TryGetValue(result.ToolCallId, out var call)
                || !TryClassify(call, result, out var candidateScope, out var candidateResult, out var candidateKind))
            {
                Reset();
                continue;
            }

            if (_observed.TryGetValue(candidateScope, out var priorResult) && priorResult != candidateResult)
                Reset(); // A changed result at the same target is new evidence.
            if (!_observed.ContainsKey(candidateScope) && _observed.Count >= 2)
                Reset(); // Different targets/operations may be genuine exploration, not a spin.
            _observed[candidateScope] = candidateResult;
            _count++;

            kind = candidateKind;
            if (!_warned && _count >= WarningThreshold)
            {
                _warned = true;
                warning = true;
            }
            // Finish processing the entire executed batch. A sibling result is never discarded.
        }

        return new ToolNonProgressObservation(_count, kind, warning, _count >= StopThreshold);
    }

    internal void Reset()
    {
        _observed.Clear();
        _count = 0;
        _warned = false;
    }

    private static bool TryClassify(
        ToolCallContent call,
        ToolResultAgentMessage result,
        out string scope,
        out string resultIdentity,
        out string kind)
    {
        scope = resultIdentity = kind = string.Empty;
        var text = string.Join("\n", result.Result.Content
            .Where(content => content.Type == AgentToolContentType.Text)
            .Select(content => content.Value));
        var name = call.Name.ToLowerInvariant();

        if (name == "edit" && TryGetTarget(call.Arguments, out var editTarget))
        {
            var missing = result.IsError && (text.Contains("found 0", StringComparison.OrdinalIgnoreCase)
                || text.Contains("found zero", StringComparison.OrdinalIgnoreCase)
                || text.Contains("no matches found", StringComparison.OrdinalIgnoreCase));
            var noChange = !result.IsError && (text.Contains("no changes needed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("no change needed", StringComparison.OrdinalIgnoreCase));
            if (missing || noChange)
            {
                // A different anchor or a found-0/no-change alternation is still the same
                // failed editing strategy against the same file. No call ID, anchor, or result
                // text enters the identity or the diagnostic.
                scope = Hash("edit\n" + editTarget);
                resultIdentity = "edit-non-progress";
                kind = missing ? "edit-found-zero" : "edit-no-change";
                return true;
            }
        }

        if (result.IsError)
            return false;

        if (name is "get_current_time" or "get_datetime" or "clock")
        {
            // Wall-clock movement is not work progress. A changing timestamp must not
            // let a clock-check spin indefinitely.
            scope = Hash(name + "\n" + CanonicalArguments(call.Arguments));
            resultIdentity = "clock-check";
            kind = "clock-check";
            return true;
        }

        if (name is "shell" or "exec" && IsReadOnlyGitStatus(call.Arguments, out var command))
        {
            scope = Hash("git-status\n" + command);
            resultIdentity = Hash(text);
            kind = "unchanged-status";
            return true;
        }

        if (name == "read" && TryGetTarget(call.Arguments, out var target))
        {
            scope = Hash("read\n" + target + "\n" + CanonicalArguments(call.Arguments));
            resultIdentity = Hash(text);
            kind = "unchanged-read";
            return true;
        }

        return false;
    }

    private static bool IsReadOnlyGitStatus(IReadOnlyDictionary<string, object?> arguments, out string command)
    {
        command = string.Empty;
        var pair = arguments.FirstOrDefault(item => item.Key.Equals("command", StringComparison.OrdinalIgnoreCase));
        var value = pair.Value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : pair.Value as string;
        if (value is null)
            return false;
        // Recognize only a literal read-only status/diff chain. An arbitrary shell success is
        // never evidence of a harmless repeat: it could have completed a side effect.
        var parts = value.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(part => part is not ("git status --short" or "git diff --stat")))
            return false;
        command = value;
        return true;
    }

    private static bool TryGetTarget(IReadOnlyDictionary<string, object?> arguments, out string target)
    {
        foreach (var key in new[] { "path", "file", "filename", "filepath" })
        {
            var pair = arguments.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (pair.Key is not null && pair.Value is not null)
            {
                target = SerializeValue(pair.Value);
                return true;
            }
        }
        target = string.Empty;
        return false;
    }

    private static string CanonicalArguments(IReadOnlyDictionary<string, object?> arguments)
        => string.Join("\n", arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + SerializeValue(pair.Value)));

    private static string SerializeValue(object? value)
        => value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed record ToolNonProgressObservation(
    int ConsecutiveCount,
    string? Kind,
    bool WarningReady,
    bool ShouldStop);
