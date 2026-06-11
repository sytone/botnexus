using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Implements <see cref="IQmdBackend"/> by shelling out to the <c>qmd</c> CLI binary.
/// Each method maps to a specific <c>qmd</c> subcommand with <c>--json</c> output.
/// </summary>
public sealed class QmdCliBackend : IQmdBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _qmdPath;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new QMD CLI backend.
    /// </summary>
    /// <param name="qmdPath">Path to the qmd binary (or just "qmd" for PATH resolution).</param>
    /// <param name="timeout">Maximum time to wait for a CLI call.</param>
    /// <param name="logger">Optional logger.</param>
    public QmdCliBackend(string? qmdPath = null, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _qmdPath = string.IsNullOrWhiteSpace(qmdPath) ? "qmd" : qmdPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct = default)
    {
        var command = mode switch
        {
            QmdSearchMode.Keyword => "search",
            QmdSearchMode.Semantic => "vsearch",
            QmdSearchMode.Hybrid => "query",
            _ => "query"
        };

        var args = new List<string> { command, query };
        if (!string.IsNullOrEmpty(store)) { args.Add("-c"); args.Add(store); }
        args.Add("-n"); args.Add(limit.ToString());
        args.Add("--json");

        var output = await RunAsync(args, ct);
        return JsonSerializer.Deserialize<QmdSearchResult[]>(output, JsonOptions) ?? [];
    }

    /// <inheritdoc />
    public async Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct = default)
    {
        var output = await RunAsync(["get", id, "--json"], ct);
        return JsonSerializer.Deserialize<QmdDocument>(output, JsonOptions);
    }

    /// <inheritdoc />
    public async Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct = default)
    {
        var output = await RunAsync(["status", "--json"], ct);
        return JsonSerializer.Deserialize<QmdStoreInfo[]>(output, JsonOptions) ?? [];
    }

    /// <inheritdoc />
    public async Task UpdateIndexAsync(string? store, CancellationToken ct = default)
    {
        var args = new List<string> { "update" };
        if (!string.IsNullOrEmpty(store)) { args.Add("-c"); args.Add(store); }
        await RunAsync(args, ct);
    }

    /// <inheritdoc />
    public async Task EmbedAsync(string? store, CancellationToken ct = default)
    {
        var args = new List<string> { "embed" };
        if (!string.IsNullOrEmpty(store)) { args.Add("-c"); args.Add(store); }
        await RunAsync(args, ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        _logger?.LogDebug("Executing: {QmdPath} {Args}", _qmdPath, string.Join(' ', arguments));

        var psi = new ProcessStartInfo(_qmdPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new QmdBinaryNotFoundException(_qmdPath, ex);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdout, ct);
        var stderrTask = ReadStreamAsync(process.StandardError, stderr, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"qmd CLI call timed out after {_timeout.TotalSeconds}s. Args: {string.Join(' ', arguments)}");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            _logger?.LogWarning("qmd exited with code {ExitCode}: {Error}", process.ExitCode, error);
            throw new QmdCliException(process.ExitCode, error, arguments);
        }

        return stdout.ToString();
    }

    private static async Task ReadStreamAsync(System.IO.StreamReader reader, StringBuilder buffer, CancellationToken ct)
    {
        var buf = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buf, ct)) > 0)
            buffer.Append(buf, 0, read);
    }
}

/// <summary>Thrown when the qmd binary cannot be found at the configured path.</summary>
public sealed class QmdBinaryNotFoundException : Exception
{
    public QmdBinaryNotFoundException(string path, Exception? inner = null)
        : base($"The qmd binary was not found at '{path}'. Ensure qmd is installed and available on PATH, or configure QmdPath in the extension config.", inner) { }
}

/// <summary>Thrown when the qmd CLI returns a non-zero exit code.</summary>
public sealed class QmdCliException : Exception
{
    public int ExitCode { get; }
    public string StdErr { get; }
    public IReadOnlyList<string> Arguments { get; }

    public QmdCliException(int exitCode, string stderr, IReadOnlyList<string> arguments)
        : base($"qmd exited with code {exitCode}: {stderr}")
    {
        ExitCode = exitCode;
        StdErr = stderr;
        Arguments = arguments;
    }
}
