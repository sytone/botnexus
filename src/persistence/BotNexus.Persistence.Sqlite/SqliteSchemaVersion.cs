namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// The schema-version half of BotNexus SQLite store metadata (#2835): which schema a store on disk
/// was last written by.
/// </summary>
/// <remarks>
/// <para><b>Why this is deliberately NOT fused with world identity.</b> The two guards answer
/// different questions and must produce different outcomes. An identity mismatch is always a bug and
/// must never auto-recover. A version mismatch is <i>expected</i> during a deployment and must
/// migrate forward automatically. Encoding both in one token would make a legitimate rollback and a
/// wrong-world open produce the same error, leaving the operator unable to tell which happened -
/// which is the whole diagnosis.</para>
/// <para><b>Why the version is recorded twice.</b> <c>store_meta</c> is the value BotNexus reads and
/// writes; <c>PRAGMA user_version</c> is the idiomatic SQLite slot, free, atomic, and readable by any
/// external tool (including <c>sqlite3</c>) without knowing BotNexus' table layout. They are written
/// in the same transaction so they cannot diverge across a crash, and a lagging pragma is repaired on
/// next open.</para>
/// </remarks>
public static class SqliteSchemaVersion
{
    /// <summary>The <c>store_meta</c> key carrying the store's schema version.</summary>
    /// <remarks>
    /// Lives in the same table as <see cref="SqliteStoreIdentity"/>'s keys on purpose: a second
    /// metadata table would be a second spelling of "what does this store say about itself", and the
    /// two would drift.
    /// </remarks>
    public const string SchemaVersionKey = "schema_version";

    /// <summary>
    /// The version an empty or unversioned store is treated as holding before any stamp. Zero means
    /// "nothing known", which is distinct from version 1 - a real, declared first schema.
    /// </summary>
    public const int Unversioned = 0;
}

/// <summary>
/// Raised when a BotNexus SQLite store on disk was written by a NEWER schema than the running code
/// understands (#2835).
/// </summary>
/// <remarks>
/// This is the dangerous direction and the reason the feature exists. Rolling <i>forward</i> onto an
/// old store fails visibly at query time with a missing-column error. Rolling <i>back</i> onto a new
/// store fails silently: if the newer schema only added things, old code reads it happily, ignores
/// the new columns, and writes rows the newer code will later read as incomplete. Nothing throws and
/// nothing logs. Refusing the open converts that silent corruption into an actionable stop; the
/// recovery is to restore a backup or run the newer code, never to down-migrate.
/// </remarks>
public sealed class SqliteSchemaVersionMismatchException : InvalidOperationException
{
    /// <summary>Creates a version-mismatch failure.</summary>
    public SqliteSchemaVersionMismatchException(
        string message,
        int storeVersion,
        int codeVersion,
        string storePath)
        : base(message)
    {
        StoreVersion = storeVersion;
        CodeVersion = codeVersion;
        StorePath = storePath;
    }

    /// <summary>The schema version stamped into the store on disk.</summary>
    public int StoreVersion { get; }

    /// <summary>The schema version the running code understands.</summary>
    public int CodeVersion { get; }

    /// <summary>The store file that was refused.</summary>
    public string StorePath { get; }
}

/// <summary>
/// One ordered, forward-only, idempotent schema step (#2835).
/// </summary>
/// <remarks>
/// <para>There is deliberately no down-migration. A rollback is handled by restoring a backup;
/// pretending a schema change can be losslessly reversed invites the data loss it claims to
/// prevent.</para>
/// <para><paramref name="Apply"/> must be idempotent (<c>CREATE TABLE IF NOT EXISTS</c>, guarded
/// <c>ALTER</c>) because a crash between the schema change and the version stamp is always possible,
/// even though the runner brackets them in one transaction.</para>
/// </remarks>
/// <param name="TargetVersion">The version the store holds once this step has run. Must be positive.</param>
/// <param name="Description">A short human-readable name, used in failure messages and logs.</param>
/// <param name="Apply">The schema change, executed on the migrating connection inside the runner's transaction.</param>
public sealed record SqliteSchemaMigration(
    int TargetVersion,
    string Description,
    Action<Microsoft.Data.Sqlite.SqliteConnection> Apply);
