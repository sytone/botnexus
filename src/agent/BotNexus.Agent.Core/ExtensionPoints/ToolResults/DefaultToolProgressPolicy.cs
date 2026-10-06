using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Core classification for known, safely repeatable non-progress result shapes.</summary>
internal static class DefaultToolProgressPolicy
{
    internal static Task<ToolProgressDecision?> EvaluateAsync(
        ToolProgressContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = context.ToolCall;
        var result = context.ToolResult;
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
                var kind = missing ? "edit-found-zero" : "edit-no-change";
                var guidance = missing
                    ? "[Tool progress guard] Repeated edit attempts found no matching target. Re-read the current file, then change the editing mechanism (for example, use a smaller unique anchor or a different patch method). Do not retry by only varying the same missing anchor."
                    : null;
                return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                    Hash("edit\n" + editTarget),
                    "edit-non-progress",
                    kind,
                    guidance));
            }
        }

        if (result.IsError)
            return Task.FromResult<ToolProgressDecision?>(null);

        if (name is "get_current_time" or "get_datetime" or "clock")
        {
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                Hash(name + "\n" + CanonicalArguments(call.Arguments)),
                "clock-check",
                "clock-check"));
        }

        if (name is "shell" or "exec" && IsReadOnlyGitStatus(call.Arguments, out var command))
        {
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                Hash("git-status\n" + command),
                Hash(text),
                "unchanged-status"));
        }

        if (name == "read" && TryGetTarget(call.Arguments, out var target))
        {
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                Hash("read\n" + target + "\n" + CanonicalArguments(call.Arguments)),
                Hash(text),
                "unchanged-read"));
        }

        return Task.FromResult<ToolProgressDecision?>(null);
    }

    private static bool IsReadOnlyGitStatus(IReadOnlyDictionary<string, object?> arguments, out string command)
    {
        command = string.Empty;
        var pair = arguments.FirstOrDefault(item => item.Key.Equals("command", StringComparison.OrdinalIgnoreCase));
        var value = pair.Value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : pair.Value as string;
        if (value is null)
            return false;

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
