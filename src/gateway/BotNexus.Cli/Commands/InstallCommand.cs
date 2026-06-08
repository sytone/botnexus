using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class InstallCommand
{
    private const string DefaultRepo = "https://github.com/sytone/botnexus.git";

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var sourceOption = new Option<string?>(
            "--source",
            () => null,
            "Directory to clone into. Defaults to ~/botnexus.");

        var repoOption = new Option<string>(
            "--repo",
            () => DefaultRepo,
            "Git repository URL to clone.");

        var buildOption = new Option<bool>(
            "--build",
            "Build the solution after cloning.");

        var command = new Command("install", "Clone the BotNexus repository and optionally build it.")
        {
            sourceOption,
            repoOption,
            buildOption
        };

        command.SetHandler(async context =>
        {
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var repo = context.ParseResult.GetValueForOption(repoOption)!;
            var build = context.ParseResult.GetValueForOption(buildOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteAsync(source, repo, build, verbose, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(string? source, string repo, bool build, bool verbose, CancellationToken cancellationToken)
    {
        var targetPath = CliPaths.ResolveSource(source);

        if (Directory.Exists(Path.Combine(targetPath, ".git")))
        {
            AnsiConsole.MarkupLine($"Repository already exists at: [dim]{Markup.Escape(targetPath)}[/]");
            AnsiConsole.MarkupLine("Use [green]git pull[/] to update, or remove the directory and re-run install.");
        }
        else
        {
            AnsiConsole.MarkupLine($"Cloning [dim]{Markup.Escape(repo)}[/] → [dim]{Markup.Escape(targetPath)}[/]");
            var cloneResult = await RunProcessAsync("git", $"clone \"{repo}\" \"{targetPath}\"", null, verbose, cancellationToken);
            if (cloneResult != 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Clone failed.");
                return cloneResult;
            }

            AnsiConsole.MarkupLine($"[green]\u2713[/] Repository cloned to: [dim]{Markup.Escape(targetPath)}[/]");
        }

        if (build)
        {
            AnsiConsole.WriteLine();
            var buildResult = await BuildCommand.BuildSolutionAsync(targetPath, verbose, cancellationToken);
            if (buildResult != 0)
                return buildResult;
        }

        return 0;
    }

    internal static async Task<int> RunProcessAsync(string fileName, string arguments, string? workingDirectory, bool showOutput, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = !showOutput,
            RedirectStandardError = !showOutput
        };

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        // Drain redirected streams to prevent deadlocks on large output
        if (!showOutput)
        {
            await Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(cancellationToken),
                process.StandardError.ReadToEndAsync(cancellationToken));
        }

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
