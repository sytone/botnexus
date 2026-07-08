using System.Collections.Concurrent;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Thread-safe in-memory <see cref="ISqliteDatabaseRegistry"/>. Paths are normalised to their
/// full form and de-duplicated case-insensitively on Windows / case-sensitively elsewhere via
/// <see cref="StringComparer.OrdinalIgnoreCase"/> keys so that stores sharing one file (for
/// example the sessions and conversations stores that both live in <c>sessions.sqlite</c>) are
/// only checkpointed once per sweep.
/// </summary>
public sealed class SqliteDatabaseRegistry : ISqliteDatabaseRegistry
{
    private readonly ConcurrentDictionary<string, string> _paths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(databasePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path we cannot canonicalise is not something we can safely open a connection to;
            // fall back to the raw value so it is at least de-duplicated verbatim.
            full = databasePath;
        }

        _paths.TryAdd(full, full);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetDatabasePaths() => _paths.Values.ToArray();
}
