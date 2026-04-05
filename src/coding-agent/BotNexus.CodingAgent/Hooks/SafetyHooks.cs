using BotNexus.AgentCore.Hooks;
using BotNexus.CodingAgent.Utils;

namespace BotNexus.CodingAgent.Hooks;

public sealed class SafetyHooks
{
    private static readonly string[] DefaultBlockedCommands =
    [
        "rm -rf /",
        "format",
        "del /s /q"
    ];

    private const int LargeWriteThresholdBytes = 1024 * 1024;

    public Task<BeforeToolCallResult?> ValidateAsync(
        BeforeToolCallContext context,
        CodingAgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        if (IsWriteTool(context.ToolCallRequest.Name))
        {
            var pathResult = ValidatePath(context, config);
            if (pathResult is not null)
            {
                return Task.FromResult<BeforeToolCallResult?>(pathResult);
            }

            EmitLargeWriteWarning(context);
        }

        if (IsShellTool(context.ToolCallRequest.Name))
        {
            var shellResult = ValidateShellCommand(context, config);
            if (shellResult is not null)
            {
                return Task.FromResult<BeforeToolCallResult?>(shellResult);
            }
        }

        return Task.FromResult<BeforeToolCallResult?>(null);
    }

    private static bool IsWriteTool(string toolName)
    {
        return toolName.Equals("write", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("edit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellTool(string toolName)
    {
        return toolName.Equals("bash", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("shell", StringComparison.OrdinalIgnoreCase);
    }

    private static BeforeToolCallResult? ValidatePath(BeforeToolCallContext context, CodingAgentConfig config)
    {
        var rawPath = ReadString(context.ValidatedArgs, "path");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            var resolved = PathUtils.ResolvePath(rawPath, config.WorkingDirectory());
            if (IsBlockedPath(resolved, config))
            {
                return new BeforeToolCallResult(true, $"Blocked path: '{rawPath}'.");
            }
        }
        catch (Exception ex)
        {
            return new BeforeToolCallResult(true, $"Unsafe path '{rawPath}': {ex.Message}");
        }

        return null;
    }

    private static BeforeToolCallResult? ValidateShellCommand(BeforeToolCallContext context, CodingAgentConfig config)
    {
        var command = ReadString(context.ValidatedArgs, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var commandLower = command.ToLowerInvariant();
        if (config.AllowedCommands.Count > 0
            && !config.AllowedCommands.Any(prefix =>
                commandLower.StartsWith(prefix.ToLowerInvariant(), StringComparison.Ordinal)))
        {
            return new BeforeToolCallResult(true, "Command is not in the allowed command list.");
        }

        foreach (var blocked in DefaultBlockedCommands)
        {
            if (commandLower.Contains(blocked, StringComparison.Ordinal))
            {
                return new BeforeToolCallResult(true, $"Blocked dangerous command pattern: '{blocked}'.");
            }
        }

        return null;
    }

    private static bool IsBlockedPath(string resolvedPath, CodingAgentConfig config)
    {
        foreach (var blockedPath in config.BlockedPaths)
        {
            if (string.IsNullOrWhiteSpace(blockedPath))
            {
                continue;
            }

            var resolvedBlockedPath = Path.GetFullPath(
                Path.IsPathRooted(blockedPath)
                    ? blockedPath
                    : Path.Combine(config.WorkingDirectory(), blockedPath));

            if (resolvedPath.StartsWith(resolvedBlockedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitLargeWriteWarning(BeforeToolCallContext context)
    {
        var payload = ReadString(context.ValidatedArgs, "content")
                      ?? ReadString(context.ValidatedArgs, "new_str")
                      ?? ReadString(context.ValidatedArgs, "newText")
                      ?? ReadFirstEditNewText(context.ValidatedArgs);
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (bytes > LargeWriteThresholdBytes)
        {
            Console.WriteLine($"[safety] Warning: large write payload detected ({bytes} bytes).");
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        return args.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static string? ReadFirstEditNewText(IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("edits", out var edits) || edits is not IEnumerable<object?> list)
        {
            return null;
        }

        foreach (var entry in list)
        {
            if (entry is IReadOnlyDictionary<string, object?> dictionary)
            {
                return ReadString(dictionary, "newText");
            }
        }

        return null;
    }
}

internal static class CodingAgentConfigSafetyExtensions
{
    public static string WorkingDirectory(this CodingAgentConfig config)
    {
        return Path.GetDirectoryName(config.ConfigDirectory)
               ?? config.ConfigDirectory;
    }
}
