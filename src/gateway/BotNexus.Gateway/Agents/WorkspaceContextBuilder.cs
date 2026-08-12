using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Prompts;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Loads workspace context files and delegates prompt assembly to <see cref="SystemPromptBuilder"/>.
/// </summary>
public sealed class WorkspaceContextBuilder : IContextBuilder
{
    private const string BootstrapFileName = "BOOTSTRAP.md";
    private const string MemoryFileName = "MEMORY.md";
    private const string UserFileName = "USER.md";
    private const string MemoryPromptInjectionNone = "none";
    private const string MemoryPromptInjectionFull = "full";
    private static readonly string[] DefaultPromptFiles =
        ["AGENTS.md", "SOUL.md", "TOOLS.md", "BOOTSTRAP.md", "IDENTITY.md", UserFileName, MemoryFileName];

    /// <summary>
    /// The workspace files that belong to the agent's owner alone and must never reach a
    /// conversation with non-owner participants (issue #2846). <c>MEMORY.md</c> is the agent's
    /// consolidated long-term memory; <c>USER.md</c> is the owner's personal profile (name,
    /// timezone, working preferences). Daily memory notes are equally private but are identified
    /// by their location under the memory root rather than by name — see
    /// <see cref="IsOwnerPrivateContextFile"/>.
    /// </summary>
    private static readonly string[] OwnerPrivateFileNames = [MemoryFileName, UserFileName];
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IFileSystem _fileSystem;
    private readonly IHookDispatcher? _hookDispatcher;
    private readonly IConversationStore? _conversationStore;
    private readonly ISessionStore? _sessionStore;
    private readonly IAgentMemoryFactory? _agentMemoryFactory;
    private readonly string? _homePath;

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

    public WorkspaceContextBuilder(
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem,
        BotNexusHome botNexusHome,
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        IHookDispatcher? hookDispatcher = null)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
        _homePath = botNexusHome.RootPath;
        _conversationStore = conversationStore;
        _sessionStore = sessionStore;
        _hookDispatcher = hookDispatcher;
    }

    public WorkspaceContextBuilder(
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem,
        BotNexusHome botNexusHome)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
        _homePath = botNexusHome.RootPath;
    }

    public WorkspaceContextBuilder(
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem,
        BotNexusHome botNexusHome,
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        IAgentMemoryFactory agentMemoryFactory,
        IHookDispatcher? hookDispatcher = null)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
        _homePath = botNexusHome.RootPath;
        _conversationStore = conversationStore;
        _sessionStore = sessionStore;
        _agentMemoryFactory = agentMemoryFactory;
        _hookDispatcher = hookDispatcher;
    }

    public async Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        EffectiveExecutionSettings? effectiveSettings = null,
        CancellationToken cancellationToken = default)
        => await BuildSystemPromptAsync(descriptor, executionContext, effectiveSettings, ConversationScope.Private, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        EffectiveExecutionSettings? effectiveSettings,
        ConversationScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(descriptor.AgentId.Value));
        var conversation = await ResolveConversationAsync(descriptor, executionContext, cancellationToken).ConfigureAwait(false);
        // #2984: null for every non-cron session, so interactive prompt rendering is unchanged.
        var runStartedAt = await ResolveRunStartedAtAsync(executionContext, cancellationToken).ConfigureAwait(false);

        // #2846: the caller's scope is a floor, not the last word. A conversation whose persisted
        // participant set already contains non-owner citizens is shared even if the caller did not
        // know it, so the exclusion protects call sites that have not been threaded yet.
        scope = ResolveEffectiveScope(scope, conversation);

        var memoryPromptInjection = ResolveMemoryPromptInjection(descriptor.Memory?.PromptInjection);
        var promptFiles = ResolvePromptFiles(descriptor, includeMemoryFile: !IsMemoryPromptInjectionNone(memoryPromptInjection));
        var contextFiles = (await LoadContextFilesAsync(_fileSystem, workspacePath, promptFiles, cancellationToken)).ToList();

        // Inject world-level instructions if WORLD.md exists at ~/.botnexus/WORLD.md
        if (!string.IsNullOrWhiteSpace(_homePath))
        {
            var worldFilePath = Path.Combine(_homePath, "WORLD.md");
            if (_fileSystem.File.Exists(worldFilePath))
            {
                var worldContent = await _fileSystem.File.ReadAllTextAsync(worldFilePath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(worldContent))
                    contextFiles.Insert(0, new ContextFile("WORLD.md", worldContent.Trim()));
            }
        }

        // Automatic daily memory injection is governed by the memory config (`memory.promptInjection`)
        // alone. It is deliberately NOT gated on `systemPromptFiles` / `systemPromptFile`: those settings
        // select which workspace prompt files to load, and must not silently disable memory. Note that
        // `none` suppresses only this automatic pass; a memory file named explicitly in `systemPromptFiles`
        // is still loaded by the prompt-file pass above, because an explicit list is an explicit request.
        // NOTE: like MEMORY.md and USER.md, daily notes are owner-private content. They are loaded
        // unconditionally here and withheld later by FilterOwnerPrivateContextFiles when the
        // conversation is shared (#2846), so this pass stays concerned only with memory config.
        if (!IsMemoryPromptInjectionNone(memoryPromptInjection))
        {
            var recentMemoryFiles = await LoadDailyMemoryAsync(descriptor, workspacePath, cancellationToken);
            AddContextFilesWithoutDuplicates(contextFiles, recentMemoryFiles);
        }

        // #2846: hooks contribute context files here, BEFORE the owner-private exclusion and
        // BEFORE assembly. Ordering is the whole point: a hook that tries to reintroduce MEMORY.md
        // into a shared conversation has its addition dropped by the filter below, and no private
        // content is ever materialised into prompt text.
        contextFiles = await ApplyContextFileHooksAsync(descriptor, scope, contextFiles, cancellationToken)
            .ConfigureAwait(false);
        contextFiles = FilterOwnerPrivateContextFiles(contextFiles, scope, descriptor.Memory?.Path);

        // Surface the connecting client kind (e.g. SignalR "mobile" vs "desktop") on the runtime
        // line when the execution context carries it. Absent -> null, which the runtime-line
        // builder omits, so desktop / no-hint sessions render an unchanged line (#1209).
        var clientKind = ResolveClientKindParameter(executionContext);

        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = workspacePath,
            ExtraSystemPrompt = descriptor.SystemPrompt,
            ContextFiles = contextFiles,
            Runtime = new RuntimeInfo
            {
                AgentId = descriptor.AgentId.Value,
                Host = Environment.MachineName,
                Os = Environment.OSVersion.ToString(),
                // #2796: the provider/model reported here are the ALREADY-RESOLVED effective values
                // threaded in from the isolation strategy, never a second derivation from the
                // descriptor. Descriptor fields are the fallback only when no run context supplied
                // settings at all (descriptor-only prompt callers).
                Provider = effectiveSettings?.Provider ?? descriptor.ApiProvider,
                Model = effectiveSettings?.Model ?? descriptor.ModelId,
                DefaultModel = effectiveSettings?.DivergentDescriptorDefaultModel,
                ContextWindow = effectiveSettings?.ContextWindow,
                Channel = "signalr",
                ClientKind = clientKind,
                SessionId = executionContext?.SessionId.Value
            },
            HeartbeatPrompt = descriptor.Heartbeat?.Enabled == true
                ? descriptor.Heartbeat.Prompt ?? "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK."
                : null,
            // #2796: reasoning observability was never populated, so the runtime block always
            // claimed "off". It now carries the same effective thinking level applied to AgentOptions.
            ReasoningLevel = effectiveSettings?.ThinkingWireToken,
            MemoryPromptInjection = memoryPromptInjection,
            Scope = scope,
            ConversationContext = ToConversationContext(conversation, runStartedAt),
            PromptMode = PromptMode.Full
        });

        // Dispatch BeforePromptBuild hooks (e.g. skills injection)
        if (_hookDispatcher is not null)
        {
            var hookEvent = new BeforePromptBuildEvent(descriptor.AgentId, descriptor, prompt, []);
            var results = await _hookDispatcher
                .DispatchAsync<BeforePromptBuildEvent, BeforePromptBuildResult>(hookEvent, cancellationToken)
                .ConfigureAwait(false);
            prompt = MergeHookResults(prompt, results);
        }

        return prompt;
    }

    /// <summary>
    /// Builds a descriptor-only prompt for callers that do not have a runtime session context.
    /// Conversation-scoped prompt context requires the overload that accepts an <see cref="AgentExecutionContext"/>.
    /// </summary>
    public Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
        => BuildSystemPromptAsync(descriptor, null, null, cancellationToken);

    /// <summary>
    /// Reads the connecting client kind from the execution-context parameter bag, if present.
    /// Returns <see langword="null"/> when no kind was carried so the runtime-line builder omits
    /// the field for desktop / no-hint sessions (#1209 AC#5).
    /// </summary>
    /// <param name="executionContext">The execution context, or <see langword="null"/>.</param>
    /// <returns>The client kind string, or <see langword="null"/> when absent.</returns>
    private static string? ResolveClientKindParameter(AgentExecutionContext? executionContext)
    {
        if (executionContext is not null
            && executionContext.Parameters.TryGetValue("clientKind", out var raw)
            && raw is string clientKind
            && !string.IsNullOrWhiteSpace(clientKind))
        {
            return clientKind;
        }

        return null;
    }

    private async Task<Conversation?> ResolveConversationAsync(
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
            if (session is not null && session.ConversationId.IsInitialized())
                conversation = await _conversationStore.GetAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);
        }

        if (conversation is null)
        {
            var conversations = await _conversationStore.ListAsync(descriptor.AgentId, cancellationToken).ConfigureAwait(false);
            conversation = conversations.FirstOrDefault(candidate => candidate.ActiveSessionId == executionContext.SessionId);
        }

        return conversation;
    }

    /// <summary>
    /// Start of the current run, for a session that IS one run of a recurring job (#2984).
    /// </summary>
    /// <remarks>
    /// A cron job gets a fresh session per run but reuses one durable conversation, so the session's own
    /// creation time IS the run boundary. Read from the session rather than parsed out of the session-id
    /// string: the id format (<c>cron:{jobId}:{timestamp}:{guid}</c>, with a legacy jobId-less variant) is
    /// an internal encoding, and a parser over it would silently start returning null the day it changes.
    /// Returns null for every non-cron session, which keeps interactive prompts byte-identical.
    /// </remarks>
    private async Task<DateTimeOffset?> ResolveRunStartedAtAsync(
        AgentExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        if (_sessionStore is null || executionContext is null || !executionContext.SessionId.IsCron)
            return null;

        var session = await _sessionStore.GetAsync(executionContext.SessionId, cancellationToken).ConfigureAwait(false);
        return session?.CreatedAt;
    }

    private static ConversationContext? ToConversationContext(Conversation? conversation, DateTimeOffset? runStartedAt = null)
        => conversation is null
            ? null
            : new ConversationContext(conversation.ConversationId.Value, conversation.Title, conversation.Purpose, conversation.Instructions, conversation.TodoJson, runStartedAt);

    /// <summary>
    /// Escalates the caller-supplied scope to <see cref="ConversationScope.Shared"/> when the
    /// resolved conversation carries a participant that is neither the owning agent nor a single
    /// human counterpart (issue #2846).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Escalation only. A caller that already knows the conversation is shared (a federation entry
    /// point, say) is never downgraded to private by a participant list that has not been
    /// backfilled yet, so this cannot reopen the disclosure it exists to close.
    /// </para>
    /// <para>
    /// "Non-owner" means: any agent participant other than the conversation's owning agent, or more
    /// than one distinct human participant. A single human plus the owning agent is the classic
    /// one-to-one channel the private file set was designed for. An empty participant list — the
    /// shape of every legacy row written before participants were tracked — stays private, which
    /// is what keeps the default path byte-identical.
    /// </para>
    /// </remarks>
    private static ConversationScope ResolveEffectiveScope(ConversationScope requestedScope, Conversation? conversation)
    {
        if (requestedScope == ConversationScope.Shared || conversation is null)
            return requestedScope;

        return HasNonOwnerParticipants(conversation) ? ConversationScope.Shared : requestedScope;
    }

    /// <summary>
    /// True when the conversation's participant set extends past the owning agent and one human.
    /// </summary>
    internal static bool HasNonOwnerParticipants(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var participants = conversation.Participants;
        if (participants.Count == 0)
            return false;

        var distinctHumans = participants
            .Where(static participant => participant.CitizenId.Kind == CitizenKind.User)
            .Select(static participant => participant.CitizenId.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctHumans > 1)
            return true;

        return participants.Any(participant =>
            participant.CitizenId.Kind == CitizenKind.Agent
            && !string.Equals(participant.CitizenId.Value, conversation.AgentId.Value, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Appends <paramref name="additions"/> to <paramref name="contextFiles"/>, skipping any whose
    /// normalized path is already present. A daily note listed explicitly in <c>systemPromptFiles</c>
    /// is loaded by the prompt-file pass and would otherwise be emitted twice.
    /// </summary>
    private static void AddContextFilesWithoutDuplicates(List<ContextFile> contextFiles, IReadOnlyList<ContextFile> additions)
    {
        if (additions.Count == 0)
            return;

        var seen = contextFiles
            .Select(file => ContextFileOrdering.NormalizePath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var addition in additions)
        {
            if (seen.Add(ContextFileOrdering.NormalizePath(addition.Path)))
                contextFiles.Add(addition);
        }
    }

    /// <summary>
    /// Dispatches <see cref="BeforeContextFilesBuildEvent"/> and appends whatever handlers
    /// contribute. Runs BEFORE <see cref="FilterOwnerPrivateContextFiles"/> so hook additions are
    /// subject to the same owner-private exclusion as the loaded set (#2846 AC#4).
    /// </summary>
    private async Task<List<ContextFile>> ApplyContextFileHooksAsync(
        AgentDescriptor descriptor,
        ConversationScope scope,
        List<ContextFile> contextFiles,
        CancellationToken cancellationToken)
    {
        if (_hookDispatcher is null)
            return contextFiles;

        var hookEvent = new BeforeContextFilesBuildEvent(
            descriptor.AgentId,
            descriptor,
            scope,
            [.. contextFiles.Select(static file => new PromptContextFile(file.Path, file.Content))]);

        var results = await _hookDispatcher
            .DispatchAsync<BeforeContextFilesBuildEvent, BeforeContextFilesBuildResult>(hookEvent, cancellationToken)
            .ConfigureAwait(false);

        var additions = results
            .SelectMany(static result => result.AdditionalContextFiles)
            .Where(static file => !string.IsNullOrWhiteSpace(file.Path) && !string.IsNullOrWhiteSpace(file.Content))
            .Select(static file => new ContextFile(file.Path, file.Content))
            .ToList();

        AddContextFilesWithoutDuplicates(contextFiles, additions);
        return contextFiles;
    }

    /// <summary>
    /// Removes owner-private context files when the conversation is shared (#2846).
    /// </summary>
    /// <remarks>
    /// Applied to the FILE SET, never to assembled prompt text: a post-hoc string filter would
    /// have to have materialised the private content first, and could not tell a memory heading
    /// apart from an agent legitimately quoting one.
    /// </remarks>
    /// <param name="contextFiles">The candidate context files, hook additions included.</param>
    /// <param name="scope">The conversation scope; <see cref="ConversationScope.Private"/> is a no-op.</param>
    /// <param name="memoryPathOverride">The agent's configured memory root, when overridden.</param>
    private static List<ContextFile> FilterOwnerPrivateContextFiles(
        List<ContextFile> contextFiles,
        ConversationScope scope,
        string? memoryPathOverride)
    {
        if (scope != ConversationScope.Shared)
            return contextFiles;

        return [.. contextFiles.Where(file => !IsOwnerPrivateContextFile(file.Path, memoryPathOverride))];
    }

    /// <summary>
    /// True when a context-file path denotes owner-private content: the named private files
    /// (<c>MEMORY.md</c>, <c>USER.md</c>) anywhere in the workspace, or any note under the agent's
    /// memory root (daily notes are as private as the consolidated file they feed).
    /// </summary>
    private static bool IsOwnerPrivateContextFile(string? path, string? memoryPathOverride)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = ContextFileOrdering.NormalizePath(path);
        var fileName = Path.GetFileName(normalized);

        if (OwnerPrivateFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            return true;

        var memoryRoot = string.IsNullOrWhiteSpace(memoryPathOverride)
            ? "memory"
            : memoryPathOverride.Trim().Replace('\\', '/').TrimEnd('/');
        if (memoryRoot.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            memoryRoot = Path.GetDirectoryName(memoryRoot)?.Replace('\\', '/') ?? "memory";

        return !string.IsNullOrWhiteSpace(memoryRoot)
            && normalized.StartsWith(memoryRoot + "/", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Loads daily memory context, delegating to IAgentMemory when available,
    /// falling back to direct file I/O for backward compatibility.
    /// </summary>
    private async Task<IReadOnlyList<ContextFile>> LoadDailyMemoryAsync(
        AgentDescriptor descriptor,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        if (_agentMemoryFactory is not null)
        {
            try
            {
                var agentMemory = _agentMemoryFactory.Create(descriptor.AgentId.Value);
                var request = new AgentMemoryPromptRequest(descriptor.AgentId.Value);
                var context = await agentMemory.GetPromptContextAsync(request, cancellationToken).ConfigureAwait(false);
                return MapMemoryContextToFiles(context, descriptor.Memory?.Path);
            }
            catch (NotSupportedException)
            {
                // Provider not registered — fall through to file-based loading
            }
        }

        return await LoadRecentDailyMemoryFilesAsync(_fileSystem, workspacePath, descriptor.Memory?.Path, cancellationToken);
    }

    /// <summary>
    /// Maps an <see cref="AgentMemoryContext"/> to context files compatible with the prompt pipeline.
    /// </summary>
    private static IReadOnlyList<ContextFile> MapMemoryContextToFiles(AgentMemoryContext context, string? memoryPathOverride)
    {
        var memoryDir = string.IsNullOrWhiteSpace(memoryPathOverride) ? "memory" : memoryPathOverride.Trim().Replace('\\', '/');
        if (memoryDir.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            memoryDir = Path.GetDirectoryName(memoryDir)?.Replace('\\', '/') ?? "memory";

        List<ContextFile> result = [];
        foreach (var note in context.DailyNotes)
        {
            if (!string.IsNullOrWhiteSpace(note.Content))
            {
                var relativePath = $"{memoryDir}/{note.Date:yyyy-MM-dd}.md";
                result.Add(new ContextFile(relativePath, note.Content.Trim()));
            }
        }

        return result;
    }

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
