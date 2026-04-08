using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Loads workspace context files and delegates prompt assembly to <see cref="SystemPromptBuilder"/>.
/// </summary>
public sealed class WorkspaceContextBuilder : IContextBuilder
{
    private const string BootstrapFileName = "BOOTSTRAP.md";
    private static readonly string[] DefaultPromptFiles =
        ["AGENTS.md", "SOUL.md", "TOOLS.md", "BOOTSTRAP.md", "IDENTITY.md", "USER.md"];
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IHookDispatcher? _hookDispatcher;

    public WorkspaceContextBuilder(IAgentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public WorkspaceContextBuilder(
        IAgentWorkspaceManager workspaceManager,
        IHookDispatcher hookDispatcher)
    {
        _workspaceManager = workspaceManager;
        _hookDispatcher = hookDispatcher;
    }

    public async Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(descriptor.AgentId));
        var promptFiles = ResolvePromptFiles(descriptor);
        var contextFiles = await LoadContextFilesAsync(workspacePath, promptFiles, cancellationToken);

        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = workspacePath,
            ExtraSystemPrompt = descriptor.SystemPrompt,
            ContextFiles = contextFiles,
            Runtime = new RuntimeInfo
            {
                AgentId = descriptor.AgentId,
                Host = Environment.MachineName,
                Os = Environment.OSVersion.ToString(),
                Provider = descriptor.ApiProvider,
                Model = descriptor.ModelId,
                Channel = "signalr"
            },
            PromptMode = PromptMode.Full
        });

        // Dispatch BeforePromptBuild hooks (e.g. skills injection)
        if (_hookDispatcher is not null)
        {
            var hookEvent = new BeforePromptBuildEvent(descriptor.AgentId, prompt, []);
            var results = await _hookDispatcher
                .DispatchAsync<BeforePromptBuildEvent, BeforePromptBuildResult>(hookEvent, cancellationToken)
                .ConfigureAwait(false);
            prompt = MergeHookResults(prompt, results);
        }

        return prompt;
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
        try { File.Delete(filePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private static string MergeHookResults(string prompt, IReadOnlyList<BeforePromptBuildResult> results)
    {
        if (results.Count == 0)
            return prompt;

        var prepend = string.Join("\n", results
            .Where(r => !string.IsNullOrWhiteSpace(r.PrependSystemContext))
            .Select(r => r.PrependSystemContext));

        var append = string.Join("\n", results
            .Where(r => !string.IsNullOrWhiteSpace(r.AppendSystemContext))
            .Select(r => r.AppendSystemContext));

        if (!string.IsNullOrWhiteSpace(prepend))
            prompt = prepend + "\n" + prompt;

        if (!string.IsNullOrWhiteSpace(append))
            prompt = prompt + "\n" + append;

        return prompt;
    }
}
