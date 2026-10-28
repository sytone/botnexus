using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

public sealed partial class FileSessionStore
{
    private static readonly HashSet<string> SummaryFields = new(StringComparer.Ordinal)
    { "conversationId", "channelType", "sessionType", "createdAt", "updatedAt", "status", "messageCount" };

    private static async Task<SessionSummaryMeta?> ReadSummaryMetadataAsync(
        System.IO.Abstractions.IFileSystem fileSystem, string path, JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(path)) return null;
        await using var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await BoundedJsonProjection.ReadAsync<SessionSummaryMeta>(stream, options, SummaryFields, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<SessionHistoryScanPage> ScanHistoryAsync(SessionId sessionId, SessionHistoryScan scan,
        CancellationToken cancellationToken = default)
    {
        scan.Validate();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var meta = await ReadSummaryMetadataAsync(
                _fileSystem, GetMetaPath(sessionId), JsonOptions, cancellationToken).ConfigureAwait(false);
            if (meta?.ConversationId is not { } conversationId) return new([], true, 0);
            var conversation = await _conversationStore.GetOwnershipAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (conversation?.AgentId != scan.ExpectedAgentId)
                throw new UnauthorizedAccessException("Session owner changed.");
            var path = GetHistoryPath(sessionId);
            if (!_fileSystem.File.Exists(path)) return new([], true, 0);
            await using var stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            var entries = new List<SessionEntry>();
            var characters = 0;
            var buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            // At most one lookahead character beyond the raw scan budget, in a fixed buffer.
            while ((read = await reader.ReadAsync(buffer.AsMemory(0,
                       Math.Min(buffer.Length, scan.MaxCharacters - characters + 1)), cancellationToken).ConfigureAwait(false)) != 0)
            {
                for (var i = 0; i < read; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Bound raw JSON too: unused thinking/tool-args cannot defeat the cap.
                    if (characters == scan.MaxCharacters || entries.Count == scan.MaxEntries)
                        return new(entries, false, characters);
                    characters++;
                    if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                    AddLine();
                }
            }
            if (line.Length > 0) AddLine();
            return new(entries, true, characters);

            void AddLine()
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var entry = JsonSerializer.Deserialize<SessionEntry>(line.ToString(), JsonOptions);
                    if (entry is not null)
                        entries.Add(new SessionEntry { Role = entry.Role, Content = entry.Content, Timestamp = entry.Timestamp });
                }
                catch (JsonException) { /* Same tolerant JSONL policy as ordinary history reads. */ }
                line.Clear();
            }
        }
        finally { _lock.Release(); }
    }

}
