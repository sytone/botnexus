using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

public sealed partial class FileSessionStore
{
    // These probes bracket the one atomic parent transcript replacement, not two writes.
    internal Func<CancellationToken, Task>? BeforeSubAgentReceiptCommitAsync { get; set; }
    internal Func<Task>? AfterSubAgentReceiptCommitAsync { get; set; }

    // Aggregate-save probes bracket the receipt-preserving history write, after snapshot capture.
    internal Func<CancellationToken, Task>? BeforeReceiptHistorySaveAsync { get; set; }
    internal Func<Task>? AfterReceiptHistorySaveAsync { get; set; }

    private string RunPath(string id) => Path.Combine(_storePath, "subagents", SessionFileNames.SanitizeSessionId(id) + ".json");
    private string SpawnPath(SessionId parent, string call) => Path.Combine(_storePath, "subagent-spawns",
        SessionFileNames.SanitizeSessionId(parent.Value), SessionFileNames.SanitizeSessionId(call) + ".json");

    /// <inheritdoc />
    public override async Task SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadRunAsync(info.SubAgentId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.ParentSessionId != info.ParentSessionId.Value || existing.SpawningToolCallId != info.SpawningToolCallId)
                    throw new UnauthorizedAccessException("Sub-agent admission identity cannot be rebound.");
                return;
            }
            if (info.SpawningToolCallId is { } call)
            {
                var indexed = await ReadSpawnAsync(info.ParentSessionId, call, cancellationToken).ConfigureAwait(false);
                if (indexed is not null && indexed != info.SubAgentId)
                    throw new InvalidOperationException("A different sub-agent already owns this spawn call.");
            }
            // Write the accelerator first. If admission fails, a dangling index is ignored;
            // no retained run is published until the final atomic admission replacement.
            if (info.SpawningToolCallId is { } spawn)
                await AtomicJsonAsync(SpawnPath(info.ParentSessionId, spawn), info.SubAgentId, cancellationToken).ConfigureAwait(false);
            await WriteRunAsync(SubAgentRunDetail.FromLive(info), cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task UpdateSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retained = await ReadRunAsync(info.SubAgentId, cancellationToken).ConfigureAwait(false);
            if (retained is null) throw new InvalidOperationException("Sub-agent admission is missing.");
            if (retained.ParentSessionId != info.ParentSessionId.Value || retained.SpawningToolCallId != info.SpawningToolCallId)
                throw new UnauthorizedAccessException("Sub-agent admission identity cannot be rebound.");
            await WriteRunAsync(SubAgentRunDetail.FromLive(info), cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private Task WriteRunAsync(SubAgentRunDetail detail, CancellationToken ct)
    {
        // Redact the whole bounded projection as well as the consuming result.
        var json = JsonSerializer.Serialize(detail, JsonOptions);
        var safe = JsonSerializer.Deserialize<SubAgentRunDetail>(_redactor?.Redact(json) ?? json, JsonOptions)
            ?? throw new InvalidOperationException("Invalid retained sub-agent detail.");
        return AtomicJsonAsync(RunPath(detail.SubAgentId), safe, ct);
    }

    private async Task AtomicJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null) _fileSystem.Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await _fileSystem.File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _fileSystem.File.Move(temporary, path, overwrite: true);
        }
        finally { if (_fileSystem.File.Exists(temporary)) _fileSystem.File.Delete(temporary); }
    }

    private Task<SubAgentRunDetail?> ReadRunAsync(string id, CancellationToken ct) =>
        SessionMetadataSidecar.ReadAsync<SubAgentRunDetail>(_fileSystem, RunPath(id), JsonOptions, ct);

    private async Task<IReadOnlyList<SubAgentRunDetail>> ReadRunsAsync(CancellationToken ct)
    {
        var directory = Path.Combine(_storePath, "subagents");
        if (!_fileSystem.Directory.Exists(directory)) return [];
        var runs = new List<SubAgentRunDetail>();
        foreach (var path in _fileSystem.Directory.GetFiles(directory, "*.json"))
        {
            var run = await SessionMetadataSidecar.ReadAsync<SubAgentRunDetail>(_fileSystem, path, JsonOptions, ct).ConfigureAwait(false);
            if (run is not null) runs.Add(run);
        }
        return runs;
    }

    private async Task<string?> ReadSpawnAsync(SessionId parent, string call, CancellationToken ct)
    {
        var id = await SessionMetadataSidecar.ReadAsync<string>(_fileSystem, SpawnPath(parent, call), JsonOptions, ct).ConfigureAwait(false);
        if (id is not null && await ReadRunAsync(id, ct).ConfigureAwait(false) is { } indexed
            && indexed.ParentSessionId == parent.Value && indexed.SpawningToolCallId == call) return id;
        return (await ReadRunsAsync(ct).ConfigureAwait(false)).SingleOrDefault(d =>
            d.ParentSessionId == parent.Value && d.SpawningToolCallId == call)?.SubAgentId;
    }

    /// <inheritdoc />
    public override async Task<SubAgentRunDetail?> GetSubAgentSessionAsync(string subAgentId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadRunAsync(subAgentId, cancellationToken).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task<SubAgentRunDetail?> FindSubAgentSpawnAsync(SessionId parentSessionId, string toolCallId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = await ReadSpawnAsync(parentSessionId, toolCallId, cancellationToken).ConfigureAwait(false);
            return id is null ? null : await ReadRunAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SubAgentRunDetail>> ListSubAgentSessionsAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        => (await ListAllSubAgentSessionsAsync(limit: int.MaxValue, cancellationToken: cancellationToken,
            parentSessionId: sessionId.Value).ConfigureAwait(false)).OrderBy(d => d.StartedAt).ToArray();

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SubAgentRunDetail>> ListAllSubAgentSessionsAsync(string? status = null,
        int limit = 200, CancellationToken cancellationToken = default, string? parentSessionId = null,
        string? childAgentId = null, int offset = 0)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadRunsAsync(cancellationToken).ConfigureAwait(false)).Where(d =>
                (string.IsNullOrWhiteSpace(status) || string.Equals(d.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
                && (parentSessionId is null || d.ParentSessionId == parentSessionId)
                && (childAgentId is null || d.ChildAgentId == childAgentId))
                .OrderByDescending(d => d.StartedAt).ThenByDescending(d => d.SubAgentId, StringComparer.Ordinal)
                .Skip(offset).Take(limit).ToArray();
        }
        finally { _lock.Release(); }
    }

    // File-private extra JSON property: ordinary SessionEntry readers ignore it. The original
    // ToolResult and this provenance marker are ONE JSONL row in ONE atomic snapshot commit.
    private const string ReceiptProperty = "subAgentConsumptionReceipt";
    private sealed record RetainedReceipt(string SubAgentId, SessionEntry Entry);

    private async Task<IReadOnlyList<RetainedReceipt>> ReadReceiptsAsync(SessionId parent, CancellationToken ct)
    {
        var path = GetHistoryPath(parent);
        if (!_fileSystem.File.Exists(path)) return [];
        var receipts = new List<RetainedReceipt>();
        foreach (var line in await _fileSystem.File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Ordinary history readers tolerate corrupt JSONL entries. Preserve that behavior,
            // but never silently discard a row that may carry consumption provenance.
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) when (!line.Contains(ReceiptProperty, StringComparison.Ordinal))
            {
                continue;
            }
            using var receiptDocument = document;
            if (!document.RootElement.TryGetProperty(ReceiptProperty, out var marker)) continue;
            var id = marker.GetString() ?? throw new InvalidOperationException("Invalid consumption receipt.");
            var entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOptions)
                ?? throw new InvalidOperationException("Consumption receipt has no original result.");
            if (entry.Kind != MessageKind.ToolResult || string.IsNullOrWhiteSpace(entry.ToolCallId))
                throw new InvalidOperationException("Invalid original consumption result.");
            entry.PersistenceKey = "tool-result:" + entry.ToolCallId;
            receipts.Add(new(id, entry));
        }
        return receipts;
    }

    private Task WriteReceiptHistoryAsync(SessionId parent, IEnumerable<SessionEntry> entries,
        IReadOnlyList<RetainedReceipt> receipts, CancellationToken ct)
    {
        var rows = entries.Select(entry =>
        {
            var node = JsonSerializer.SerializeToNode(entry, JsonOptions) as JsonObject
                ?? throw new InvalidOperationException("Invalid session entry.");
            var receipt = receipts.SingleOrDefault(r => r.Entry.ToolCallId == entry.ToolCallId && entry.Kind == MessageKind.ToolResult);
            if (receipt is not null) node[ReceiptProperty] = receipt.SubAgentId;
            return node;
        });
        return SessionJsonl.WriteAllAsync(_fileSystem, GetHistoryPath(parent), rows, JsonOptions, ct);
    }

    /// <inheritdoc />
    public override async Task<string?> ConsumeSubAgentResultAsync(string subAgentId, SessionId parentSessionId,
        ConversationId? parentConversationId, SessionEntry result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result.ToolCallId);
        if (result.Kind != MessageKind.ToolResult || result.Role != MessageRole.Tool
            || result.ToolName is not ("spawn_subagent" or "manage_subagent") || result.ToolIsError)
            throw new ArgumentException("A receipt requires an original sub-agent ToolResult row.");
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read durable metadata, never an unsaved cached admission/status.
            var parent = await LoadFromFileAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            if (parent is null || SessionMutationPolicy.IsTerminal(parent.Status)
                || (parentConversationId.HasValue && parent.ConversationId != parentConversationId.Value)
                || (_cache.TryGetValue(parentSessionId, out var live) && (SessionMutationPolicy.IsTerminal(live.Status)
                    || live.ConversationId != parent.ConversationId)))
                throw new InvalidOperationException("Parent session is missing, sealed or rebound; result was not consumed.");
            var run = await ReadRunAsync(subAgentId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.ParentSessionId != parentSessionId.Value
                || (run.ParentAgentId is not null && run.ParentAgentId != parent.AgentId.Value))
                throw new UnauthorizedAccessException("Sub-agent does not belong to this parent.");
            if ((run.ParentConversationId is not null && run.ParentConversationId != parent.ConversationId.Value)
                || !SubAgentStatusPolicy.IsTerminal(run.Status))
                throw new InvalidOperationException("Sub-agent is nonterminal or its parent was rebound.");
            var receipts = await ReadReceiptsAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            var accepted = receipts.SingleOrDefault(r => r.SubAgentId == subAgentId);
            if (accepted is not null)
            {
                if (accepted.Entry.ToolName != result.ToolName && accepted.Entry.ToolCallId == result.ToolCallId)
                    throw new InvalidOperationException("Original receipt tool name differs.");
                return accepted.Entry.ToolCallId == result.ToolCallId ? accepted.Entry.Content : null;
            }
            if (parent.GetHistorySnapshot().Any(e => e.Kind == MessageKind.ToolResult && e.ToolCallId == result.ToolCallId))
                throw new InvalidOperationException("An original tool result already exists without this receipt.");
            var safe = result with { Content = _redactor?.Redact(result.Content) ?? result.Content,
                ToolArgs = result.ToolArgs is null ? null : _redactor?.Redact(result.ToolArgs) ?? result.ToolArgs };
            safe.PersistenceKey = "tool-result:" + safe.ToolCallId;
            if (BeforeSubAgentReceiptCommitAsync is { } before) await before(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteReceiptHistoryAsync(parentSessionId, parent.GetHistorySnapshot().Append(safe),
                receipts.Append(new RetainedReceipt(subAgentId, safe)).ToArray(), cancellationToken).ConfigureAwait(false);
            // No fallible second persistence step: retained original row IS the receipt.
            _cache.Remove(parentSessionId);
            if (AfterSubAgentReceiptCommitAsync is { } after) await after().ConfigureAwait(false);
            return safe.Content;
        }
        finally { _lock.Release(); }
    }

    private async Task<bool> PersistWithReceiptsAsync(GatewaySession session, CancellationToken ct)
    {
        var receipts = await ReadReceiptsAsync(session.SessionId, ct).ConfigureAwait(false);
        if (receipts.Count == 0) return false;
        var snapshot = session.SnapshotHistoryForCompaction();
        var entries = snapshot.Entries.Where(e => !receipts.Any(r =>
            e.Kind == MessageKind.ToolResult && e.ToolCallId == r.Entry.ToolCallId)).ToList();
        entries.AddRange(receipts.Select(r => r.Entry));
        entries = entries.OrderBy(e => e.Timestamp).ToList();
        if (BeforeReceiptHistorySaveAsync is { } before) await before(ct).ConfigureAwait(false);
        await WriteReceiptHistoryAsync(session.SessionId, entries, receipts, ct).ConfigureAwait(false);
        if (AfterReceiptHistorySaveAsync is { } after) await after().ConfigureAwait(false);
        var outcome = session.TryReplaceHistoryFromSnapshot(entries, snapshot.DestructiveVersion, snapshot.Count);
        if (outcome != HistoryReplaceOutcome.Aborted)
        {
            // Applied/Rebased increments the destructive version exactly once. Acknowledge
            // only the written replacement prefix, never the rebased concurrent tail. Another
            // destructive mutation between merge and acknowledgement invalidates this cursor.
            var persisted = new SessionHistoryPersistenceSnapshot(entries, [], [], true, 0,
                entries.Count, snapshot.DestructiveVersion + 1);
            session.AcknowledgeHistoryPersistence(persisted, new Dictionary<long, long>());
        }
        await WriteMetadataAsync(session, ct).ConfigureAwait(false);
        return true;
    }
}
