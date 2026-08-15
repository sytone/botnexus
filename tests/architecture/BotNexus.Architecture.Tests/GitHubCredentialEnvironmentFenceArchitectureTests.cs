using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function pinning AC5 of #2732: the GitHub extension must never assign a
/// credential — or anything else — to a process environment variable.
/// </summary>
/// <remarks>
/// <para><b>Why this is a static source fence rather than a unit test.</b> The defect being
/// prevented is the pre-existing workflow this extension replaces: minting a token into
/// <c>$env:GH_TOKEN</c> so a shell can use it. An environment variable is process-global — every
/// child process the agent spawns inherits it, and any tool that dumps the environment surfaces it.
/// A unit test can only observe the code paths it happens to call; a source scan catches the
/// assignment even on a branch no test exercises.</para>
/// <para>The scan targets <c>Environment.SetEnvironmentVariable</c> and direct writes to a
/// <c>ProcessStartInfo</c>/<c>StartInfo</c> environment dictionary, which are the two ways managed
/// code exports a value into an environment block.</para>
/// </remarks>
public sealed class GitHubCredentialEnvironmentFenceArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string ExtensionRoot =>
        Path.Combine(RepoRoot, "src", "extensions", "BotNexus.Extensions.GitHub");

    [Fact]
    public void GitHubExtension_ProjectExists()
    {
        // Vacuity guard: without this, deleting the extension would make every fence below pass by
        // enumerating nothing.
        Directory.Exists(ExtensionRoot).ShouldBeTrue(
            $"GitHub extension project not found at {ExtensionRoot} (#2732).");

        EnumerateSources().ShouldNotBeEmpty(
            "No C# sources found under the GitHub extension — the environment fence would be vacuous.");
    }

    [Fact]
    public void GitHubExtension_NeverAssignsAnyValueToAnEnvironmentVariable()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSources())
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');

            if (Regex.IsMatch(text, @"Environment\s*\.\s*SetEnvironmentVariable"))
                offenders.Add($"{relative}: calls Environment.SetEnvironmentVariable.");

            if (Regex.IsMatch(text, @"(StartInfo|ProcessStartInfo)[^\r\n;]*\.\s*(Environment|EnvironmentVariables)\s*\["))
                offenders.Add($"{relative}: writes into a process environment block.");
        }

        offenders.ShouldBeEmpty(
            "The GitHub extension must never export a credential (or anything else) to an environment "
            + "variable (#2732 AC5). An environment variable is process-global and is inherited by every "
            + "child process the agent spawns, which is precisely the agent-visible-credential hole the "
            + "platform-owned provider exists to close:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void GitHubExtension_RegistersThroughTheServiceContributorSeam()
    {
        // AC1: registration must go through the existing seam, not a bespoke mechanism.
        var registersViaSeam = EnumerateSources()
            .Select(File.ReadAllText)
            .Any(text => text.Contains("IServiceContributor", StringComparison.Ordinal));

        registersViaSeam.ShouldBeTrue(
            "The GitHub extension must register through IServiceContributor (#2732 AC1). "
            + "See src/gateway/BotNexus.Gateway.Abstractions/Extensions/IServiceContributor.cs.");
    }

    private static string[] EnumerateSources()
    {
        if (!Directory.Exists(ExtensionRoot))
            return [];

        return Directory
            .EnumerateFiles(ExtensionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var normalised = path.Replace('\\', '/');
                return !normalised.Contains("/bin/") && !normalised.Contains("/obj/");
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
