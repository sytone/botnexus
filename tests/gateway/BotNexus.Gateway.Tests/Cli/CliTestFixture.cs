using System.Diagnostics;
using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Cli;

internal sealed class CliTestFixture : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _rootPath;

    private CliTestFixture(string rootPath)
    {
        _rootPath = rootPath;
        ConfigPath = Path.Combine(_rootPath, "config.json");
    }

    public string RootPath => _rootPath;
    public string ConfigPath { get; }

    public static async Task<CliTestFixture> CreateAsync(string? configJson = null)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var fixture = new CliTestFixture(rootPath);
        if (configJson is not null)
            await File.WriteAllTextAsync(fixture.ConfigPath, configJson);
        return fixture;
    }

    public async Task<PlatformConfig> LoadConfigAsync()
    {
        await using var stream = File.OpenRead(ConfigPath);
        return (await JsonSerializer.DeserializeAsync<PlatformConfig>(stream, ReadOptions))!;
    }

    public async Task<CliResult> RunCliAsync(params string[] args)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add(ResolveCliAssemblyPath());
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["BOTNEXUS_HOME"] = _rootPath;

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("CLI command timed out.");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string ResolveCliAssemblyPath()
    {
        var localCopy = Path.Combine(AppContext.BaseDirectory, "BotNexus.Cli.dll");
        var root = FindRepositoryRoot();
        var fallback = Path.Combine(root, "src", "gateway", "BotNexus.Cli", "bin", "Debug", "net10.0", "BotNexus.Cli.dll");

        if (File.Exists(localCopy) && File.Exists(fallback))
            return File.GetLastWriteTimeUtc(fallback) > File.GetLastWriteTimeUtc(localCopy) ? fallback : localCopy;
        if (File.Exists(localCopy))
            return localCopy;
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException("Unable to locate BotNexus.Cli.dll for CLI tests.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be resolved from test base path.");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public string CombinedOutput => $"{StdOut}{Environment.NewLine}{StdErr}";
}
