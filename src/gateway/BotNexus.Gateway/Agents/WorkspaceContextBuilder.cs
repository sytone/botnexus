using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

public sealed class WorkspaceContextBuilder : IContextBuilder
{
    private const string SectionSeparator = "\n\n";
    private const string BootstrapFileName = "BOOTSTRAP.md";
    private static readonly string[] DefaultPromptFiles =
        ["AGENTS.md", "SOUL.md", "TOOLS.md", "BOOTSTRAP.md", "IDENTITY.md", "USER.md"];
    private readonly IAgentWorkspaceManager _workspaceManager;

    public WorkspaceContextBuilder(IAgentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(descriptor.AgentId));
        var promptFiles = ResolvePromptFiles(descriptor);
        List<string> sections = [];

        if (!string.IsNullOrWhiteSpace(descriptor.SystemPrompt))
            sections.Add(descriptor.SystemPrompt.Trim());

        var contextFiles = await LoadContextFilesAsync(workspacePath, promptFiles, cancellationToken);
        if (contextFiles.Length > 0)
            sections.AddRange(contextFiles.Select(static f => f.Content));

        return string.Join(SectionSeparator, sections);
    }

    private static async Task<ContextFile[]> LoadContextFilesAsync(
        string workspacePath,
        IReadOnlyList<string> promptFiles,
        CancellationToken cancellationToken)
    {
        List<ContextFile> contextFiles = [];
        foreach (var promptFile in promptFiles)
        {
            if (string.IsNullOrWhiteSpace(promptFile))
                continue;

            var filePath = Path.GetFullPath(Path.Combine(workspacePath, promptFile));
            if (!IsPathUnderWorkspace(workspacePath, filePath) || !File.Exists(filePath))
                continue;

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
                contextFiles.Add(new ContextFile(promptFile, content.Trim()));

            if (Path.GetFileName(promptFile).Equals(BootstrapFileName, StringComparison.OrdinalIgnoreCase))
                DeleteBootstrapFile(filePath);
        }

        return [.. contextFiles];
    }

    private static string ResolveWorkspaceDirectory(string workspacePath)
    {
        var resolvedPath = Path.GetFullPath(workspacePath);
        if (Path.GetFileName(resolvedPath).Equals("workspace", StringComparison.OrdinalIgnoreCase))
            return resolvedPath;

        var nestedWorkspacePath = Path.Combine(resolvedPath, "workspace");
        return Directory.Exists(nestedWorkspacePath) ? nestedWorkspacePath : resolvedPath;
    }

    private static void DeleteBootstrapFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<string> ResolvePromptFiles(AgentDescriptor descriptor)
    {
        if (descriptor.SystemPromptFiles.Count > 0)
            return descriptor.SystemPromptFiles;

        if (!string.IsNullOrWhiteSpace(descriptor.SystemPromptFile))
            return [descriptor.SystemPromptFile];

        return DefaultPromptFiles;
    }

    private static bool IsPathUnderWorkspace(string workspacePath, string filePath)
    {
        var workspaceFullPath = Path.GetFullPath(workspacePath);
        var workspacePrefix = workspaceFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return filePath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase) ||
            filePath.Equals(workspaceFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ContextFile(string Path, string Content);
}
