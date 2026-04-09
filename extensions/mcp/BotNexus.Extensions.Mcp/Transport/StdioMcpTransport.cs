using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp.Transport;

/// <summary>
/// Spawns an MCP server as a subprocess and communicates via stdin/stdout using JSON-RPC.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly IReadOnlyDictionary<string, string>? _env;
    private readonly string? _workingDirectory;

    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly ConcurrentQueue<JsonRpcResponse> _responseQueue = new();
    private readonly SemaphoreSlim _responseSemaphore = new(0);
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private bool _disposed;

    public StdioMcpTransport(
        string command,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null)
    {
        _command = command;
        _args = args ?? [];
        _env = env;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (fileName, processArgs) = ResolveCommand(_command, _args);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(_workingDirectory))
        {
            startInfo.WorkingDirectory = _workingDirectory;
        }

        foreach (var arg in processArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (_env is not null)
        {
            foreach (var (key, value) in _env)
            {
                startInfo.Environment[key] = ResolveEnvValue(value);
            }
        }

        _process = new Process { StartInfo = startInfo };
        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to start MCP server process: {_command}");
        }

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;

        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonRpcRequest message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writer is null)
            throw new InvalidOperationException("Transport is not connected.");

        var json = JsonSerializer.Serialize(message, JsonContext.Default.JsonRpcRequest);
        await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writer is null)
            throw new InvalidOperationException("Transport is not connected.");

        var json = JsonSerializer.Serialize(message, JsonContext.Default.JsonRpcNotification);
        await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _responseSemaphore.WaitAsync(ct).ConfigureAwait(false);

        if (_responseQueue.TryDequeue(out var response))
        {
            return response;
        }

        throw new InvalidOperationException("Response queue was signaled but no message available.");
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_readLoopCts is not null)
        {
            await _readLoopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_readLoopTask is not null)
        {
            try
            {
                await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }

        TryKillProcess();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_readLoopCts is not null)
        {
            await _readLoopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_readLoopTask is not null)
        {
            try
            {
                await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { }
        }

        TryKillProcess();
        _readLoopCts?.Dispose();
        _process?.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_reader is null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // process exited

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var response = JsonSerializer.Deserialize(line, JsonContext.Default.JsonRpcResponse);
                    if (response is not null)
                    {
                        _responseQueue.Enqueue(response);
                        _responseSemaphore.Release();
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines (e.g. server stderr leaking to stdout)
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void TryKillProcess()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Resolves environment variable substitution patterns like <c>${env:VAR_NAME}</c>.
    /// </summary>
    internal static string ResolveEnvValue(string value)
    {
        if (!value.StartsWith("${env:", StringComparison.Ordinal) || !value.EndsWith('}'))
            return value;

        var inner = value.AsSpan(6, value.Length - 7); // strip ${env: and }
        var defaultSep = inner.IndexOf(":-", StringComparison.Ordinal);

        if (defaultSep >= 0)
        {
            var varName = inner[..defaultSep].ToString();
            var defaultValue = inner[(defaultSep + 2)..].ToString();
            return Environment.GetEnvironmentVariable(varName) ?? defaultValue;
        }

        return Environment.GetEnvironmentVariable(inner.ToString()) ?? string.Empty;
    }

    /// <summary>
    /// Resolves command and arguments, handling Windows .cmd/.bat shims.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Args) ResolveCommand(
        string command, IReadOnlyList<string> args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (command, args);

        var resolved = ResolveWindowsExecutable(command);
        if (resolved is not null && IsWindowsBatchFile(resolved))
        {
            var cmdArgs = new List<string> { "/d", "/s", "/c", BuildCmdCommandLine(resolved, args) };
            return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", cmdArgs);
        }

        return (resolved ?? command, args);
    }

    private static string? ResolveWindowsExecutable(string command)
    {
        if (Path.HasExtension(command))
            return command;

        string[] extensions = [".exe", ".cmd", ".bat"];
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var ext in extensions)
        {
            var candidate = command + ext;
            foreach (var dir in pathDirs)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    private static bool IsWindowsBatchFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cmd" or ".bat";
    }

    private static string BuildCmdCommandLine(string command, IReadOnlyList<string> args)
    {
        var parts = new List<string> { QuoteForCmd(command) };
        foreach (var arg in args)
            parts.Add(QuoteForCmd(arg));
        return string.Join(' ', parts);
    }

    private static string QuoteForCmd(string arg)
    {
        if (!arg.Contains(' ') && !arg.Contains('"'))
            return arg;
        return $"\"{arg.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
