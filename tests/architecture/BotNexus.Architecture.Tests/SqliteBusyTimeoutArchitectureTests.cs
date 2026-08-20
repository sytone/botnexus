using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function guarding the SQLite <c>busy_timeout</c> contract (#1450).
///
/// Every BotNexus SQLite store sets <c>PRAGMA journal_mode = WAL</c> but historically none set
/// <c>PRAGMA busy_timeout</c>. <c>busy_timeout</c> is a <b>per-connection</b> runtime setting that
/// resets to <c>0</c> on every fresh connection - unlike <c>journal_mode = WAL</c>, which is a
/// persistent database-level property. Without a busy timeout, a concurrent cross-process writer
/// (gateway + a CLI <c>debug-db</c>/<c>doctor</c> reader, or two gateways on the same state dir)
/// hits <c>SQLITE_BUSY</c> immediately instead of waiting briefly for the lock to clear.
///
/// This fence requires every store that opens SQLite connections to apply <c>busy_timeout</c>
/// somewhere in the file (either inline in the init PRAGMA block for cached-connection stores,
/// or in the per-open <c>CreateConnection</c> path for fresh-connection stores). It runs with zero
/// runtime dependency - it parses the store source text, which is the right guard because
/// <c>busy_timeout</c> is per-connection and awkward to assert reliably across the differing store
/// connection shapes at runtime.
///
/// All nine SQLite stores are covered: the eight wired by #1450/#1451 plus
/// <see cref="SqliteConversationStore"/>, which was added in #1437 once PR #1442 unlocked the file.
/// See #1435/#1436 for the shared-helper consolidation that will eventually replace these inline
/// PRAGMAs.
/// </summary>
public sealed class SqliteBusyTimeoutArchitectureTests : ArchitectureTest
{

    // The SQLite store source files that #1450 covers (relative to repo root).
    // SqliteConversationStore.cs was added in #1437 once PR #1442 unlocked the file.
    private static readonly string[] StoreFiles =
    {
        "src/gateway/BotNexus.Cron/SqliteCronStore.cs",
        "src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs",
        "src/gateway/BotNexus.Memory/SqliteMemoryStore.cs",
        "src/gateway/BotNexus.Gateway/Extensions/SqliteExtensionStateStore.cs",
        "src/gateway/BotNexus.Gateway.Webhooks/SqliteWebhookRegistrationStore.cs",
        "src/gateway/BotNexus.Gateway.Webhooks/SqliteWebhookRunStore.cs",
        "src/extensions/BotNexus.Extensions.DataStore/SqliteDataStoreBackend.cs",
        "src/gateway/BotNexus.Gateway.Conversations/SqliteConversationStore.cs",
    };

    // Post-#1541: the busy_timeout policy is owned by the shared SqliteConnectionFactory, which
    // attaches a StateChange Open-handler that applies `PRAGMA busy_timeout` on every open. A store
    // satisfies the fence either by the literal inline pragma (legacy shape) OR by routing through
    // the factory (SqliteConnectionFactory.Create / AttachBusyTimeout).
    private static readonly Regex BusyTimeoutPragma =
        new(@"PRAGMA\s+busy_timeout|SqliteConnectionFactory\.(Create|AttachBusyTimeout)", RegexOptions.IgnoreCase);

    // Post-#1436: the seven unblocked stores delegate journal-mode selection to the shared
    // SqliteWalMaintenance helper (WAL on local disk, DELETE on network mounts) instead of an
    // inline `PRAGMA journal_mode = WAL`. Either shape satisfies the "this store manages WAL"
    // precondition of the busy_timeout fence: the literal pragma (still used by the deferred
    // SqliteConversationStore) OR a call to the helper's ApplyJournalModeAsync.
    private static readonly Regex JournalModeWalPragma =
        new(@"PRAGMA\s+journal_mode\s*=\s*WAL|ApplyJournalModeAsync", RegexOptions.IgnoreCase);

    [Fact]
    public void AllCoveredStoreFiles_Exist()
    {
        foreach (var rel in StoreFiles)
        {
            var path = Path.Combine(Repository.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).ShouldBeTrue($"Expected SQLite store source not found: {path}");
        }
    }

    [Fact]
    public void EveryWalStore_AlsoSetsBusyTimeout()
    {
        foreach (var rel in StoreFiles)
        {
            var path = Path.Combine(Repository.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            var source = File.ReadAllText(path);

            // Sanity: the file must still manage WAL - either the inline pragma or the shared
            // SqliteWalMaintenance helper (#1436) - otherwise the fence is matching the wrong file.
            JournalModeWalPragma.IsMatch(source).ShouldBeTrue(
                $"Expected `PRAGMA journal_mode = WAL` or a SqliteWalMaintenance.ApplyJournalModeAsync " +
                $"call in {rel}. If WAL management was removed, update this fence to match the new " +
                "connection-init shape. See #1450/#1436.");

            BusyTimeoutPragma.IsMatch(source).ShouldBeTrue(
                $"{rel} sets `PRAGMA journal_mode = WAL` but never sets `PRAGMA busy_timeout`. " +
                "Without a busy timeout a concurrent cross-process writer hits SQLITE_BUSY immediately " +
                "instead of waiting for the lock. Add `PRAGMA busy_timeout = <ms>` to the connection " +
                "path (inline in the init block for cached-connection stores, or in CreateConnection for " +
                "per-operation stores so EVERY open gets it - busy_timeout is per-connection and does not " +
                "persist like WAL). See #1450.\nFile: " + path);
        }
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsWalWithoutBusyTimeout()
    {
        // Synthetic regression: the pre-#1450 shape - WAL set, no busy_timeout. The detectors MUST
        // recognise WAL is present AND busy_timeout is absent, so the fence would flag it.
        const string preFixSource = """
            public sealed class FakeStore
            {
                public async Task InitAsync(CancellationToken ct)
                {
                    await using var connection = new SqliteConnection(_cs);
                    await connection.OpenAsync(ct);
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "PRAGMA journal_mode = WAL;";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            """;

        JournalModeWalPragma.IsMatch(preFixSource).ShouldBeTrue(
            "Vacuity guard: the pre-fix shape sets WAL and the detector must see it.");
        BusyTimeoutPragma.IsMatch(preFixSource).ShouldBeFalse(
            "Vacuity guard: the pre-fix shape has no busy_timeout and the detector must report that. " +
            "If this fails, the busy_timeout detector is too loose and the fence passes vacuously.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsWalWithBusyTimeout()
    {
        // Synthetic positive: a store applying busy_timeout in CreateConnection (per-open) AND WAL at
        // init must be accepted, so the fence does not over-tighten against the real fixed shape.
        const string fixedSource = """
            public sealed class FakeStore
            {
                private SqliteConnection CreateConnection()
                {
                    var connection = new SqliteConnection(_cs);
                    connection.StateChange += (_, e) =>
                    {
                        if (e.CurrentState == ConnectionState.Open)
                        {
                            using var pragma = connection.CreateCommand();
                            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                            pragma.ExecuteNonQuery();
                        }
                    };
                    return connection;
                }

                public async Task InitAsync(CancellationToken ct)
                {
                    await using var connection = CreateConnection();
                    await connection.OpenAsync(ct);
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "PRAGMA journal_mode = WAL;";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            """;

        JournalModeWalPragma.IsMatch(fixedSource).ShouldBeTrue(
            "Positive pin precondition: the fixed shape still sets WAL.");
        BusyTimeoutPragma.IsMatch(fixedSource).ShouldBeTrue(
            "Positive pin: a store that applies busy_timeout in CreateConnection must be accepted. " +
            "If this fails, the busy_timeout detector is over-tight.");
    }

    // #2977: the busy_timeout StateChange handler must not capture the connection it is attached
    // to. Capturing the local (rather than using the event's sender) is what let a stale
    // subscription prepare a statement against an already-disposed SQLitePCL.sqlite3 handle and
    // throw ObjectDisposedException out of the callback, red-lighting the core gate.
    private static readonly Regex CapturingBusyTimeoutHandler =
        new(
            @"StateChange\s*\+=\s*\(\s*_\s*,\s*\w+\s*\)\s*=>[\s\S]{0,400}?busy_timeout",
            RegexOptions.IgnoreCase);

    [Fact]
    public void BusyTimeoutHandler_UsesSender_NotACapturedConnection()
    {
        const string rel = "src/persistence/BotNexus.Persistence.Sqlite/SqliteConnectionFactory.cs";
        var path = Path.Combine(Repository.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);

        // Precondition: this really is the file that owns the busy_timeout policy, so a rename
        // cannot silently turn this fence into a no-op.
        BusyTimeoutPragma.IsMatch(source).ShouldBeTrue(
            $"Expected the busy_timeout policy to live in {rel}. If it moved, update this fence.");

        CapturingBusyTimeoutHandler.IsMatch(source).ShouldBeFalse(
            "The busy_timeout StateChange handler discards the event sender and must therefore be " +
            "capturing the connection local. It must use the event's sender instead, so the " +
            "subscription never holds - and never issues commands against - a connection whose " +
            "underlying sqlite3 handle has been disposed. See #2977.\nFile: " + path);
    }

    [Fact]
    public void BusyTimeoutHandlerFence_IsNotVacuous_DetectsTheCapturingShape()
    {
        // Mutation guard: the exact pre-#2977 handler must be recognised by the detector, otherwise
        // BusyTimeoutHandler_UsesSender_NotACapturedConnection passes for the wrong reason.
        const string preFixHandler = @"
            connection.StateChange += (_, e) =>
            {
                if (e.CurrentState == System.Data.ConnectionState.Open)
                {
                    using var pragma = connection.CreateCommand();
                    pragma.CommandText = PRAGMA busy_timeout=5000;
                    pragma.ExecuteNonQuery();
                }
            };";

        CapturingBusyTimeoutHandler.IsMatch(preFixHandler).ShouldBeTrue(
            "Vacuity guard: the detector must recognise the pre-#2977 capturing handler shape. " +
            "If this fails, the lifetime fence above is vacuous.");
    }

    [Fact]
    public void BusyTimeoutHandlerFence_PositivePin_AcceptsTheSenderShape()
    {
        // Positive pin: the fixed shape (named handler taking sender) must NOT be flagged, so the
        // detector is not merely rejecting every handler.
        const string fixedHandler = @"
            connection.StateChange += BusyTimeoutOnOpen;

            void BusyTimeoutOnOpen(object? sender, System.Data.StateChangeEventArgs e)
            {
                if (e.CurrentState != System.Data.ConnectionState.Open) { return; }
                if (sender is not SqliteConnection opened) { return; }
                using var pragma = opened.CreateCommand();
                pragma.CommandText = PRAGMA busy_timeout=5000;
                pragma.ExecuteNonQuery();
            }";

        CapturingBusyTimeoutHandler.IsMatch(fixedHandler).ShouldBeFalse(
            "Positive pin: the sender-based handler must be accepted. If this fails the detector is " +
            "over-tight and would flag the corrected shape.");
    }

}
