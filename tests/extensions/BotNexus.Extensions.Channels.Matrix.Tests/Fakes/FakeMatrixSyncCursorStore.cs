using System.Collections.Concurrent;

namespace BotNexus.Extensions.Channels.Matrix.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IMatrixSyncCursorStore"/> standing in for the SQLite store (#3595). Every
/// write is recorded in order so a test can assert not only the final cursor but that it was written
/// exactly once per fully-processed batch.
/// </summary>
public sealed class FakeMatrixSyncCursorStore : IMatrixSyncCursorStore
{
    private readonly ConcurrentDictionary<string, string> _cursors = new(StringComparer.Ordinal);

    /// <summary>Every token written, in order, as <c>agentId/accountName</c> to token.</summary>
    public List<(string Key, string Token)> Writes { get; } = [];

    /// <summary>When set, <see cref="GetAsync"/> throws this on every call.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>When set, <see cref="SetAsync"/> throws this on every call.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>Seeds a stored cursor, standing in for state written before a gateway restart.</summary>
    /// <param name="agentId">Owning agent id.</param>
    /// <param name="accountName">Account configuration key.</param>
    /// <param name="sinceToken">The token a previous process left behind.</param>
    public void Seed(string agentId, string accountName, string sinceToken) =>
        _cursors[Key(agentId, accountName)] = sinceToken;

    /// <inheritdoc />
    public Task<string?> GetAsync(string agentId, string accountName, CancellationToken cancellationToken = default)
    {
        if (ReadFailure is not null)
            throw ReadFailure;

        return Task.FromResult(_cursors.TryGetValue(Key(agentId, accountName), out var token) ? token : null);
    }

    /// <inheritdoc />
    public Task SetAsync(string agentId, string accountName, string sinceToken, CancellationToken cancellationToken = default)
    {
        if (WriteFailure is not null)
            throw WriteFailure;

        var key = Key(agentId, accountName);
        _cursors[key] = sinceToken;

        lock (Writes)
            Writes.Add((key, sinceToken));

        return Task.CompletedTask;
    }

    /// <summary>Snapshot of the writes recorded so far, taken under the same lock writes use.</summary>
    public IReadOnlyList<(string Key, string Token)> WriteSnapshot()
    {
        lock (Writes)
            return [.. Writes];
    }

    private static string Key(string agentId, string accountName) => $"{agentId}/{accountName}";
}
