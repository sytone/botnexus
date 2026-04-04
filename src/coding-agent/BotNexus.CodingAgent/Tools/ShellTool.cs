using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

/// <summary>
/// Executes shell commands and returns normalized process output.
/// </summary>
/// <remarks>
/// <para>
/// Platform contract: Windows executes through PowerShell, non-Windows executes through bash.
/// This keeps command semantics predictable for repository automation scenarios.
/// </para>
/// <para>
/// Output is capped at 10,000 characters to protect downstream token budgets and prevent runaway
/// responses from large command streams.
/// </para>
/// </remarks>
public sealed class ShellTool : IAgentTool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxOutputCharacters = 10000;

    /// <inheritdoc />
    public string Name => "shell";

    /// <inheritdoc />
    public string Label => "Run Shell Command";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Run a shell command with timeout and captured stdout/stderr.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "Shell command text to execute."
                },
                "timeout": {
                  "type": "integer",
                  "description": "Optional timeout in seconds. Defaults to 120."
                }
              },
              "required": ["command"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var command = ReadRequiredString(arguments, "command");
        var timeoutSeconds = DefaultTimeoutSeconds;

        if (arguments.TryGetValue("timeout", out var rawTimeout) && rawTimeout is not null)
        {
            timeoutSeconds = ReadInt(rawTimeout, "timeout");
            if (timeoutSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "timeout must be >= 1 second.");
            }
        }

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["timeout"] = timeoutSeconds
        };

        return Task.FromResult(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var command = arguments["command"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: command.");
        var timeoutSeconds = arguments.TryGetValue("timeout", out var timeoutObj) && timeoutObj is int timeout
            ? timeout
            : DefaultTimeoutSeconds;

        var (fileName, shellArgs) = BuildShellInvocation(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = shellArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start shell process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Command timed out after {timeoutSeconds} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        var outputBuilder = new StringBuilder()
            .AppendLine($"Exit Code: {process.ExitCode}")
            .AppendLine("--- STDOUT ---")
            .AppendLine(stdout)
            .AppendLine("--- STDERR ---")
            .Append(stderr);

        var output = outputBuilder.ToString();
        if (output.Length > MaxOutputCharacters)
        {
            output = $"{output[..MaxOutputCharacters]}\n[warning] Output truncated at {MaxOutputCharacters} characters.";
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, output)]);
    }

    private static (string FileName, string Args) BuildShellInvocation(string command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var escaped = command.Replace("\"", "`\"", StringComparison.Ordinal);
            return ("powershell", $"-NoLogo -NoProfile -NonInteractive -Command \"{escaped}\"");
        }

        var bashEscaped = command.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        return ("/bin/bash", $"-lc '{bashEscaped}'");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort process cleanup; propagate original execution exception.
        }
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()
                ?? throw new ArgumentException($"Argument '{key}' cannot be null."),
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? throw new ArgumentException($"Argument '{key}' is invalid.")
        };
    }

    private static int ReadInt(object value, string key)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
    }
}
