using System.Diagnostics;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tools;

public sealed class FileWatcherTool(IOptions<FileWatcherToolOptions> options, IPathValidator? pathValidator = null) : IAgentTool
{
    private readonly FileWatcherToolOptions _options = options?.Value ?? new FileWatcherToolOptions();
    private readonly IPathValidator? _pathValidator = pathValidator;

    public string Name => "watch_file";
    public string Label => "Watch File";

    public Tool Definition => new(
        Name,
        "Watch a file for changes and resume when it is modified, created, or deleted. Use to react to file saves or wait for build output.",
        JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Path to the file to watch (absolute or relative to workspace)."
                },
                "timeout": {
                  "type": "integer",
                  "description": "Maximum seconds to wait before returning a timeout result (default {{_options.DefaultTimeoutSeconds}}).",
                  "minimum": 1,
                  "maximum": {{Math.Max(1, _options.MaxTimeoutSeconds)}}
                },
                "event": {
                  "type": "string",
                  "enum": ["modified", "created", "deleted", "any"],
                  "description": "What filesystem event to watch for. Default: modified."
                }
              },
              "required": ["path"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ReadString(arguments, "path")
            ?? throw new ArgumentException("Missing required argument: path.");

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Argument 'path' must not be empty.");

        // Validate the event type if provided
        var eventType = ReadString(arguments, "event") ?? "modified";
        if (!eventType.Equals("modified", StringComparison.OrdinalIgnoreCase) &&
            !eventType.Equals("created", StringComparison.OrdinalIgnoreCase) &&
            !eventType.Equals("deleted", StringComparison.OrdinalIgnoreCase) &&
            !eventType.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported event type '{eventType}'. Must be 'modified', 'created', 'deleted', or 'any'.");
        }

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var rawPath = ReadString(arguments, "path")!;
        var eventType = (ReadString(arguments, "event") ?? "modified").ToLowerInvariant();
        var requestedTimeout = ReadOptionalInt(arguments, "timeout", _options.DefaultTimeoutSeconds);
        var maxTimeout = Math.Max(1, _options.MaxTimeoutSeconds);
        var timeout = Math.Clamp(requestedTimeout, 1, maxTimeout);

        var fullPath = _pathValidator?.ValidateAndResolve(rawPath, FileAccessMode.Read);
        if (_pathValidator is not null && fullPath is null)
        {
            return TextResult($"Access denied: path '{rawPath}' is not permitted for read");
        }

        fullPath ??= Path.GetFullPath(rawPath);
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);

        if (string.IsNullOrEmpty(directory))
            return TextResult($"Error: cannot determine directory for path '{rawPath}'.");

        if (!Directory.Exists(directory))
            return TextResult($"Error: directory '{directory}' does not exist.");

        // For "modified" and "deleted", the file should already exist
        if (eventType is "modified" or "deleted")
        {
            if (!File.Exists(fullPath))
                return TextResult($"Error: file '{fullPath}' does not exist. Use event type 'created' or 'any' to watch for file creation.");
        }

        onUpdate?.Invoke(TextResult($"Watching '{fullPath}' for {eventType} event (timeout: {timeout}s)..."));

        var stopwatch = Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Timer? debounceTimer = null;
        var debounceMs = Math.Max(50, _options.DebounceMilliseconds);

        try
        {
            using var watcher = new FileSystemWatcher(directory, fileName);
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime;

            void HandleEvent(string detectedEvent)
            {
                var previousTimer = Interlocked.Exchange(ref debounceTimer, null);
                previousTimer?.Dispose();

                var newTimer = new Timer(_ => tcs.TrySetResult(detectedEvent), null, debounceMs, Timeout.Infinite);
                var replaced = Interlocked.CompareExchange(ref debounceTimer, newTimer, null);
                if (replaced is not null)
                {
                    // Another thread set a timer concurrently — dispose ours
                    newTimer.Dispose();
                }
            }

            if (eventType is "modified" or "any")
            {
                watcher.Changed += (_, _) => HandleEvent("modified");
            }

            if (eventType is "created" or "any")
            {
                watcher.Created += (_, _) => HandleEvent("created");
            }

            if (eventType is "deleted" or "any")
            {
                watcher.Deleted += (_, _) => HandleEvent("deleted");
            }

            if (eventType is "any")
            {
                watcher.Renamed += (_, _) => HandleEvent("renamed");
            }

            watcher.EnableRaisingEvents = true;

            // Link caller cancellation with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

            using var cancellationReg = timeoutCts.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetResult("cancelled");
                else
                    tcs.TrySetResult("timeout");
            });

            var result = await tcs.Task.ConfigureAwait(false);
            var elapsed = (int)stopwatch.Elapsed.TotalSeconds;

            return result switch
            {
                "timeout" => TextResult($"Timeout after {timeout} seconds — no change detected on '{fullPath}'."),
                "cancelled" => TextResult($"Watch cancelled after {elapsed} seconds. No change detected."),
                _ => TextResult($"File {result}: '{fullPath}' (after {elapsed} seconds).")
            };
        }
        finally
        {
            var finalTimer = Interlocked.Exchange(ref debounceTimer, null);
            finalTimer?.Dispose();
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static int ReadOptionalInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            int number => number,
            long l => (int)l,
            double d => (int)d,
            string text when int.TryParse(text, out var parsed) => parsed,
            string text when double.TryParse(text, out var d) => (int)d,
            _ => defaultValue
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
