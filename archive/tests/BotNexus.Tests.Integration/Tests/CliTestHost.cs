using System.Diagnostics;

namespace BotNexus.Tests.Integration.Tests;

internal static class CliTestHost
{
    private static readonly string RepoRoot = FindRepoRoot();

    internal static async Task<CliRunResult> RunCliAsync(string command, string homePath, string? standardInput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project src\\BotNexus.Cli -- {command} --home \"{homePath}\"",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CLI process.");

        if (!string.IsNullOrEmpty(standardInput))
            await process.StandardInput.WriteAsync(standardInput);

        process.StandardInput.Close();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliRunResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "BotNexus.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Cannot find repository root (BotNexus.slnx not found).");
    }
}

internal sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);

internal sealed class CliHomeScope : IAsyncDisposable
{
    private CliHomeScope(string path) => Path = path;

    internal string Path { get; }

    internal static Task<CliHomeScope> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "botnexus-cli-int", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return Task.FromResult(new CliHomeScope(path));
    }

    public ValueTask DisposeAsync()
    {
        CleanupDirectory(Path);
        CleanupDirectory(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + "-backups");
        return ValueTask.CompletedTask;
    }

    private static void CleanupDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
