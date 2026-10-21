using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp.Transport;

/// <summary>
/// Spawns an MCP server as a subprocess and communicates via stdin/stdout using JSON-RPC.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private static readonly HashSet<string> SensitiveKeyPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOKEN", "KEY", "SECRET", "PASSWORD", "CREDENTIAL", "AUTH", "API_KEY", "APIKEY"
    };

    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly IReadOnlyDictionary<string, string>? _env;
    private readonly string? _workingDirectory;
    private readonly bool _inheritEnv;

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
        string? workingDirectory = null,
        bool inheritEnv = true)
    {
        _command = command;
        _args = args ?? [];
        _env = env;
        _workingDirectory = workingDirectory;
        _inheritEnv = inheritEnv;
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> the MCP server subprocess is started with.
    /// <para>
    /// Extracted from <see cref="ConnectAsync"/> so the environment block the child actually receives
    /// can be asserted on without spawning a process (#2892). Environment overrides are applied through
    /// the shared <see cref="ProcessEnvironment.Merge"/> seam rather than an ad-hoc loop, so the
    /// platform casing rule is owned in one place instead of re-derived per spawn site.
    /// </para>
    /// </summary>
    internal ProcessStartInfo BuildStartInfo()
    {
        var launch = ResolveCommand(_command, _args);
        var fileName = launch.FileName;

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

        // Never loop over the argument list by hand here: a Windows .cmd/.bat shim resolves to a
        // RAW cmd.exe line that must not be re-escaped through ArgumentList (#3642).
        launch.ApplyArgumentsTo(startInfo);

        // When inheritEnv is false, clear inherited environment so the subprocess
        // only sees explicitly configured variables.
        if (!_inheritEnv)
        {
            startInfo.Environment.Clear();
        }

        if (_env is not null)
        {
            // Shared merge seam: an override must replace the inherited variable of the same
            // name under the platform casing rule, not sit alongside it (#2892). Placeholder
            // resolution rides along as the merge's value projection, not a second loop.
            ProcessEnvironment.Merge(startInfo.Environment, _env, valueTransform: ResolveEnvValue);
        }

        return startInfo;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var startInfo = BuildStartInfo();

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start MCP server process: {_command}");
            }
        }
        catch
        {
            // No OS process was started, so HasExited cannot confirm termination. Do not
            // publish this wrapper to strict cleanup or retain ownership of a failed launch.
            process.Dispose();
            throw;
        }

        // Retain every successfully started process before accessing its streams: failures
        // from here on still require strict, confirmed termination and retry ownership.
        _process = process;
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

        EnsureCanWrite();

        var json = JsonSerializer.Serialize(message, JsonContext.Default.JsonRpcRequest);
        try
        {
            await _writer!.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw CreateClosedPipeException(ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw CreateClosedPipeException(ex);
        }
    }

    /// <inheritdoc />
    public async Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureCanWrite();

        var json = JsonSerializer.Serialize(message, JsonContext.Default.JsonRpcNotification);
        try
        {
            await _writer!.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw CreateClosedPipeException(ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw CreateClosedPipeException(ex);
        }
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

    /// <summary>
    /// Default grace window allowed for the child to exit after its stdin is closed,
    /// before the process tree is force-killed.
    /// </summary>
    internal static readonly TimeSpan DefaultTerminationGrace = TimeSpan.FromSeconds(1);

    // The production defaults bind directly to Process; tests replace only these OS operations.
    internal (Func<bool> HasExited, Func<CancellationToken, Task> WaitForExit, Action Kill)? TerminationBoundary { get; set; }

    /// <inheritdoc />
    public Task DisconnectStrictAsync(CancellationToken ct = default) => DisconnectCoreAsync(true, ct);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default) => DisconnectCoreAsync(false, ct);

    private async Task DisconnectCoreAsync(bool strict, CancellationToken ct = default)
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
            catch (TimeoutException) when (!strict) { }
            catch (OperationCanceledException) when (!strict) { }
        }

        await TerminateCoreAsync(null, strict, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates the spawned MCP server process using a bounded graceful-then-force sequence:
    /// close stdin so a well-behaved server can exit on its own, wait at most
    /// <paramref name="graceWindow"/>, then <c>Kill(entireProcessTree: true)</c>.
    /// A child that ignores its stdin closing is therefore force-killed rather than leaked
    /// (issue #2723). This method never throws.
    /// </summary>
    /// <param name="graceWindow">Grace period allowed after stdin close. Defaults to one second.</param>
    public Task TerminateProcessAsync(TimeSpan? graceWindow = null) => TerminateCoreAsync(graceWindow, false, CancellationToken.None);

    private async Task TerminateCoreAsync(TimeSpan? graceWindow, bool strict, CancellationToken ct)
    {
        var process = _process;
        var boundary = TerminationBoundary;
        if (boundary is null && process is null) return;
        var hasExited = boundary?.HasExited ?? (() => process!.HasExited);
        var waitForExit = boundary?.WaitForExit ?? (token => process!.WaitForExitAsync(token));
        var kill = boundary?.Kill ?? (() => process!.Kill(entireProcessTree: true));
        try
        {
            ct.ThrowIfCancellationRequested();
            try { _writer?.Close(); } catch { } // still attempt kill if stdin close failed
            if (hasExited()) return;
            var grace = graceWindow ?? DefaultTerminationGrace;
            if (grace > TimeSpan.Zero)
            {
                using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                graceCts.CancelAfter(grace);
                try { await waitForExit(graceCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            if (hasExited()) return;
            try { kill(); }
            catch when (hasExited()) { } // benign exit racing Kill
            using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            killCts.CancelAfter(TimeSpan.FromSeconds(5));
            await waitForExit(killCts.Token).ConfigureAwait(false);
            if (!hasExited()) throw new IOException("MCP process termination was not confirmed.");
        }
        catch when (!strict) { } // existing best-effort contract
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

        await TerminateProcessAsync().ConfigureAwait(false);
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

    private void EnsureCanWrite()
    {
        if (_writer is null || _process is null)
            throw new InvalidOperationException("Transport is not connected.");

        if (_process.HasExited)
            throw new InvalidOperationException($"MCP server process exited with code {_process.ExitCode}; cannot send on closed stdin.");
    }

    private InvalidOperationException CreateClosedPipeException(Exception innerException)
    {
        if (_process is not null && _process.HasExited)
        {
            return new InvalidOperationException(
                $"MCP server process exited with code {_process.ExitCode}; cannot send on closed stdin.",
                innerException);
        }

        return new InvalidOperationException(
            "MCP server stdin pipe is closed; unable to send message.",
            innerException);
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
    /// Returns <c>true</c> if the environment variable key looks like it holds
    /// a sensitive value (token, key, secret, password, etc.).
    /// </summary>
    internal static bool IsSensitiveEnvKey(string key)
    {
        foreach (var pattern in SensitiveKeyPatterns)
        {
            if (key.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a log-safe representation of an environment variable value.
    /// Sensitive values are masked as <c>***</c>.
    /// </summary>
    internal static string MaskValue(string key, string value)
    {
        if (IsSensitiveEnvKey(key))
            return "***";

        return value;
    }

    /// <summary>
    /// Resolves command and arguments, handling Windows .cmd/.bat shims.
    /// </summary>
    /// <remarks>
    /// Delegates to the shared <see cref="WindowsShimLaunch"/> seam. This method used to carry a
    /// private copy of the PATH probe and the cmd.exe quoting rules; that copy assembled the
    /// <c>/d /s /c</c> payload as a fourth <see cref="ProcessStartInfo.ArgumentList"/> entry, which
    /// .NET then CRT-escaped into <c>\"</c> - so every npx-launched stdio MCP server on Windows
    /// failed to start (#3642). One seam, so the two copies cannot drift apart again.
    /// </remarks>
    internal static ProcessLaunch ResolveCommand(string command, IReadOnlyList<string> args)
        => WindowsShimLaunch.Resolve(command, args);
}
