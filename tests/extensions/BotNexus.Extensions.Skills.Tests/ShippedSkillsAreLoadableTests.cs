using System.IO.Abstractions;
using System.Text.RegularExpressions;
using BotNexus.Extensions.Skills;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Every SKILL.md this repository ships actually loads, and its installer puts it where
/// discovery will find it.
///
/// <remarks>
/// A skill is prose with a frontmatter contract: nothing is compiled and nothing fails loudly.
/// A malformed one is SKIPPED by <see cref="SkillDiscovery"/> with a log line nobody reads, and
/// the agent simply never gains the capability - indistinguishable from the skill working badly.
/// <para>
/// The sharpest edge is that discovery requires the skill's DIRECTORY name to equal its
/// frontmatter name. The install script chooses the directory and the skill author chooses the
/// name, and they are in different files - so they can drift, and when they do the operator
/// installs something that is silently never loaded.
/// </para>
/// </remarks>
/// </summary>
public sealed class ShippedSkillsAreLoadableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private sealed record Shipped(string Directory, string SkillMdPath, string DeclaredName);

    /// <summary>Every <c>docs/*-skill/SKILL.md</c>, with the name its frontmatter declares.</summary>
    private static IReadOnlyList<Shipped> FindShipped()
    {
        var results = new List<Shipped>();
        foreach (var directory in Directory.GetDirectories(Path.Combine(RepoRoot, "docs"), "*-skill"))
        {
            var skillMd = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillMd))
                continue;

            var match = Regex.Match(
                File.ReadAllText(skillMd),
                @"^name:\s*(?<name>[^\r\n]+)$",
                RegexOptions.Multiline);

            match.Success.ShouldBeTrue($"{skillMd} has no 'name:' in its frontmatter");
            results.Add(new Shipped(directory, skillMd, match.Groups["name"].Value.Trim().Trim('"')));
        }

        return results;
    }

    [Fact]
    public void Every_shipped_skill_is_discovered_rather_than_silently_skipped()
    {
        var shipped = FindShipped();
        shipped.ShouldNotBeEmpty("this test is vacuous if it finds no skills to check");

        // Staged the way an installer lays them out: one directory per skill, named for the
        // skill. Discovery rejects any other arrangement, which is the point of the next test.
        var staged = Path.Combine(Path.GetTempPath(), "botnexus-shipped-skills", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var skill in shipped)
            {
                var destination = Path.Combine(staged, skill.DeclaredName);
                Directory.CreateDirectory(destination);
                File.Copy(skill.SkillMdPath, Path.Combine(destination, "SKILL.md"));
            }

            var discovered = SkillDiscovery.Discover(
                staged, agentSkillsDir: null, workspaceSkillsDir: null, new FileSystem());

            discovered.Select(s => s.Name).OrderBy(n => n)
                .ShouldBe(shipped.Select(s => s.DeclaredName).OrderBy(n => n));
        }
        finally
        {
            if (Directory.Exists(staged))
                Directory.Delete(staged, recursive: true);
        }
    }

    [Fact]
    public void Each_installer_writes_the_skill_to_a_directory_named_after_it()
    {
        // THE test in this file. SkillDiscovery.TryValidate rejects a skill whose frontmatter
        // name differs from its containing directory - so if an install script's destination and
        // the skill's declared name drift apart, the operator installs a skill that is skipped
        // with a log line and never loads. Nothing else in the build notices; the two values
        // live in different files and neither is compiled.
        foreach (var skill in FindShipped())
        {
            var installer = Directory
                .GetFiles(Path.Combine(RepoRoot, "scripts"), "install-*-skill.sh")
                .Select(File.ReadAllText)
                .FirstOrDefault(text => text.Contains(Path.GetFileName(skill.Directory), StringComparison.Ordinal));

            installer.ShouldNotBeNull($"no install script references docs/{Path.GetFileName(skill.Directory)}");
            installer.ShouldContain(
                $"/skills/{skill.DeclaredName}",
                Case.Sensitive,
                $"the installer must write '{skill.DeclaredName}' to a directory of that name, or discovery skips it");
        }
    }

    [Fact]
    public void Every_install_script_is_committed_executable()
    {
        // A shell script with a shebang that is not executable fails with "Permission denied" the
        // first time an operator runs it the obvious way. It slipped through because this repo
        // sets core.fileMode=false - the checkout lives on a volume that cannot hold the bit - so
        // `chmod +x` is a NO-OP as far as git is concerned and the file commits as 100644. The
        // mode has to be set in the index directly (`git update-index --chmod=+x`), which is not
        // something anyone does by habit.
        //
        // Reads the mode out of git rather than off disk, because on this checkout disk cannot
        // answer: every file reports the same permissions whatever git records.
        var modes = Git("ls-files -s -- scripts/install-*.sh")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Mode: parts[0].Split(' ')[0], Path: parts[1].Trim()))
            .ToList();

        modes.ShouldNotBeEmpty("no install scripts found - the glob or the directory has moved");

        var notExecutable = modes.Where(m => m.Mode != "100755").Select(m => m.Path).ToList();
        notExecutable.ShouldBeEmpty(
            $"committed non-executable, so running them directly fails: {string.Join(", ", notExecutable)}");
    }

    /// <summary>Runs a git command in the repository and returns stdout.</summary>
    private static string Git(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void A_skills_description_says_when_to_use_it_not_just_what_it_is()
    {
        // The description is the only thing a model sees when deciding whether to load a skill.
        // One that describes the contents rather than the trigger is a skill that never fires.
        foreach (var skill in FindShipped())
        {
            var content = File.ReadAllText(skill.SkillMdPath);
            var description = Regex.Match(
                content, @"^description:\s*(?<d>[^\r\n]+)$", RegexOptions.Multiline);

            description.Success.ShouldBeTrue($"{skill.DeclaredName} has no description");
            description.Groups["d"].Value.ShouldContain(
                "Use",
                Case.Insensitive,
                $"'{skill.DeclaredName}' never states when to use it, so nothing will choose it");
        }
    }
}
