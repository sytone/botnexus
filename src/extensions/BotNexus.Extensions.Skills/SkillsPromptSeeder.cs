using System.IO.Abstractions;
using System.Reflection;
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
    {
        if (string.IsNullOrWhiteSpace(sharedPromptsDir))
            return;

        var fs = fileSystem ?? new FileSystem();
        var destinationPath = fs.Path.Combine(sharedPromptsDir, OptimizationPromptFileName);
        if (fs.File.Exists(destinationPath))
            return;

        fs.Directory.CreateDirectory(sharedPromptsDir);
        fs.File.WriteAllText(destinationPath, ReadBundledPrompt());

        logger?.LogInformation(
            "Skills: installed shared prompt template at '{PromptPath}'.",
            destinationPath);
    }

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
