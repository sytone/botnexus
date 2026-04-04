using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BotNexus.Tests.Deployment.Infrastructure;

/// <summary>
/// Manages starting/stopping the BotNexus Gateway as a real OS process.
/// Each instance creates an isolated temp directory for BOTNEXUS_HOME and
/// a working directory with a custom appsettings.json.
/// </summary>
public sealed class GatewayProcessFixture : IAsyncDisposable
{
    private Process? _process;
    private readonly string _tempRoot;
    private readonly string _botNexusHome;
    private readonly string _workingDir;
    private readonly int _port;
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];
    private bool _disposed;

    public string BotNexusHome => _botNexusHome;
    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}";
    public string SessionsPath => Path.Combine(_botNexusHome, "sessions");
    public string AgentsPath => Path.Combine(_botNexusHome, "agents");
    public string ExtensionsPath => Path.Combine(_botNexusHome, "extensions");
    public string ConfigJsonPath => Path.Combine(_botNexusHome, "config.json");
    public IReadOnlyList<string> Stdout => _stdout;
    public IReadOnlyList<string> Stderr => _stderr;
    public bool IsRunning => _process is not null && !_process.HasExited;

    private static readonly string RepoRoot = FindRepoRoot();

    public static string GatewayDllPath { get; } = Path.Combine(
        RepoRoot, "src", "BotNexus.Gateway", "bin", "Debug", "net10.0", "BotNexus.Gateway.dll");

    public static string ExtensionE2EDllPath { get; } = Path.Combine(
        RepoRoot, "tests", "BotNexus.Tests.Extensions.E2E", "bin", "Debug", "net10.0",
        "BotNexus.Tests.Extensions.E2E.dll");

    public static string ExtensionConventionDllPath { get; } = Path.Combine(
        RepoRoot, "tests", "BotNexus.Tests.Extensions.Convention", "bin", "Debug", "net10.0",
        "BotNexus.Tests.Extensions.Convention.dll");

    public GatewayProcessFixture(int port)
    {
        _port = port;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"botnexus-deploy-{Guid.NewGuid():N}");
        _botNexusHome = Path.Combine(_tempRoot, "home");
        _workingDir = Path.Combine(_tempRoot, "run");
        Directory.CreateDirectory(_botNexusHome);
        Directory.CreateDirectory(_workingDir);
    }

    /// <summary>Write appsettings.json to the working directory (primary config source).</summary>
    public void WriteAppSettings(string json) =>
        File.WriteAllText(Path.Combine(_workingDir, "appsettings.json"), json);

    /// <summary>Pre-create config.json in BOTNEXUS_HOME (prevents default creation by Initialize).</summary>
    public void WriteConfigJson(string json) =>
        File.WriteAllText(ConfigJsonPath, json);

    /// <summary>Start the Gateway as an OS process.</summary>
    public Task StartAsync()
    {
        if (!File.Exists(GatewayDllPath))
            throw new FileNotFoundException(
                $"Gateway DLL not found at {GatewayDllPath}. Build the solution first.");

        _stdout.Clear();
        _stderr.Clear();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{GatewayDllPath}\"",
            WorkingDirectory = _workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.Environment["BOTNEXUS_HOME"] = _botNexusHome;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Production";

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Gateway process");

        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_stdout) _stdout.Add(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.Add(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    /// <summary>Poll /health until it returns 200 or timeout.</summary>
    public async Task WaitForHealthyAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout.Value);
        using var client = CreateHttpClient();

        while (!cts.IsCancellationRequested)
        {
            if (_process?.HasExited == true)
            {
                var errors = string.Join(Environment.NewLine, _stderr.TakeLast(30));
                var output = string.Join(Environment.NewLine, _stdout.TakeLast(30));
                throw new InvalidOperationException(
                    $"Gateway process exited with code {_process.ExitCode}.\nStdout:\n{output}\nStderr:\n{errors}");
            }

            try
            {
                var response = await client.GetAsync("/health", cts.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }

            await Task.Delay(200, cts.Token);
        }

        var errLog = string.Join(Environment.NewLine, _stderr.TakeLast(30));
        var outLog = string.Join(Environment.NewLine, _stdout.TakeLast(30));
        throw new TimeoutException(
            $"Gateway did not become healthy within {timeout}.\nStdout:\n{outLog}\nStderr:\n{errLog}");
    }

    /// <summary>Kill the Gateway process.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (_process is null || _process.HasExited) return;

        timeout ??= TimeSpan.FromSeconds(10);
        try
        {
            _process.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(timeout.Value);
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Create an HttpClient pointed at this Gateway instance.</summary>
    public HttpClient CreateHttpClient() => new() { BaseAddress = new Uri(BaseUrl) };

    /// <summary>Find a free TCP port on localhost.</summary>
    public static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Deploy an extension DLL to the extensions folder under the specified type and key.</summary>
    public void DeployExtension(string type, string key, string sourceDllPath)
    {
        var targetDir = Path.Combine(ExtensionsPath, type, key);
        Directory.CreateDirectory(targetDir);
        File.Copy(sourceDllPath, Path.Combine(targetDir, Path.GetFileName(sourceDllPath)), overwrite: true);
    }

    /// <summary>Remove an extension folder.</summary>
    public void RemoveExtension(string type, string key)
    {
        var targetDir = Path.Combine(ExtensionsPath, type, key);
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, recursive: true);
    }

    /// <summary>Write a session JSONL file to the sessions directory.</summary>
    public void SeedSession(string sessionKey, params (string role, string content)[] entries)
    {
        Directory.CreateDirectory(SessionsPath);
        var encoded = Uri.EscapeDataString(sessionKey).Replace("%", "_");
        var filePath = Path.Combine(SessionsPath, $"{encoded}.jsonl");
        var lines = entries.Select(e =>
            JsonSerializer.Serialize(new
            {
                role = e.role,
                content = e.content,
                timestamp = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.WriteAllLines(filePath, lines);
    }

    /// <summary>Check if a session file exists.</summary>
    public bool SessionFileExists(string sessionKey)
    {
        var encoded = Uri.EscapeDataString(sessionKey).Replace("%", "_");
        return File.Exists(Path.Combine(SessionsPath, $"{encoded}.jsonl"));
    }

    /// <summary>Generate minimal appsettings.json with no providers/channels.</summary>
    public static string DefaultAppSettings(int port) => $$"""
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "BotNexus": {
            "ExtensionsPath": "~/.botnexus/extensions",
            "Extensions": { "DryRun": false },
            "Providers": {},
            "Channels": { "Instances": {} },
            "Gateway": {
              "Host": "127.0.0.1",
              "Port": {{port}},
              "ApiKey": "",
              "WebSocketEnabled": true,
              "WebSocketPath": "/ws",
              "Heartbeat": { "Enabled": false }
            },
            "Agents": { "Workspace": "~/.botnexus", "Named": {} },
            "Tools": { "McpServers": {}, "Extensions": {} }
          }
        }
        """;

    /// <summary>Minimal config.json that prevents Initialize() from writing defaults.</summary>
    public static string MinimalConfigJson() => """
        {
          "BotNexus": {}
        }
        """;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        _process?.Dispose();

        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, recursive: true);
                break;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BotNexus.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Cannot find repository root (BotNexus.slnx not found)");
    }
}
