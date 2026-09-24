using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Durable JSON implementation that stores exactly one atomically replaced file per read-state key.
/// </summary>
public sealed class FileConversationReadStateStore : IConversationReadStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private readonly IFileSystem _fileSystem;
    private readonly SemaphoreSlim _gate;

    /// <summary>
    /// Creates a store rooted at <paramref name="directory"/>. All instances using the same root in
    /// this process share a gate so read-modify-replace remains monotonic across reopened stores.
    /// </summary>
    public FileConversationReadStateStore(string directory, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _fileSystem = fileSystem ?? new FileSystem();
        _directory = _fileSystem.Path.GetFullPath(directory);
        _fileSystem.Directory.CreateDirectory(_directory);
        _gate = DirectoryGates.GetOrAdd(_directory, static _ => new SemaphoreSlim(1, 1));
    }

    /// <inheritdoc />
    public async Task<ConversationReadState?> GetAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        var key = CreateKey(worldId, readerId, conversationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(GetPath(key), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConversationReadState> AdvanceAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        CancellationToken cancellationToken = default)
    {
        var key = CreateKey(worldId, readerId, conversationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(key);
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(
                path + ".lock", cancellationToken).ConfigureAwait(false);
            var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (current is not null && position.Value <= current.Position.Value)
                return current;

            var next = new ConversationReadState(
                key.WorldId,
                key.ReaderId,
                key.ConversationId,
                position,
                current is null ? 1 : checked(current.Version + 1));
            await WriteAtomicallyAsync(path, next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Key CreateKey(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId)
    {
        ConversationReadStateKey.Validate(readerId, conversationId);
        return new Key(ConversationReadStateKey.NormalizeWorldId(worldId), readerId, conversationId);
    }

    private string GetPath(Key key)
    {
        var material = $"{key.WorldId}\0{key.ReaderId.Value}\0{key.ConversationId.Value}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return _fileSystem.Path.Combine(_directory, $"{hash}.json");
    }

    private async Task<ConversationReadState?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(path))
            return null;

        await using var stream = _fileSystem.FileStream.New(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<ConversationReadState>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSystemStream> AcquireCrossProcessLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return _fileSystem.FileStream.New(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAtomicallyAsync(
        string path,
        ConversationReadState state,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = _fileSystem.FileStream.New(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _fileSystem.File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (_fileSystem.File.Exists(temporaryPath))
                _fileSystem.File.Delete(temporaryPath);
        }
    }

    private readonly record struct Key(
        string WorldId,
        ConversationReaderId ReaderId,
        ConversationId ConversationId);
}
