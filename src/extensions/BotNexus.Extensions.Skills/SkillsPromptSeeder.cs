using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Skills;

/// <summary>Installs Skills-extension-owned prompt templates into the shared prompt catalogue.</summary>
public static class SkillsPromptSeeder
{
    internal const string OptimizationPromptFileName = "optimize-shared-skills.prompt.md";
    private const string OptimizationPromptResourceName =
        "BotNexus.Extensions.Skills.Resources.Prompts.optimize-shared-skills.prompt.md";

    /// <summary>
    /// Copies the bundled shared-skill optimization prompt when it is absent. Existing operator
    /// content is never overwritten.
    /// </summary>
    public static void EnsureSharedPromptSeed(
        string? sharedPromptsDir,
        IFileSystem? fileSystem = null,
        ILogger? logger = null)
        => EnsureSharedPromptSeed(sharedPromptsDir, fileSystem ?? new FileSystem(), logger, TimeProvider.System, Thread.Sleep);

    // A clock plus delay seam keeps deadline/attempt-limit tests independent of wall-clock sleeps.
    internal static void EnsureSharedPromptSeed(
        string? sharedPromptsDir,
        IFileSystem fs,
        ILogger? logger,
        TimeProvider timeProvider,
        Action<TimeSpan> delay)
    {
        if (string.IsNullOrWhiteSpace(sharedPromptsDir))
            return;

        var destinationPath = fs.Path.Combine(sharedPromptsDir, OptimizationPromptFileName);
        if (AtomicSeedFile.EnsureSeed(fs, sharedPromptsDir, destinationPath, ReadBundledPrompt,
            logger, timeProvider, delay))
            logger?.LogInformation("Skills: installed shared prompt template at '{PromptPath}'.", destinationPath);
    }

    // Retain the existing internal diagnostic seams; both seeders use the same implementation.
    internal static void PublishCompletedFile(IFileSystem fs, string source, string destination)
        => AtomicSeedFile.PublishCompletedFile(fs, source, destination);

    internal static void LinkCompletedFile(string source, string destination, Func<string, string, int> link)
        => AtomicSeedFile.LinkCompletedFile(source, destination, link);

    private static string ReadBundledPrompt()
    {
        var assembly = typeof(SkillsPromptSeeder).Assembly;
        using var stream = assembly.GetManifestResourceStream(OptimizationPromptResourceName)
            ?? throw new InvalidOperationException(
                $"Bundled prompt resource '{OptimizationPromptResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
