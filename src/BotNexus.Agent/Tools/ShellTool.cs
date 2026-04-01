using System.Diagnostics;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for executing shell commands in the agent workspace.</summary>
public sealed class ShellTool : ToolBase
{
    private readonly string _workspacePath;
    private readonly int _timeoutSeconds;

    public ShellTool(string workspacePath, int timeoutSeconds = 60, ILogger? logger = null)
        : base(logger)
    {
        _workspacePath = workspacePath;
        _timeoutSeconds = timeoutSeconds;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "shell",
        "Execute a shell command and return the output.",
        new Dictionary<string, ToolParameterSchema>
        {
            ["command"] = new("string", "Shell command to execute", Required: true),
            ["workdir"] = new("string", "Working directory (optional, defaults to workspace)", Required: false)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var command = GetRequiredString(arguments, "command");
        var workdir = GetOptionalString(arguments, "workdir", _workspacePath);

        Directory.CreateDirectory(workdir);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var processInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workdir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            var result = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(output)) result.AppendLine(output.TrimEnd());
            if (!string.IsNullOrEmpty(error)) result.AppendLine($"STDERR: {error.TrimEnd()}");
            result.AppendLine($"Exit code: {process.ExitCode}");

            return result.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Error: Command timed out after {_timeoutSeconds} seconds";
        }
    }
}
