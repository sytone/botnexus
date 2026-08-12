namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// The identity a process asserts when it opens a BotNexus SQLite store: which world the data
/// belongs to, and the home it was resolved from (#2833).
/// </summary>
/// <remarks>
/// <para><b>Why the home path travels with the ID.</b> The failure this guards against (#2819) is
/// path resolution silently falling back to a shared default. The world ID alone tells an operator
/// <i>that</i> the store belongs elsewhere; the resolved home tells them <i>why</i> - it names the
/// directory the process believed it was working in, which is the value that was wrong. Reporting
/// only the IDs leaves the operator to infer the fallback.</para>
/// <para><b>One value, one derivation.</b> The world ID must be resolved once at startup and handed
/// here, never re-derived per store. If identity and path are produced independently by the same
/// broken resolver they fail consistently, both answers agree, and the guard passes while the data
/// is still wrong - the recurring shape behind #2796, #2792 and #2748.</para>
/// </remarks>
/// <param name="WorldId">The running world's identity, as resolved once at startup.</param>
/// <param name="HomePath">The BotNexus home the store paths were resolved against.</param>
public sealed record SqliteStoreIdentity(string WorldId, string HomePath)
{
    /// <summary>The <c>store_meta</c> key carrying the world identity.</summary>
    public const string WorldIdKey = "world_id";

    /// <summary>The <c>store_meta</c> key carrying the store kind (<c>cron</c>, <c>sessions</c>, ...).</summary>
    public const string StoreKindKey = "store_kind";

    /// <summary>The <c>store_meta</c> key carrying the stamping timestamp, for forensics.</summary>
    public const string CreatedAtKey = "created_at";

    /// <summary>The <c>store_meta</c> key carrying the stamping assembly version, for forensics.</summary>
    public const string CreatedByVersionKey = "created_by_version";

    /// <summary>The identity table name.</summary>
    public const string TableName = "store_meta";
}

/// <summary>
/// Raised when a BotNexus SQLite store on disk declares a different world (or a different store
/// kind) than the process opening it. Always fatal: identity mismatch must never auto-recover,
/// because "recovering" means writing into another world's production data (#2833).
/// </summary>
public sealed class SqliteStoreIdentityMismatchException : InvalidOperationException
{
    /// <summary>Creates a mismatch failure.</summary>
    public SqliteStoreIdentityMismatchException(
        string message,
        string expectedWorldId,
        string actualWorldId,
        string storePath,
        string homePath)
        : base(message)
    {
        ExpectedWorldId = expectedWorldId;
        ActualWorldId = actualWorldId;
        StorePath = storePath;
        HomePath = homePath;
    }

    /// <summary>The world the running process belongs to.</summary>
    public string ExpectedWorldId { get; }

    /// <summary>The world stamped into the store on disk.</summary>
    public string ActualWorldId { get; }

    /// <summary>The store file that was refused.</summary>
    public string StorePath { get; }

    /// <summary>The BotNexus home the refused path was resolved against.</summary>
    public string HomePath { get; }
}
