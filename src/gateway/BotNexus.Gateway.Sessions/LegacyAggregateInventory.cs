using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>A content-free fingerprint. Paths are relative to the explicit session directory.</summary>
public sealed record LegacyInventoryFile(string RelativePath, string Category, long ByteLength, long RecordCount, string Sha256);

/// <summary>Byte inventory only, not an import-ready aggregate or a transactionally consistent snapshot.</summary>
public sealed record LegacyInventoryResult(IReadOnlyList<LegacyInventoryFile> Files, string RetainedSectionsPath);

/// <summary>
/// Dormant, read-only preflight for the legacy file layout. Never constructs a store, creates a
/// directory, opens SQLite, or activates migration. Run against a quiesced offline source.
/// Validates wire shapes, paths and selected identity edges, not the complete decoded semantic graph.
/// </summary>
public sealed class LegacyAggregateInventory(IFileSystem fileSystem)
{
    /// <summary>Maximum bytes per JSON document or JSONL row; oversized sources fail closed.</summary>
    public const int MaxRecordBytes = 4 * 1024 * 1024;
    private const string ReceiptProperty = "subAgentConsumptionReceipt";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 64
    };

    /// <summary>
    /// Inspects sessions, nested conversations/read-state, retained subagent details and spawn
    /// indexes. sections.sqlite is reported under the separate data directory but never accessed.
    /// Any unsupported file, malformed record or unresolved inventory identity fails the whole call.
    /// </summary>
    public Task<LegacyInventoryResult> InspectAsync(string sessionDirectory, string dataDirectory,
        CancellationToken cancellationToken = default) => InspectCoreAsync(sessionDirectory, dataDirectory, false, cancellationToken);
    /// <summary>
    /// Opts into explicit active-session, metadata-agent, cursor-target and retained-run identity
    /// checks in the same byte-inventory pass. Runs are checked only against their recorded
    /// conversation's agent when both exist, never against current session bindings. Missing
    /// historical targets and parent conversation links are not checked. This is not complete
    /// import-readiness certification.
    /// No runtime store, SQLite connection, directory creation or source mutation is involved.
    /// </summary>
    public Task<LegacyInventoryResult> InspectRelationshipsAsync(string sessionDirectory, string dataDirectory,
        CancellationToken cancellationToken = default) => InspectCoreAsync(sessionDirectory, dataDirectory, true, cancellationToken);
    private async Task<LegacyInventoryResult> InspectCoreAsync(string sessionDirectory, string dataDirectory,
        bool inspectRelationships, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var root = fileSystem.Path.GetFullPath(sessionDirectory);
        if (!fileSystem.Directory.Exists(root)) throw new DirectoryNotFoundException("Legacy session directory does not exist.");
        var state = new InventoryState(inspectRelationships);
        var files = new List<LegacyInventoryFile>();
        foreach (var path in Enumerate(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = fileSystem.Path.GetRelativePath(root, path).Replace('\\', '/');
            var source = Classify(relative);
            try
            {
                var fingerprint = await InspectFileAsync(path, relative, source, state, cancellationToken).ConfigureAwait(false);
                files.Add(fingerprint);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException or OverflowException or NotSupportedException)
            {
                // Do not retain parser exceptions: their messages may contain source content.
                throw Invalid(relative);
            }
        }
        state.Validate();
        state.Relationships?.Validate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new LegacyInventoryResult(files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(),
            fileSystem.Path.Combine(fileSystem.Path.GetFullPath(dataDirectory), "sections.sqlite"));
    }

    private IEnumerable<string> Enumerate(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if ((fileSystem.File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Legacy inventory refuses linked directories.");
        foreach (var path in fileSystem.Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            if ((fileSystem.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Legacy inventory refuses linked files.");
            yield return path;
        }
        foreach (var child in fileSystem.Directory.EnumerateDirectories(directory))
            foreach (var path in Enumerate(child, ct)) yield return path;
    }

    private sealed record Source(string Category, string Identity, string? Owner = null, string? Archive = null);

    private static Source Classify(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length == 1)
        {
            var name = parts[0];
            string? archive = null;
            var marker = name.LastIndexOf(".archived.", StringComparison.Ordinal);
            // Active IDs can themselves contain '.archived.'. Only treat the marker as a
            // generation suffix when it follows the complete history/metadata extension.
            if (!name.EndsWith(".jsonl", StringComparison.Ordinal) &&
                !name.EndsWith(".meta.json", StringComparison.Ordinal) && marker >= 0 &&
                (name[..marker].EndsWith(".jsonl", StringComparison.Ordinal) ||
                 name[..marker].EndsWith(".meta.json", StringComparison.Ordinal)))
            {
                archive = name[(marker + 10)..];
                if (!DateTime.TryParseExact(archive, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _)) throw Invalid(relative);
                name = name[..marker];
            }
            if (name.EndsWith(".meta.json", StringComparison.Ordinal))
                return new Source("session-metadata", Decode(name[..^10], relative), Archive: archive);
            if (name.EndsWith(".jsonl", StringComparison.Ordinal))
                return new Source("session-history", Decode(name[..^6], relative), Archive: archive);
        }
        if (parts.Length == 2 && parts[0] == "subagents" && parts[1].EndsWith(".json", StringComparison.Ordinal))
            return new Source("subagent-detail", Decode(parts[1][..^5], relative));
        if (parts.Length == 3 && parts[0] == "subagent-spawns" && parts[2].EndsWith(".json", StringComparison.Ordinal))
            return new Source("spawn-index", Decode(parts[2][..^5], relative), Decode(parts[1], relative));
        if (parts.Length == 3 && parts[0] == "conversations" && parts[2].EndsWith(".json", StringComparison.Ordinal))
        {
            if (parts[1] == "read-state") return new Source("read-state", parts[2][..^5]);
            return new Source("conversation", parts[2][..^5], parts[1]);
        }
        throw Invalid(relative);
    }

    private static string Decode(string encoded, string path)
    {
        var decoded = Uri.UnescapeDataString(encoded);
        if (string.IsNullOrWhiteSpace(decoded) || Uri.EscapeDataString(decoded) != encoded) throw Invalid(path);
        return decoded;
    }

    private async Task<LegacyInventoryFile> InspectFileAsync(string path, string relative, Source source,
        InventoryState state, CancellationToken ct)
    {
        await using var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var record = new MemoryStream();
        var buffer = new byte[65536];
        long length = 0;
        long count = 0;
        var lines = source.Category == "session-history";
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
            var start = 0;
            while (start < read)
            {
                ct.ThrowIfCancellationRequested();
                var newline = lines ? Array.IndexOf(buffer, (byte)'\n', start, read - start) : -1;
                var end = newline < 0 ? read : newline;
                if (record.Length + end - start > MaxRecordBytes) throw Invalid(relative);
                record.Write(buffer, start, end - start);
                if (newline >= 0)
                {
                    ValidateRecord(record, source, relative, state);
                    count++;
                    record.SetLength(0);
                }
                start = end + (newline < 0 ? 0 : 1);
            }
        }
        if (!lines || record.Length != 0)
        {
            ValidateRecord(record, source, relative, state);
            count++;
        }
        // Empty history is valid; metadata still has to be paired with it.
        if (lines) state.Histories.Add((source.Identity, source.Archive));
        return new LegacyInventoryFile(relative, source.Category, length, count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void ValidateRecord(MemoryStream bytes, Source source, string path, InventoryState state)
    {
        using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)),
            new JsonDocumentOptions { MaxDepth = 64 });
        var json = document.RootElement;
        RejectDuplicates(json, path);
        if (source.Category == "spawn-index")
        {
            if (json.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(json.GetString())) throw Invalid(path);
            state.Spawns.Add((source.Owner ?? throw Invalid(path), source.Identity, json.GetString() ?? throw Invalid(path)));
            return;
        }
        if (json.ValueKind != JsonValueKind.Object) throw Invalid(path);
        switch (source.Category)
        {
            case "session-history":
                ValidateEntry(json, path);
                break;
            case "session-metadata":
                Require(json, "createdAt", JsonValueKind.String, path);
                Require(json, "updatedAt", JsonValueKind.String, path);
                var meta = json.Deserialize<FileSessionStore.SessionMeta>(Options) ?? throw Invalid(path);
                var legacyBinding = meta.ConversationId;
                if (meta.CreatedAt == default || meta.UpdatedAt == default ||
                    (meta.AgentId is null && legacyBinding is null)) throw Invalid(path);
                if (!state.Metadata.TryAdd((source.Identity, source.Archive), legacyBinding?.Value)) throw Invalid(path);
                state.Relationships?.AddMetadata(source, meta);
                break;
            case "conversation":
                Require(json, "conversationId", JsonValueKind.String, path);
                Require(json, "agentId", JsonValueKind.String, path);
                var conversation = json.Deserialize<Conversation>(Options) ?? throw Invalid(path);
                if (conversation.ConversationId.Value != source.Identity || conversation.AgentId.Value != source.Owner ||
                    !state.Conversations.Add(source.Identity)) throw Invalid(path);
                state.Relationships?.AddConversation(conversation);
                break;
            case "subagent-detail":
                var detail = json.Deserialize<SubAgentRunDetail>(Options) ?? throw Invalid(path);
                if (detail.SubAgentId != source.Identity || !Enum.IsDefined(detail.Status) ||
                    !state.Runs.TryAdd(detail.SubAgentId, (detail.ParentSessionId, detail.SpawningToolCallId))) throw Invalid(path);
                state.Relationships?.AddRun(detail);
                break;
            case "read-state":
                var cursorConversation = ValidateReadState(json, source.Identity, path);
                state.Relationships?.CursorTargets.Add(cursorConversation);
                break;
            default: throw Invalid(path);
        }
    }

    private static void ValidateEntry(JsonElement json, string path)
    {
        Require(json, "role", JsonValueKind.String, path);
        Require(json, "content", JsonValueKind.String, path);
        SessionEntry entry;
        if (json.TryGetProperty(ReceiptProperty, out var marker))
        {
            if (marker.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(marker.GetString())) throw Invalid(path);
            // The sole file-private member is explicitly removed, never generally ignored.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in json.EnumerateObject())
                    if (property.Name != ReceiptProperty) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            entry = JsonSerializer.Deserialize<SessionEntry>(stream.ToArray(), Options) ?? throw Invalid(path);
            if (entry.Kind != MessageKind.ToolResult || entry.Role != MessageRole.Tool ||
                string.IsNullOrWhiteSpace(entry.ToolCallId) || entry.ToolIsError ||
                entry.ToolName is not ("spawn_subagent" or "manage_subagent")) throw Invalid(path);
        }
        else entry = json.Deserialize<SessionEntry>(Options) ?? throw Invalid(path);
        if (entry.Role is null || entry.Content is null) throw Invalid(path);
    }

    // FileConversationReadStateStore's five-field wire record lives in Gateway.Conversations,
    // which is deliberately not a Sessions dependency. Validate its exact primitive wire shape.
    private static string ValidateReadState(JsonElement json, string identity, string path)
    {
        var persisted = json.Deserialize<PersistedReadState>(Options) ?? throw Invalid(path);
        var world = persisted.WorldId;
        var reader = persisted.ReaderId;
        var conversation = persisted.ConversationId;
        if (string.IsNullOrWhiteSpace(world) || world != world.Trim() || string.IsNullOrWhiteSpace(reader) || reader != reader.Trim() ||
            string.IsNullOrWhiteSpace(conversation) || persisted.Position < 0 || persisted.Version < 1) throw Invalid(path);
        _ = ConversationReaderId.From(reader);
        _ = ConversationId.From(conversation);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{world}\0{reader}\0{conversation}")));
        if (identity != expected) throw Invalid(path);
        return conversation;
    }

    // This DTO describes persisted cursor data, never runtime configuration or world derivation.
    private sealed record PersistedReadState
    {
        public required string WorldId { get; init; }
        public required string ReaderId { get; init; }
        public required string ConversationId { get; init; }
        public required long Position { get; init; }
        public required long Version { get; init; }
    }

    private static JsonElement Require(JsonElement json, string name, JsonValueKind kind, string path)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != kind) throw Invalid(path);
        return value;
    }

    private static void RejectDuplicates(JsonElement json, string path)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid(path);
                RejectDuplicates(property.Value, path);
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
            foreach (var item in json.EnumerateArray()) RejectDuplicates(item, path);
    }

    private static InvalidDataException Invalid(string path) => new($"Unsupported or invalid legacy source: {path}");

    // Only scalar identities survive record validation. Never retain decoded conversations,
    // run summaries/results, cursor reader identities or transcript entries in these indexes.
    private sealed record SessionBinding(string? Conversation, string? Agent);
    private sealed record ConversationBinding(string Agent, string? ActiveSession);
    private sealed record RunEndpoint(string? Conversation, string? Agent);
    private sealed class RelationshipState
    {
        private readonly Dictionary<string, SessionBinding> activeSessions = new(StringComparer.Ordinal);
        private readonly List<SessionBinding> metadata = [];
        private readonly Dictionary<string, ConversationBinding> conversations = new(StringComparer.Ordinal);
        private readonly List<RunEndpoint> endpoints = [];
        public HashSet<string> CursorTargets { get; } = new(StringComparer.Ordinal);
        public void AddMetadata(Source source, FileSessionStore.SessionMeta meta)
        {
            var legacyBinding = meta.ConversationId;
            var binding = new SessionBinding(legacyBinding?.Value, meta.AgentId?.Value);
            metadata.Add(binding);
            if (source.Archive is null) activeSessions.Add(source.Identity, binding);
        }
        public void AddConversation(Conversation conversation) => conversations.Add(conversation.ConversationId.Value,
            new ConversationBinding(conversation.AgentId.Value, conversation.ActiveSessionId?.Value));
        public void AddRun(SubAgentRunDetail run)
        {
            endpoints.Add(new RunEndpoint(run.ParentConversationId, run.ParentAgentId));
            endpoints.Add(new RunEndpoint(run.ChildConversationId, run.ChildAgentId));
        }
        public void Validate(CancellationToken ct)
        {
            foreach (var pair in conversations)
            {
                ct.ThrowIfCancellationRequested();
                if (pair.Value.ActiveSession is { } session &&
                    (!activeSessions.TryGetValue(session, out var binding) || binding.Conversation != pair.Key))
                    throw Conflict();
            }
            foreach (var binding in metadata)
            {
                ct.ThrowIfCancellationRequested();
                CheckAgent(binding.Conversation, binding.Agent);
            }
            foreach (var target in CursorTargets)
            {
                ct.ThrowIfCancellationRequested();
                if (!conversations.ContainsKey(target)) throw Conflict();
            }
            foreach (var endpoint in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                // RebindSessionsAsync/SaveAsync can change a session's binding without
                // rewriting retained runs; deletion/archive can remove the target entirely.
                // Only the explicitly recorded conversation/agent pair is comparable:
                // Conversation.AgentId is immutable, unlike the session's current binding.
                CheckAgent(endpoint.Conversation, endpoint.Agent);
            }
        }
        private void CheckAgent(string? conversation, string? agent)
        {
            if (conversation is not null && agent is not null &&
                conversations.TryGetValue(conversation, out var binding) && agent != binding.Agent) throw Conflict();
        }
        // Identity strings and source content must not escape through diagnostics.
        private static InvalidDataException Conflict() => new("Contradictory or unresolved explicit legacy relationship.");
    }
    private sealed class InventoryState(bool inspectRelationships)
    {
        public RelationshipState? Relationships { get; } = inspectRelationships ? new() : null;
        public HashSet<(string Id, string? Archive)> Histories { get; } = [];
        public Dictionary<(string Id, string? Archive), string?> Metadata { get; } = [];
        public HashSet<string> Conversations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (string? Parent, string? Call)> Runs { get; } = new(StringComparer.Ordinal);
        public List<(string Parent, string Call, string Run)> Spawns { get; } = [];

        public void Validate()
        {
            if (!Histories.SetEquals(Metadata.Keys)) throw new InvalidDataException("Unpaired legacy session files.");
            foreach (var conversation in Metadata.Values)
                if (conversation is not null && !Conversations.Contains(conversation))
                    throw new InvalidDataException("Legacy metadata references a missing conversation.");
            var admissions = new HashSet<(string, string)>();
            foreach (var run in Runs.Values)
                if (run.Parent is not null && run.Call is not null && !admissions.Add((run.Parent, run.Call)))
                    throw new InvalidDataException("Duplicate legacy spawn admission.");
            foreach (var spawn in Spawns)
                if (!Runs.TryGetValue(spawn.Run, out var run) || run.Parent != spawn.Parent || run.Call != spawn.Call)
                    throw new InvalidDataException("Unresolved legacy spawn index.");
        }
    }
}
