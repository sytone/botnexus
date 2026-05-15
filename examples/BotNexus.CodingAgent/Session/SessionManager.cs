using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Abstractions;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Sessions;

namespace BotNexus.CodingAgent.Session;

public sealed class SessionManager
{
    private const int CurrentSessionVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly IFileSystem _fileSystem;

    public SessionManager(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public async Task<SessionInfo> CreateSessionAsync(string workingDir, string? name, string? parentSessionId = null)
    {
        var root = GetSessionsRoot(workingDir);
        var id = GenerateSessionId();
        var now = DateTimeOffset.UtcNow;
        var normalizedWorkingDirectory = Path.GetFullPath(workingDir);
        var normalizedName = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        var filePath = Path.Combine(root, $"{id}.jsonl");

        var header = new SessionHeaderEntry(
            Type: "session_header",
            Version: CurrentSessionVersion,
            SessionId: id,
            Name: normalizedName,
            WorkingDirectory: normalizedWorkingDirectory,
            CreatedAt: now,
            UpdatedAt: now,
            ParentSessionId: parentSessionId);

        await WriteEntriesAsync(filePath, [header]).ConfigureAwait(false);
        await SessionMetadataSidecar.WriteAsync(
            _fileSystem,
            GetMetaPath(filePath),
            new SessionMetadata(
                header.Version,
                header.SessionId,
                header.Name,
                header.WorkingDirectory,
                header.CreatedAt,
                header.UpdatedAt,
                header.ParentSessionId,
                header.Model,
                header.Provider,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            JsonOptions).ConfigureAwait(false);

        return new SessionInfo(
            Id: id,
            Name: normalizedName,
            CreatedAt: now,
            UpdatedAt: now,
            MessageCount: 0,
            Model: null,
            WorkingDirectory: normalizedWorkingDirectory,
            Version: CurrentSessionVersion,
            ParentSessionId: parentSessionId,
            ActiveLeafId: null,
            SessionFilePath: filePath);
    }

    public async Task SaveSessionAsync(SessionInfo session, IReadOnlyList<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadSessionStateAsync(session.Id, session.WorkingDirectory, session.SessionFilePath).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var targetLeaf = session.ActiveLeafId;
        if (!string.IsNullOrWhiteSpace(targetLeaf) && state.Entries.All(entry => entry.EntryId != targetLeaf))
        {
            throw new InvalidOperationException($"Leaf '{targetLeaf}' does not exist in session '{session.Id}'.");
        }

        var branchPath = GetBranchPath(state, targetLeaf ?? state.ActiveLeafId);
        var existingBranchMessages = branchPath
            .Select(MapEntryToAgentMessage)
            .Where(static message => message is not null)
            .Cast<AgentMessage>()
            .ToList();

        var commonPrefixCount = GetCommonPrefixLength(existingBranchMessages, messages);
        var parentId = commonPrefixCount > 0 ? branchPath[commonPrefixCount - 1].EntryId : null;

        for (var i = commonPrefixCount; i < messages.Count; i++)
        {
            var entry = CreateEntry(messages[i], parentId);
            state.Entries.Add(entry);
            parentId = entry.EntryId;
        }

        state.ActiveLeafId = parentId;
        if (!string.Equals(state.LastPersistedLeafId, state.ActiveLeafId, StringComparison.Ordinal))
        {
            state.MetadataEntries.Add(new MetadataEntry(
                Type: "metadata",
                Timestamp: now,
                Key: "leaf",
                Value: state.ActiveLeafId));
            state.LastPersistedLeafId = state.ActiveLeafId;
        }

        state.Header = state.Header with
        {
            Name = session.Name,
            UpdatedAt = now,
            Model = session.Model,
            Provider = session.Provider
        };

        await PersistStateAsync(state).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SessionInfo> WriteMetadataAsync(SessionInfo session, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Metadata key cannot be empty.", nameof(key));
        }

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadSessionStateAsync(session.Id, session.WorkingDirectory, session.SessionFilePath).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            state.MetadataEntries.Add(new MetadataEntry(
                Type: "metadata",
                Timestamp: now,
                Key: key.Trim(),
                Value: value));

            state.Header = state.Header with
            {
                Name = session.Name,
                UpdatedAt = now,
                Model = session.Model,
                Provider = session.Provider
            };

            await PersistStateAsync(state).ConfigureAwait(false);
            return session with { UpdatedAt = now };
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<(SessionInfo Session, IReadOnlyList<AgentMessage> Messages)> ResumeSessionAsync(string sessionId, string workingDir)
    {
        var state = await LoadSessionStateAsync(sessionId, workingDir).ConfigureAwait(false);
        var messages = GetBranchPath(state, state.ActiveLeafId)
            .Select(MapEntryToAgentMessage)
            .Where(static message => message is not null)
            .Cast<AgentMessage>()
            .ToList();

        var session = new SessionInfo(
            Id: state.Header.SessionId,
            Name: state.Header.Name,
            CreatedAt: state.Header.CreatedAt,
            UpdatedAt: state.Header.UpdatedAt,
            MessageCount: messages.Count,
            Model: state.Header.Model,
            WorkingDirectory: state.Header.WorkingDirectory,
            Version: state.Header.Version,
            ParentSessionId: state.Header.ParentSessionId,
            ActiveLeafId: state.ActiveLeafId,
            SessionFilePath: state.FilePath,
            Provider: state.Header.Provider);

        return (session, messages);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string workingDir)
    {
        var root = GetSessionsRoot(workingDir);
        if (!_fileSystem.Directory.Exists(root))
        {
            return [];
        }

        var sessions = new List<SessionInfo>();
        var sessionFiles = _fileSystem.Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
        foreach (var file in sessionFiles)
        {
            var state = await LoadJsonlStateAsync(file).ConfigureAwait(false);
            if (state is null)
            {
                continue;
            }

            var branchMessages = GetBranchPath(state, state.ActiveLeafId)
                .Select(MapEntryToAgentMessage)
                .Count(static message => message is not null);

            sessions.Add(new SessionInfo(
                Id: state.Header.SessionId,
                Name: state.Header.Name,
                CreatedAt: state.Header.CreatedAt,
                UpdatedAt: state.Header.UpdatedAt,
                MessageCount: branchMessages,
                Model: state.Header.Model,
                WorkingDirectory: state.Header.WorkingDirectory,
                Version: state.Header.Version,
                ParentSessionId: state.Header.ParentSessionId,
                ActiveLeafId: state.ActiveLeafId,
                SessionFilePath: state.FilePath,
                Provider: state.Header.Provider));
        }

        foreach (var directory in _fileSystem.Directory.EnumerateDirectories(root))
        {
            var metadataPath = Path.Combine(directory, "session.json");
            if (!_fileSystem.File.Exists(metadataPath))
            {
                continue;
            }

            var id = Path.GetFileName(directory);
            if (sessions.Any(existing => string.Equals(existing.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }

            var json = await _fileSystem.File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<SessionInfo>(json, JsonOptions);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions
            .OrderByDescending(session => session.UpdatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<SessionBranchInfo>> ListBranchesAsync(string sessionId, string workingDir)
    {
        var state = await LoadSessionStateAsync(sessionId, workingDir).ConfigureAwait(false);
        var childParents = state.Entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.ParentEntryId))
            .Select(entry => entry.ParentEntryId!)
            .ToHashSet(StringComparer.Ordinal);

        var leaves = state.Entries
            .Where(entry => !childParents.Contains(entry.EntryId))
            .ToList();

        var branches = new List<SessionBranchInfo>(leaves.Count);
        foreach (var leaf in leaves)
        {
            var path = GetBranchPath(state, leaf.EntryId);
            var messageCount = path.Count(static entry =>
                entry.Type is "message" or "tool_result" or "compaction_summary");
            state.BranchNames.TryGetValue(leaf.EntryId, out var explicitName);
            var name = string.IsNullOrWhiteSpace(explicitName)
                ? $"branch-{leaf.EntryId[..Math.Min(8, leaf.EntryId.Length)]}"
                : explicitName;

            branches.Add(new SessionBranchInfo(
                LeafEntryId: leaf.EntryId,
                Name: name,
                IsActive: string.Equals(leaf.EntryId, state.ActiveLeafId, StringComparison.Ordinal),
                MessageCount: messageCount,
                UpdatedAt: leaf.Timestamp));
        }

        return branches
            .OrderByDescending(branch => branch.UpdatedAt)
            .ToList();
    }

    public async Task<SessionInfo> SwitchBranchAsync(
        string sessionId,
        string workingDir,
        string leafEntryId,
        string? branchName = null)
    {
        if (string.IsNullOrWhiteSpace(leafEntryId))
        {
            throw new ArgumentException("Leaf entry id is required.", nameof(leafEntryId));
        }

        var state = await LoadSessionStateAsync(sessionId, workingDir).ConfigureAwait(false);
        if (state.Entries.All(entry => !string.Equals(entry.EntryId, leafEntryId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Leaf '{leafEntryId}' does not exist in session '{sessionId}'.");
        }

        state.ActiveLeafId = leafEntryId;
        var now = DateTimeOffset.UtcNow;
        state.MetadataEntries.Add(new MetadataEntry("metadata", now, "leaf", leafEntryId));
        state.LastPersistedLeafId = leafEntryId;

        if (!string.IsNullOrWhiteSpace(branchName))
        {
            state.MetadataEntries.Add(new MetadataEntry("metadata", now, $"branch_name:{leafEntryId}", branchName.Trim()));
            state.BranchNames[leafEntryId] = branchName.Trim();
        }

        state.Header = state.Header with { UpdatedAt = now };
        await PersistStateAsync(state).ConfigureAwait(false);

        var messageCount = GetBranchPath(state, state.ActiveLeafId)
            .Count(static entry => entry.Type is "message" or "tool_result" or "compaction_summary");

        return new SessionInfo(
            Id: state.Header.SessionId,
            Name: state.Header.Name,
            CreatedAt: state.Header.CreatedAt,
            UpdatedAt: state.Header.UpdatedAt,
            MessageCount: messageCount,
            Model: null,
            WorkingDirectory: state.Header.WorkingDirectory,
            Version: state.Header.Version,
            ParentSessionId: state.Header.ParentSessionId,
            ActiveLeafId: state.ActiveLeafId,
            SessionFilePath: state.FilePath);
    }

    public Task DeleteSessionAsync(string sessionId, string workingDir)
    {
        var root = GetSessionsRoot(workingDir);
        var filePath = Path.Combine(root, $"{sessionId}.jsonl");
        if (_fileSystem.File.Exists(filePath))
        {
            _fileSystem.File.Delete(filePath);
        }
        var metadataPath = GetMetaPath(filePath);
        if (_fileSystem.File.Exists(metadataPath))
        {
            _fileSystem.File.Delete(metadataPath);
        }

        var legacyDirectory = Path.Combine(root, sessionId);
        if (_fileSystem.Directory.Exists(legacyDirectory))
        {
            _fileSystem.Directory.Delete(legacyDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<SessionState> LoadSessionStateAsync(string sessionId, string workingDir, string? preferredPath = null)
    {
        var root = GetSessionsRoot(workingDir);
        var sessionFile = !string.IsNullOrWhiteSpace(preferredPath) ? preferredPath : Path.Combine(root, $"{sessionId}.jsonl");

        if (!string.IsNullOrWhiteSpace(sessionFile) && _fileSystem.File.Exists(sessionFile))
        {
            var state = await LoadJsonlStateAsync(sessionFile).ConfigureAwait(false);
            if (state is not null)
            {
                return state;
            }
        }

        var legacyDirectory = Path.Combine(root, sessionId);
        var legacyMetadataPath = Path.Combine(legacyDirectory, "session.json");
        var legacyMessagesPath = Path.Combine(legacyDirectory, "messages.jsonl");
        if (!_fileSystem.File.Exists(legacyMetadataPath))
        {
            throw new FileNotFoundException($"Session '{sessionId}' does not exist.", legacyMetadataPath);
        }

        var metadataJson = await _fileSystem.File.ReadAllTextAsync(legacyMetadataPath).ConfigureAwait(false);
        var legacySession = JsonSerializer.Deserialize<SessionInfo>(metadataJson, JsonOptions)
            ?? throw new InvalidOperationException($"Session metadata is invalid for '{sessionId}'.");

        var entries = new List<SessionEntryBase>();
        string? parentId = null;
        if (_fileSystem.File.Exists(legacyMessagesPath))
        {
            var lines = await _fileSystem.File.ReadAllLinesAsync(legacyMessagesPath).ConfigureAwait(false);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(line, JsonOptions)
                    ?? throw new InvalidOperationException($"Invalid message entry in session '{sessionId}'.");
                var message = DeserializeMessage(envelope);
                var entry = CreateEntry(message, parentId);
                entries.Add(entry);
                parentId = entry.EntryId;
            }
        }

        return new SessionState
        {
            Header = new SessionHeaderEntry(
                Type: "session_header",
                Version: CurrentSessionVersion,
                SessionId: legacySession.Id,
                Name: legacySession.Name,
                WorkingDirectory: legacySession.WorkingDirectory,
                CreatedAt: legacySession.CreatedAt,
                UpdatedAt: legacySession.UpdatedAt,
                ParentSessionId: legacySession.ParentSessionId),
            Entries = entries,
            MetadataEntries = [],
            ActiveLeafId = parentId,
            LastPersistedLeafId = parentId,
            BranchNames = new Dictionary<string, string>(StringComparer.Ordinal),
            FilePath = Path.Combine(root, $"{legacySession.Id}.jsonl")
        };
    }

    private async Task<SessionState?> LoadJsonlStateAsync(string filePath)
    {
        var payloads = await SessionJsonl.ReadAllAsync<JsonElement>(
            _fileSystem,
            filePath,
            JsonOptions).ConfigureAwait(false);

        if (payloads.Count == 0)
        {
            return null;
        }

        SessionHeaderEntry? header = null;
        var entries = new List<SessionEntryBase>();
        var metadataEntries = new List<MetadataEntry>();
        var branchNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? activeLeafId = null;

        foreach (var payload in payloads)
        {
            if (!payload.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            var line = payload.GetRawText();
            switch (type)
            {
                case "session_header":
                    header = JsonSerializer.Deserialize<SessionHeaderEntry>(line, JsonOptions);
                    break;
                case "metadata":
                {
                    var metadata = JsonSerializer.Deserialize<MetadataEntry>(line, JsonOptions);
                    if (metadata is null)
                    {
                        break;
                    }

                    metadataEntries.Add(metadata);
                    if (string.Equals(metadata.Key, "leaf", StringComparison.Ordinal))
                    {
                        activeLeafId = metadata.Value;
                    }
                    else if (metadata.Key.StartsWith("branch_name:", StringComparison.Ordinal))
                    {
                        var branchLeaf = metadata.Key["branch_name:".Length..];
                        if (!string.IsNullOrWhiteSpace(branchLeaf) && !string.IsNullOrWhiteSpace(metadata.Value))
                        {
                            branchNames[branchLeaf] = metadata.Value;
                        }
                    }

                    break;
                }
                case "message":
                {
                    var entry = JsonSerializer.Deserialize<MessageEntry>(line, JsonOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }

                    break;
                }
                case "tool_result":
                {
                    var entry = JsonSerializer.Deserialize<ToolResultEntry>(line, JsonOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }

                    break;
                }
                case "compaction_summary":
                {
                    var entry = JsonSerializer.Deserialize<CompactionSummaryEntry>(line, JsonOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }

                    break;
                }
            }
        }

        if (header is null)
        {
            var sidecar = await SessionMetadataSidecar.ReadAsync<SessionMetadata>(
                _fileSystem,
                GetMetaPath(filePath),
                JsonOptions).ConfigureAwait(false);
            if (sidecar is null)
            {
                return null;
            }

            header = new SessionHeaderEntry(
                Type: "session_header",
                Version: sidecar.Version,
                SessionId: sidecar.SessionId,
                Name: sidecar.Name,
                WorkingDirectory: sidecar.WorkingDirectory,
                CreatedAt: sidecar.CreatedAt,
                UpdatedAt: sidecar.UpdatedAt,
                ParentSessionId: sidecar.ParentSessionId,
                Model: sidecar.Model,
                Provider: sidecar.Provider);

            activeLeafId ??= sidecar.ActiveLeafId;
            foreach (var branch in sidecar.BranchNames)
            {
                branchNames[branch.Key] = branch.Value;
            }
        }

        activeLeafId ??= entries.LastOrDefault()?.EntryId;
        return new SessionState
        {
            Header = header,
            Entries = entries,
            MetadataEntries = metadataEntries,
            ActiveLeafId = activeLeafId,
            LastPersistedLeafId = activeLeafId,
            BranchNames = branchNames,
            FilePath = filePath
        };
    }

    private async Task PersistStateAsync(SessionState state)
    {
        state.Header = state.Header with
        {
            Version = Math.Max(state.Header.Version, CurrentSessionVersion)
        };

        var allEntries = new List<object>(1 + state.Entries.Count + state.MetadataEntries.Count)
        {
            state.Header
        };
        allEntries.AddRange(state.Entries.OrderBy(entry => entry.Timestamp));
        allEntries.AddRange(state.MetadataEntries.OrderBy(entry => entry.Timestamp));

        await WriteEntriesAsync(state.FilePath, allEntries).ConfigureAwait(false);
        await WriteMetadataSidecarAsync(state).ConfigureAwait(false);
    }

    private async Task WriteEntriesAsync(string filePath, IEnumerable<object> entries)
    {
        await SessionJsonl.WriteAllAsync(
            _fileSystem,
            filePath,
            entries,
            JsonOptions).ConfigureAwait(false);
    }

    private async Task WriteMetadataSidecarAsync(SessionState state)
    {
        var metadata = new SessionMetadata(
            state.Header.Version,
            state.Header.SessionId,
            state.Header.Name,
            state.Header.WorkingDirectory,
            state.Header.CreatedAt,
            state.Header.UpdatedAt,
            state.Header.ParentSessionId,
            state.Header.Model,
            state.Header.Provider,
            state.ActiveLeafId,
            new Dictionary<string, string>(state.BranchNames, StringComparer.Ordinal));

        await SessionMetadataSidecar.WriteAsync(
            _fileSystem,
            GetMetaPath(state.FilePath),
            metadata,
            JsonOptions).ConfigureAwait(false);
    }

    private static SessionEntryBase CreateEntry(AgentMessage message, string? parentEntryId)
    {
        var entryId = GenerateEntryId();
        var timestamp = DateTimeOffset.UtcNow;

        return message switch
        {
            ToolResultAgentMessage toolResult => new ToolResultEntry(
                Type: "tool_result",
                EntryId: entryId,
                ParentEntryId: parentEntryId,
                Timestamp: timestamp,
                Message: SerializeMessage(toolResult)),
            SystemAgentMessage system when IsCompactionSummary(system.Content) => new CompactionSummaryEntry(
                Type: "compaction_summary",
                EntryId: entryId,
                ParentEntryId: parentEntryId,
                Timestamp: timestamp,
                Summary: system.Content),
            _ => new MessageEntry(
                Type: "message",
                EntryId: entryId,
                ParentEntryId: parentEntryId,
                Timestamp: timestamp,
                Message: SerializeMessage(message))
        };
    }

    private static bool IsCompactionSummary(string content)
    {
        return content.Contains("[Session context summary:", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<read-files>", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<modified-files>", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentMessage? MapEntryToAgentMessage(SessionEntryBase entry)
    {
        return entry switch
        {
            MessageEntry messageEntry => DeserializeMessage(messageEntry.Message),
            ToolResultEntry toolResultEntry => DeserializeMessage(toolResultEntry.Message),
            CompactionSummaryEntry summaryEntry => new SystemAgentMessage(summaryEntry.Summary),
            _ => null
        };
    }

    private static List<SessionEntryBase> GetBranchPath(SessionState state, string? leafEntryId)
    {
        var byId = state.Entries.ToDictionary(entry => entry.EntryId, StringComparer.Ordinal);
        var path = new List<SessionEntryBase>();
        var currentId = leafEntryId;

        while (!string.IsNullOrWhiteSpace(currentId) && byId.TryGetValue(currentId, out var current))
        {
            path.Add(current);
            currentId = current.ParentEntryId;
        }

        path.Reverse();
        return path;
    }

    private static int GetCommonPrefixLength(IReadOnlyList<AgentMessage> existingMessages, IReadOnlyList<AgentMessage> incomingMessages)
    {
        var length = Math.Min(existingMessages.Count, incomingMessages.Count);
        for (var i = 0; i < length; i++)
        {
            if (!MessagesEqual(existingMessages[i], incomingMessages[i]))
            {
                return i;
            }
        }

        return length;
    }

    private static bool MessagesEqual(AgentMessage left, AgentMessage right)
    {
        if (!MessageRole.FromString(left.Role).Equals(MessageRole.FromString(right.Role)))
        {
            return false;
        }

        var leftJson = JsonSerializer.Serialize(left, left.GetType(), JsonOptions);
        var rightJson = JsonSerializer.Serialize(right, right.GetType(), JsonOptions);
        return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
    }

    private string GetSessionsRoot(string workingDir)
    {
        var config = CodingAgentConfig.Load(_fileSystem, workingDir);
        _fileSystem.Directory.CreateDirectory(config.SessionsDirectory);
        return config.SessionsDirectory;
    }

    private static string GenerateSessionId()
    {
        var now = DateTime.UtcNow;
        var bytes = RandomNumberGenerator.GetBytes(2);
        var suffix = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{now:yyyyMMdd-HHmmss}-{suffix}";
    }

    private static string GenerateEntryId() => Guid.NewGuid().ToString("N")[..8];

    private static MessageEnvelope SerializeMessage(AgentMessage message)
    {
        return message switch
        {
            UserMessage user => new MessageEnvelope(MessageRole.User, JsonSerializer.SerializeToElement(user, JsonOptions)),
            AssistantAgentMessage assistant => new MessageEnvelope(MessageRole.Assistant, JsonSerializer.SerializeToElement(assistant, JsonOptions)),
            ToolResultAgentMessage tool => new MessageEnvelope(MessageRole.Tool, JsonSerializer.SerializeToElement(tool, JsonOptions)),
            SystemAgentMessage system => new MessageEnvelope(MessageRole.System, JsonSerializer.SerializeToElement(system, JsonOptions)),
            _ => throw new NotSupportedException($"Unsupported message type: {message.GetType().Name}")
        };
    }

    private static AgentMessage DeserializeMessage(MessageEnvelope envelope)
    {
        return envelope.Type switch
        {
            var role when role == MessageRole.User => envelope.Payload.Deserialize<UserMessage>(JsonOptions)
                      ?? throw new InvalidOperationException("Invalid user message payload."),
            var role when role == MessageRole.Assistant => envelope.Payload.Deserialize<AssistantAgentMessage>(JsonOptions)
                            ?? throw new InvalidOperationException("Invalid assistant message payload."),
            var role when role == MessageRole.Tool => envelope.Payload.Deserialize<ToolResultAgentMessage>(JsonOptions)
                       ?? throw new InvalidOperationException("Invalid tool message payload."),
            var role when role == MessageRole.System => envelope.Payload.Deserialize<SystemAgentMessage>(JsonOptions)
                         ?? throw new InvalidOperationException("Invalid system message payload."),
            _ => throw new NotSupportedException($"Unsupported message type '{envelope.Type.Value}'.")
        };
    }

    private static string GetMetaPath(string sessionFilePath)
        => Path.ChangeExtension(sessionFilePath, "meta.json");

    private sealed class SessionState
    {
        public required SessionHeaderEntry Header { get; set; }
        public required List<SessionEntryBase> Entries { get; init; }
        public required List<MetadataEntry> MetadataEntries { get; init; }
        public string? ActiveLeafId { get; set; }
        public string? LastPersistedLeafId { get; set; }
        public required Dictionary<string, string> BranchNames { get; init; }
        public required string FilePath { get; init; }
    }

    private sealed record SessionHeaderEntry(
        string Type,
        int Version,
        string SessionId,
        string Name,
        string WorkingDirectory,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? ParentSessionId,
        string? Model = null,
        string? Provider = null);

    private abstract record SessionEntryBase(
        string Type,
        string EntryId,
        string? ParentEntryId,
        DateTimeOffset Timestamp);

    private sealed record MessageEntry(
        string Type,
        string EntryId,
        string? ParentEntryId,
        DateTimeOffset Timestamp,
        MessageEnvelope Message) : SessionEntryBase(Type, EntryId, ParentEntryId, Timestamp);

    private sealed record ToolResultEntry(
        string Type,
        string EntryId,
        string? ParentEntryId,
        DateTimeOffset Timestamp,
        MessageEnvelope Message) : SessionEntryBase(Type, EntryId, ParentEntryId, Timestamp);

    private sealed record CompactionSummaryEntry(
        string Type,
        string EntryId,
        string? ParentEntryId,
        DateTimeOffset Timestamp,
        string Summary) : SessionEntryBase(Type, EntryId, ParentEntryId, Timestamp);

    private sealed record MetadataEntry(
        string Type,
        DateTimeOffset Timestamp,
        string Key,
        string? Value);

    private sealed record SessionMetadata(
        int Version,
        string SessionId,
        string Name,
        string WorkingDirectory,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? ParentSessionId,
        string? Model,
        string? Provider,
        string? ActiveLeafId,
        Dictionary<string, string> BranchNames);

    private sealed record MessageEnvelope(MessageRole Type, JsonElement Payload);
}
