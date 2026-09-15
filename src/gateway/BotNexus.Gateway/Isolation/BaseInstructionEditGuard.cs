using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Prompts;
using BotNexus.Tools;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Requires an agent to classify a write or edit to a base instruction file as agnostic or
/// model-specific before the mutation runs (#3462).
/// </summary>
internal sealed class BaseInstructionEditGuard
{
    private static readonly string[] BuiltInBaseFiles = ["AGENTS.md", "SOUL.md", "WORLD.md"];

    private readonly string _workspacePath;
    private readonly HashSet<string> _baseInstructionPaths;
    private readonly Func<string, string> _getBaseFileName;

    public BaseInstructionEditGuard(
        AgentDescriptor descriptor,
        string workspacePath,
        Func<string, string>? getBaseFileName = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        _workspacePath = Path.GetFullPath(workspacePath);
        _getBaseFileName = getBaseFileName ?? ContextFileVariants.GetBaseFileName;
        _baseInstructionPaths = new HashSet<string>(PathComparer);

        foreach (var path in BuiltInBaseFiles.Concat(descriptor.SystemPromptFiles))
            AddBaseInstructionPath(path);
    }

    /// <summary>
    /// Returns a retry prompt when a base-file mutation lacks classification; otherwise null.
    /// </summary>
    public string? Evaluate(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!IsMutationTool(toolName)
            || !TryReadToolArgument(arguments, "path", out var rawPath)
            || !TargetsBaseInstructionFile(rawPath))
        {
            return null;
        }

        if (TryReadToolArgument(arguments, InstructionScopeArgument.Name, out var scope)
            && InstructionScopeArgument.IsValid(scope))
        {
            return null;
        }

        return $"Before changing base instruction file '{rawPath}', classify the change. "
            + $"Call `model_profile` for this base file, then retry with `{InstructionScopeArgument.Name}` "
            + $"set to `{InstructionScopeArgument.Agnostic}` when the rule is true for every model, or "
            + $"`{InstructionScopeArgument.ModelSpecific}` when it belongs in a model variant. "
            + "Variant files use `<stem>.<suffix>.<ext>`; the suffix grammar is "
            + $"`{ContextFileVariants.GrammarPattern}`. A valid variant file does not require this classification.";
    }

    private bool TargetsBaseInstructionFile(string rawPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(_workspacePath, rawPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        var baseFileName = _getBaseFileName(fileName);

        // A valid suffix means the target is already model-scoped. This comparison deliberately
        // consumes ContextFileVariants.GetBaseFileName rather than re-parsing the filename here.
        if (!string.Equals(fileName, baseFileName, PathComparison))
            return false;

        return _baseInstructionPaths.Contains(fullPath);
    }

    private void AddBaseInstructionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            _baseInstructionPaths.Add(Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(_workspacePath, path)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Invalid configured paths are diagnosed by configuration/path validation. They are not
            // guard targets because no mutation tool can resolve them to a usable file.
        }
    }

    private static bool IsMutationTool(string toolName) =>
        string.Equals(toolName, "write", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, "edit", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadToolArgument(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
            return false;

        value = raw.ToString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
