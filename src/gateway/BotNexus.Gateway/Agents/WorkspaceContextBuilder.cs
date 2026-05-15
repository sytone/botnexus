using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Loads workspace context files and delegates prompt assembly to <see cref="SystemPromptBuilder"/>.
/// </summary>
public sealed class WorkspaceContextBuilder : IContextBuilder
{
    private const string BootstrapFileName = "BOOTSTRAP.md";
    private const string MemoryFileName = "MEMORY.md";
    private const string MemoryPromptInjectionNone = "none";
    private const string MemoryPromptInjectionFull = "full";
    private static readonly string[] DefaultPromptFiles =
        ["AGENTS.md", "SOUL.md", "TOOLS.md", "BOOTSTRAP.md", "IDENTITY.md", "USER.md", MemoryFileName];
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IFileSystem _fileSystem;
    private readonly IHookDispatcher? _hookDispatcher;
    private readonly IConversationStore? _conversationStore;
    private readonly ISessionStore? _sessionStore;

    public WorkspaceContextBuilder(IAgentWorkspaceManager workspaceManager, IFileSystem fileSystem)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
    }

    public WorkspaceContextBuilder(
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem,
        IHookDispatcher hookDispatcher)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
        _hookDispatcher = hookDispatcher;
    }

    public WorkspaceContextBuilder(
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem,
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        IHookDispatcher? hookDispatcher = null)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
        _conversationStore = conversationStore;
        _sessionStore = sessionStore;
        _hookDispatcher = hookDispatcher;
    }

    public async Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(descriptor.AgentId));
        var memoryPromptInjection = ResolveMemoryPromptInjection(descriptor.Memory?.PromptInjection);
        var promptFiles = ResolvePromptFiles(descriptor, includeMemoryFile: !IsMemoryPromptInjectionNone(memoryPromptInjection));
        var contextFiles = (await LoadContextFilesAsync(_fileSystem, workspacePath, promptFiles, cancellationToken)).ToList();
        if (descriptor.SystemPromptFiles.Count == 0 && string.IsNullOrWhiteSpace(descriptor.SystemPromptFile))
        {
            if (!IsMemoryPromptInjectionNone(memoryPromptInjection))
            {
                var recentMemoryFiles = await LoadRecentDailyMemoryFilesAsync(_fileSystem, workspacePath, descriptor.Memory?.Path, cancellationToken);
                contextFiles.AddRange(recentMemoryFiles);
            }
        }

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
            HeartbeatPrompt = descriptor.Heartbeat?.Enabled == true
                ? descriptor.Heartbeat.Prompt ?? "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK."
                : null,
            MemoryPromptInjection = memoryPromptInjection,
            ConversationContext = await ResolveConversationContextAsync(descriptor, executionContext, cancellationToken),
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

    public Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
        => BuildSystemPromptAsync(descriptor, null, cancellationToken);

    private async Task<ConversationContext?> ResolveConversationContextAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        if (_conversationStore is null || executionContext is null)
            return null;

        Conversation? conversation = null;
        if (_sessionStore is not null)
        {
            var session = await _sessionStore.GetAsync(executionContext.SessionId, cancellationToken).ConfigureAwait(false);
            if (session?.Session.ConversationId is { } conversationId)
                conversation = await _conversationStore.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }

        if (conversation is null)
        {
            var conversations = await _conversationStore.ListAsync(descriptor.AgentId, cancellationToken).ConfigureAwait(false);
            conversation = conversations.FirstOrDefault(candidate => candidate.ActiveSessionId == executionContext.SessionId);
        }

        return conversation is null
            ? null
            : new ConversationContext(conversation.ConversationId.Value, conversation.Title, conversation.Purpose);
    }

    private static async Task<ContextFile[]> LoadContextFilesAsync(
        IFileSystem fileSystem,
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
            if (!IsPathUnderWorkspace(workspacePath, filePath) || !fileSystem.File.Exists(filePath))
                continue;

            var content = await fileSystem.File.ReadAllTextAsync(filePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
                contextFiles.Add(new ContextFile(promptFile, content.Trim()));

            if (Path.GetFileName(promptFile).Equals(BootstrapFileName, StringComparison.OrdinalIgnoreCase))
                DeleteBootstrapFile(fileSystem, filePath);
        }

        return [.. contextFiles];
    }

    private string ResolveWorkspaceDirectory(string workspacePath)
    {
        var resolvedPath = Path.GetFullPath(workspacePath);
        if (Path.GetFileName(resolvedPath).Equals("workspace", StringComparison.OrdinalIgnoreCase))
            return resolvedPath;

        var nestedWorkspacePath = Path.Combine(resolvedPath, "workspace");
        return _fileSystem.Directory.Exists(nestedWorkspacePath) ? nestedWorkspacePath : resolvedPath;
    }

    private static void DeleteBootstrapFile(IFileSystem fileSystem, string filePath)
    {
        try { fileSystem.File.Delete(filePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IReadOnlyList<string> ResolvePromptFiles(AgentDescriptor descriptor, bool includeMemoryFile)
    {
        if (descriptor.SystemPromptFiles.Count > 0)
            return FilterMemoryFiles(descriptor.SystemPromptFiles, includeMemoryFile);

        if (!string.IsNullOrWhiteSpace(descriptor.SystemPromptFile))
            return includeMemoryFile || !IsMemoryPromptFile(descriptor.SystemPromptFile) ? [descriptor.SystemPromptFile] : [];

        return includeMemoryFile ? DefaultPromptFiles : FilterMemoryFiles(DefaultPromptFiles, includeMemoryFile);
    }

    private static IReadOnlyList<string> FilterMemoryFiles(IReadOnlyList<string> promptFiles, bool includeMemoryFile)
    {
        if (includeMemoryFile)
            return promptFiles;

        return promptFiles.Where(static file => !IsMemoryPromptFile(file)).ToList();
    }

    private static bool IsMemoryPromptFile(string? promptFile) =>
        !string.IsNullOrWhiteSpace(promptFile) &&
        Path.GetFileName(promptFile).Equals(MemoryFileName, StringComparison.OrdinalIgnoreCase);

    private static string ResolveMemoryPromptInjection(string? promptInjection)
    {
        if (string.IsNullOrWhiteSpace(promptInjection))
            return MemoryPromptInjectionFull;

        return promptInjection.Trim();
    }

    private static bool IsMemoryPromptInjectionNone(string promptInjection) =>
        promptInjection.Equals(MemoryPromptInjectionNone, StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<ContextFile>> LoadRecentDailyMemoryFilesAsync(
        IFileSystem fileSystem,
        string workspacePath,
        string? memoryPathOverride,
        CancellationToken cancellationToken)
    {
        var memoryRoot = ResolveMemoryRoot(fileSystem, workspacePath, memoryPathOverride);
        if (!fileSystem.Directory.Exists(memoryRoot))
            return [];

        var today = DateTime.Now.Date;
        var targetNames = new HashSet<string>(StringComparer.Ordinal)
        {
            today.ToString("yyyy-MM-dd"),
            today.AddDays(-1).ToString("yyyy-MM-dd")
        };

        var files = fileSystem.Directory.GetFiles(memoryRoot, "*.md")
            .Select(path => new
            {
                FullPath = path,
                Name = fileSystem.Path.GetFileNameWithoutExtension(path),
                RelativePath = fileSystem.Path.GetRelativePath(workspacePath, path).Replace('\\', '/')
            })
            .Where(file => targetNames.Contains(file.Name))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();

        List<ContextFile> result = [];
        foreach (var file in files)
        {
            var content = await fileSystem.File.ReadAllTextAsync(file.FullPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
                result.Add(new ContextFile(file.RelativePath, content.Trim()));
        }

        return result;
    }

    private static string ResolveMemoryRoot(IFileSystem fileSystem, string workspacePath, string? memoryPathOverride)
    {
        var relative = string.IsNullOrWhiteSpace(memoryPathOverride)
            ? "memory"
            : memoryPathOverride.Trim().Replace('\\', '/');
        if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relative = fileSystem.Path.GetDirectoryName(relative) ?? "memory";

        var memoryRoot = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(workspacePath, relative));
        var workspaceFullPath = fileSystem.Path.GetFullPath(workspacePath);
        var workspacePrefix = workspaceFullPath.TrimEnd(fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar)
            + fileSystem.Path.DirectorySeparatorChar;
        if (!memoryRoot.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase) &&
            !memoryRoot.Equals(workspaceFullPath, StringComparison.OrdinalIgnoreCase))
            return fileSystem.Path.Combine(workspacePath, "memory");

        return memoryRoot;
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
