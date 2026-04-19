using System.CommandLine;
using System.Diagnostics;

namespace BotNexus.Cli.Commands;

internal sealed class InstallCommand
{
    private const string DefaultRepo = "https://github.com/jbullen/botnexus.git";

    public Command Build(Option<bool> verboseOption)
    {
        var pathOption = new Option<string?>(
            "--path",
            () => null,
            "Target directory for the clone. Defaults to USERPROFILE/botnexus.");

        var repoOption = new Option<string>(
            "--repo",
            () => DefaultRepo,
            "Git repository URL to clone.");

        var buildOption = new Option<bool>(
            "--build",
            "Build the solution after cloning.");

        var command = new Command("install", "Clone the BotNexus repository and optionally build it.")
        {
            pathOption,
            repoOption,
            buildOption
        };

        command.SetHandler(async context =>
        {
            var path = context.ParseResult.GetValueForOption(pathOption);
            var repo = context.ParseResult.GetValueForOption(repoOption)!;
            var build = context.ParseResult.GetValueForOption(buildOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteAsync(path, repo, build, verbose, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(string? path, string repo, bool build, bool verbose, CancellationToken cancellationToken)
    {
        var targetPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "botnexus");

        if (Directory.Exists(Path.Combine(targetPath, ".git")))
        {
            Console.WriteLine($"Repository already exists at: {targetPath}");
            Console.WriteLine("Use 'git pull' to update, or remove the directory and re-run install.");
        }
        else
        {
            Console.WriteLine($"Cloning {repo} → {targetPath}");
            var cloneResult = await RunProcessAsync("git", $"clone \"{repo}\" \"{targetPath}\"", null, verbose, cancellationToken);
            if (cloneResult != 0)
            {
                Console.WriteLine("Clone failed.");
                return cloneResult;
            }

            Console.WriteLine($"Repository cloned to: {targetPath}");
        }

        if (build)
        {
            Console.WriteLine();
            var buildResult = await BuildCommand.BuildSolutionAsync(targetPath, verbose, cancellationToken);
            if (buildResult != 0)
                return buildResult;
        }

        return 0;
    }

    internal static async Task<int> RunProcessAsync(string fileName, string arguments, string? workingDirectory, bool verbose, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = !verbose,
            RedirectStandardError = !verbose
        };

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
