using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Seeds a well-commented <c>example-skill</c> into the global skills directory
/// the first time the Skills extension is used on a machine.
/// <para>
/// The seed is a one-time bootstrap: it runs only when the global skills directory
/// is newly created (did not previously exist) or exists but contains no skills.
/// Existing skills are never modified or removed.
/// </para>
/// </summary>
public static class SkillsSeeder
{
    private const string ExampleSkillName = "example-skill";
    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Ensures the global skills directory exists and, if it contains no existing
    /// skills, writes the <c>example-skill</c> seed.
    /// </summary>
    /// <param name="globalSkillsDir">Path to <c>~/.botnexus/skills</c>.</param>
    /// <param name="fileSystem">Optional filesystem abstraction; defaults to real filesystem.</param>
    /// <param name="logger">Optional logger; emits info when the seed is written.</param>
    public static void EnsureGlobalSkillsSeed(
        string? globalSkillsDir,
        IFileSystem? fileSystem = null,
        ILogger? logger = null)
        => EnsureGlobalSkillsSeed(globalSkillsDir, fileSystem ?? new FileSystem(), logger, TimeProvider.System, Thread.Sleep);

    internal static void EnsureGlobalSkillsSeed(
        string? globalSkillsDir,
        IFileSystem fs,
        ILogger? logger,
        TimeProvider timeProvider,
        Action<TimeSpan> delay)
    {
        if (string.IsNullOrWhiteSpace(globalSkillsDir))
            return;

        var retry = new AtomicSeedFile.SharingRetry(timeProvider, delay);
        if (!fs.Directory.Exists(globalSkillsDir))
            retry.Execute(() => fs.Directory.CreateDirectory(globalSkillsDir));

        // Always discover existing skills, even if this caller observed a missing directory.
        if (HasExistingSkills(fs, globalSkillsDir))
            return;

        var exampleDir = fs.Path.Combine(globalSkillsDir, ExampleSkillName);
        var skillFilePath = fs.Path.Combine(exampleDir, SkillFileName);
        if (AtomicSeedFile.EnsureSeed(fs, exampleDir, skillFilePath, BuildExampleSkillContent,
            logger, timeProvider, delay, retry,
            () => HasExistingSkills(fs, globalSkillsDir, exampleDir)))
            logger?.LogInformation(
                "Skills: created example skill at '{SkillPath}'. " +
                "Use this as a template to create your own skills.", skillFilePath);
    }

    private static bool HasExistingSkills(IFileSystem fs, string skillsDir, string? excludedDirectory = null)
    {
        foreach (var subDir in fs.Directory.GetDirectories(skillsDir))
        {
            if (string.Equals(subDir, excludedDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            var skillFile = fs.Path.Combine(subDir, SkillFileName);
            if (fs.File.Exists(skillFile))
                return true;
        }

        return false;
    }

    private static string BuildExampleSkillContent() =>
        """
        ---
        name: example-skill
        description: A simple example skill to demonstrate the BotNexus skill format.
        tags: [example]
        ---
        # Example Skill

        This is an example skill. Copy this directory and edit `SKILL.md` to create your own skills.

        ## What is a Skill?

        Skills are reusable markdown files that inject domain-specific knowledge and instructions
        into an agent's context when loaded. They let you package expertise once and share it
        across sessions or agents.

        ## Instructions

        When the user asks you to greet them, respond with a friendly greeting that includes:
        - Their name if known
        - The current time of day
        - A helpful tip about BotNexus skills

        ## Example

        User: "Give me a greeting"

        Assistant: "Good morning! Welcome back. Did you know you can create your own skills by
        adding markdown files to your `~/.botnexus/skills/` directory? Each skill lives in its
        own subdirectory with a `SKILL.md` file — just like this one."

        ## How to Create Your Own Skill

        1. Copy this directory: `cp -r ~/.botnexus/skills/example-skill ~/.botnexus/skills/my-skill`
        2. Rename to match your skill name (lowercase, hyphens only)
        3. Edit the frontmatter: set `name` to match the directory name
        4. Replace the content with your domain knowledge or instructions
        5. Load it in a session: `/skills load my-skill`
        """;
}
