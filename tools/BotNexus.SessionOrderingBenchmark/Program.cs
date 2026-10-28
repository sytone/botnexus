using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Standalone diagnostic, not a store test. Only a newly-created child of the supplied scratch
// directory is opened. No production assemblies, host, home lookup or services are loaded.
if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--reverse"))
    throw new ArgumentException("Usage: <repository-root> <scratch-root> [--reverse]; scratch must be outside the repository and .botnexus");
var repo = Path.GetFullPath(args[0]);
var scratch = Path.GetFullPath(args[1]);
if (scratch.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || scratch.Equals(repo, StringComparison.OrdinalIgnoreCase)
    || scratch.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals(".botnexus", StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Scratch must be outside the repository and .botnexus.");
var run = Path.Combine(scratch, "ordering-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(run);
Console.WriteLine($"Synthetic diagnostic output: {run}");
const int sessionCount = 17000;
const int readRepeats = 25;
const int writeRepeats = 5;
var sourcePath = Path.Combine(repo, "src", "gateway", "BotNexus.Gateway.Sessions", "SqliteSessionStore.cs");
var source = File.ReadAllText(sourcePath);
var schema = RawAfter(source, "CREATE TABLE IF NOT EXISTS sessions", includeContaining: true);
// Keep the two measured tables, with final additive columns. Unrelated auxiliary tables
// remain absent; their writes and indexes are not part of this diagnostic.
schema = schema[..schema.IndexOf("CREATE TABLE IF NOT EXISTS sub_agent_sessions", StringComparison.Ordinal)];
var summaries = RawAfter(source, "public override async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(");
var page = RawAfter(source, "internal static string BuildBoundedSummaryPageSql");
var fallback = RawAfter(source, ": $\"\"\"", start: source.IndexOf("public override async Task<SessionSummaryPage> ListSummaryPageAsync", StringComparison.Ordinal));
var enumerate = RawAfter(source, "protected override async Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync");
var basePath = Path.Combine(run, "synthetic.sqlite");
var sessionIds = Enumerable.Range(0, sessionCount).Select(Id).ToArray();
var rng = new Random(3839);
var counts = new int[sessionCount];
var conversationCounts = new Dictionary<string, int>();
using (var db = Open(basePath))
{
    Exec(db, schema);
    Exec(db, "ALTER TABLE session_history ADD COLUMN tool_args TEXT; ALTER TABLE session_history ADD COLUMN tool_is_error INTEGER NOT NULL DEFAULT 0; ALTER TABLE session_history ADD COLUMN thinking_content TEXT; ALTER TABLE session_history ADD COLUMN sender_id TEXT; ALTER TABLE session_history ADD COLUMN agent_run_id TEXT; ALTER TABLE session_history ADD COLUMN tool_is_incomplete INTEGER NOT NULL DEFAULT 0;");
    using var tx = db.BeginTransaction();
    using var insert = db.CreateCommand();
    insert.Transaction = tx;
    insert.CommandText = "INSERT INTO sessions(id,channel_type,caller_id,session_type,participants_json,status,metadata,created_at,updated_at,expires_at,conversation_id) VALUES($id,$channel,'synthetic-caller','UserAgent','[]',$status,$metadata,$created,$updated,NULL,$conv)";
    foreach (var name in new[] { "$id", "$channel", "$status", "$metadata", "$created", "$updated", "$conv" })
        insert.Parameters.AddWithValue(name, "");
    using var history = db.CreateCommand();
    history.Transaction = tx;
    history.CommandText = "INSERT INTO session_history(session_id,role,content,timestamp,message_kind) VALUES($id,$role,$content,$time,'message')";
    foreach (var name in new[] { "$id", "$role", "$content", "$time" })
        history.Parameters.AddWithValue(name, "");
    for (var i = 0; i < sessionCount; i++)
    {
        var roll = rng.NextDouble();
        var conv = roll < .30 ? "c0000" : roll < .55 ? $"c{rng.Next(1, 40):D4}" : $"c{rng.Next(40, 4000):D4}";
        conversationCounts[conv] = conversationCounts.GetValueOrDefault(conv) + 1;
        // Duplicate timestamps exercise the id tiebreaker; mixed offsets are deliberately
        // retained. No timestamp WHERE is introduced, matching the store's managed filter.
        var age = conv == "c0000" ? rng.Next(0, 1200) : rng.Next(0, 90000);
        var updated = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-(age / 10 * 10));
        if (i % 11 == 0) updated = updated.ToOffset(TimeSpan.FromHours(5.5));
        if (i % 13 == 0) updated = updated.ToOffset(TimeSpan.FromHours(-7));
        insert.Parameters["$id"].Value = sessionIds[i];
        insert.Parameters["$channel"].Value = i % 8 == 0 ? "cron" : "signalr";
        insert.Parameters["$status"].Value = i % 7 == 0 ? "Sealed" : i % 17 == 0 ? "Expired" : i % 23 == 0 ? "Suspended" : i % 29 == 0 ? DBNull.Value : "Active";
        insert.Parameters["$metadata"].Value = JsonSerializer.Serialize(new { title = $"Synthetic session {i}", note = new string('m', 256 + i % 1024) });
        insert.Parameters["$created"].Value = updated.AddDays(-1).ToString("O");
        insert.Parameters["$updated"].Value = updated.ToString("O");
        insert.Parameters["$conv"].Value = conv;
        insert.ExecuteNonQuery();
        var historyRoll = rng.NextDouble();
        counts[i] = historyRoll < .90 ? rng.Next(0, 17) : historyRoll < .99 ? rng.Next(80, 161) : rng.Next(600, 1001);
        history.Parameters["$id"].Value = sessionIds[i];
        history.Parameters["$time"].Value = updated.ToString("O");
        history.Parameters["$content"].Value = new string('h', counts[i] > 500 ? 4096 : counts[i] > 50 ? 1024 : 128);
        for (var j = 0; j < counts[i]; j++)
        {
            history.Parameters["$role"].Value = j % 2 == 0 ? "user" : "assistant";
            history.ExecuteNonQuery();
        }
    }
    tx.Commit();
    Exec(db, "CREATE INDEX idx_session_history_session_id ON session_history(session_id); CREATE INDEX idx_sessions_created_at ON sessions(created_at); CREATE INDEX idx_sessions_conversation_created ON sessions(conversation_id,created_at,id); CREATE INDEX idx_session_history_crash_sentinel ON session_history(is_crash_sentinel,session_id); CREATE INDEX idx_session_history_tool_invocation_id ON session_history(tool_invocation_id) WHERE tool_invocation_id IS NOT NULL; CREATE INDEX idx_session_history_unlinked_tool_rows ON session_history(id) WHERE tool_invocation_id IS NULL AND tool_call_id IS NOT NULL AND (message_kind IN ('tool-start','tool-result') OR (message_kind IS NULL AND role = 'tool')); CREATE UNIQUE INDEX idx_session_history_persistence_key ON session_history(session_id,persistence_key) WHERE persistence_key IS NOT NULL;");
    if (Scalar(db, "SELECT COUNT(*) FROM sessions") != sessionCount || Scalar(db, "SELECT COUNT(*) FROM session_history") != counts.Sum())
        throw new InvalidOperationException("Fixture row counts do not match.");
    if (TextScalar(db, "PRAGMA integrity_check") != "ok") throw new InvalidOperationException("Fixture integrity check failed.");
    Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
}
var queries = new List<Query>
{
    new("enumerate-ids-unbounded", enumerate, null, [], false),
    new("summaries-global-50", summaries + "\nLIMIT $limit OFFSET $offset", null, [new("$limit", 50), new("$offset", 0)], true),
    new("summaries-global-50-offset5000", summaries + "\nLIMIT $limit OFFSET $offset", null, [new("$limit", 50), new("$offset", 5000)], true),
    new("summaries-unbounded-managed-time", summaries, null, [], true),
};
AddPage("page-single-hot-active-50", ["c0000"], false, 50, 0);
AddPage("page-single-hot-all-200", ["c0000"], true, 200, 0);
AddPage("page-single-hot-active-50-offset1000", ["c0000"], false, 50, 1000);
AddPage("page-single-sparse-active-50", ["c3999"], false, 50, 0);
AddPage("page-agent40-active-50", Enumerable.Range(0, 40).Select(i => $"c{i:D4}").ToArray(), false, 50, 0);
AddPage("page-agent40-active-50-offset1000", Enumerable.Range(0, 40).Select(i => $"c{i:D4}").ToArray(), false, 50, 1000);
AddPage("page-search4000-all-500", Enumerable.Range(0, 4000).Select(i => $"c{i:D4}").ToArray(), true, 500, 0);
queries.Add(new("page-global-fallback-active", fallback.Replace("{where}", "WHERE lower(coalesce(s.status, 'active')) NOT IN ('sealed', 'expired', 'closed')", StringComparison.Ordinal), null, [], true));
queries.Add(new("page-agent40-fallback-managed-time", fallback.Replace("{where}", Where(40, false), StringComparison.Ordinal), null,
    Enumerable.Range(0, 40).Select(i => new KeyValuePair<string, object>($"$conv{i}", $"c{i:D4}")).ToArray(), true));

var candidates = new[]
{
    new Candidate("baseline", null),
    new Candidate("updated-desc", "CREATE INDEX candidate_updated ON sessions(updated_at DESC)"),
    new Candidate("updated-desc-id", "CREATE INDEX candidate_updated_id ON sessions(updated_at DESC,id ASC)"),
    new Candidate("conversation-updated-id", "CREATE INDEX candidate_conversation_updated_id ON sessions(conversation_id,updated_at DESC,id ASC)")
};
if (args.Length == 3) Array.Reverse(candidates);
var expected = new Dictionary<string, string>();
var results = new List<object>();
var environment = new Dictionary<string, object>();
foreach (var candidate in candidates)
{
    var path = Path.Combine(run, candidate.Name + ".sqlite");
    File.Copy(basePath, path, overwrite: false);
    using var db = Open(path);
    if (candidate.Sql is not null) Exec(db, candidate.Sql);
    Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
    var databaseBytesBeforeWrites = new FileInfo(path).Length;
    if (environment.Count == 0)
    {
        environment["sqliteVersion"] = TextScalar(db, "SELECT sqlite_version()");
        environment["sqliteSourceId"] = TextScalar(db, "SELECT sqlite_source_id()");
        environment["provider"] = typeof(SqliteConnection).Assembly.FullName ?? "unknown";
        environment["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        environment["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        foreach (var pragma in new[] { "page_size", "cache_size", "synchronous", "temp_store", "journal_mode", "wal_autocheckpoint", "journal_size_limit", "foreign_keys", "busy_timeout" })
            environment[pragma] = TextScalar(db, "PRAGMA " + pragma);
        environment["compileOptions"] = ReadStrings(db, "PRAGMA compile_options");
    }
    var reads = new List<object>();
    foreach (var q in queries)
    {
        var plan = Plan(db, q.Sql, q.Parameters);
        var countPlan = q.CountSql is null ? [] : Plan(db, q.CountSql, q.Parameters);
        // All result values (not id-only) are read and hashed outside timed repetitions.
        var fingerprint = Fingerprint(db, q);
        if (expected.TryGetValue(q.Name, out var previous) && previous != fingerprint)
            throw new InvalidOperationException($"Candidate changed results for {q.Name}");
        expected[q.Name] = fingerprint;
        for (var i = 0; i < 3; i++) Consume(db, q);
        var samples = new double[readRepeats];
        long rows = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var timer = Stopwatch.StartNew();
            rows = Consume(db, q);
            samples[i] = timer.Elapsed.TotalMilliseconds;
        }
        reads.Add(new { q.Name, q.Sql, q.CountSql, Parameters = q.Parameters.ToDictionary(x => x.Key, x => x.Value), Plan = plan, CountPlan = countPlan, Rows = rows, Fingerprint = fingerprint, LatencyMs = Stats(samples) });
        Console.WriteLine($"{candidate.Name}: {q.Name}: {Stats(samples).Median:F3} ms, {rows} rows");
    }
    // Exact physical WAL growth: turn off automatic checkpoints only for the write
    // diagnostic, checkpoint/truncate before each repetition. This is not a production policy.
    Exec(db, "PRAGMA wal_autocheckpoint=0");
    var writes = new List<object>();
    foreach (var transactionSize in new[] { 2000, 1 })
    {
        var times = new List<double>();
        var walBytes = new List<long>();
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE sessions SET updated_at=$updatedAt WHERE id=$sessionId";
        update.Parameters.AddWithValue("$updatedAt", "");
        update.Parameters.AddWithValue("$sessionId", "");
        for (var repeat = 0; repeat < writeRepeats; repeat++)
        {
            Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
            var updateRng = new Random(3839);
            var timer = Stopwatch.StartNew();
            for (var batch = 0; batch < 2000; batch += transactionSize)
            {
                using var tx = db.BeginTransaction();
                update.Transaction = tx;
                for (var j = 0; j < transactionSize; j++)
                {
                    update.Parameters["$sessionId"].Value = sessionIds[updateRng.Next(sessionCount)];
                    update.Parameters["$updatedAt"].Value = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddSeconds(transactionSize * 100 + repeat * 2000 + batch + j).ToString("O");
                    if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Update missed fixture row.");
                }
                tx.Commit();
                update.Transaction = null;
            }
            times.Add(timer.Elapsed.TotalMilliseconds);
            walBytes.Add(new FileInfo(path + "-wal").Length);
        }
        writes.Add(new { Statements = 2000, TransactionSize = transactionSize, LatencyMs = Stats(times), WalBytes = walBytes });
        Console.WriteLine($"{candidate.Name}: 2000 updates transactionSize={transactionSize}: {Stats(times).Median:F3} ms, WAL median {walBytes.Order().ElementAt(2)} bytes");
    }
    results.Add(new { candidate.Name, IndexSql = candidate.Sql, DatabaseBytesBeforeWrites = databaseBytesBeforeWrites, Reads = reads, Writes = writes });
}
var report = new
{
    SourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
    MeasuredAtUtc = DateTimeOffset.UtcNow, Environment = environment,
    Fixture = new { Sessions = sessionCount, HistoryRows = counts.Sum(), HistoryCounts = new { Min = counts.Min(), Max = counts.Max(), Median = counts.Order().ElementAt(sessionCount / 2) }, ConversationCounts = conversationCounts.OrderByDescending(x => x.Value).Take(10).ToDictionary(), BaseDatabaseBytes = new FileInfo(basePath).Length, Seed = 3839 },
    Method = new { ReadRepeats = readRepeats, ReadWarmups = 3, WriteRepeats = writeRepeats, Analyze = false, ResultParityAcrossCandidates = true, WriteAutocheckpoint = 0, CandidateOrder = candidates.Select(c => c.Name).ToArray() },
    Results = results
};
File.WriteAllText(Path.Combine(run, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
// Remove only databases created by this run, leaving the durable JSON output.
foreach (var file in Directory.EnumerateFiles(run).Where(p => Path.GetFileName(p) != "results.json")) File.Delete(file);
Console.WriteLine($"Complete; parity passed for {queries.Count} shapes across {candidates.Length} candidates. Results: {Path.Combine(run, "results.json")}");

void AddPage(string name, string[] conversations, bool inactive, int limit, int offset)
{
    var where = Where(conversations.Length, inactive);
    var parameters = conversations.Select((conv, i) => new KeyValuePair<string, object>($"$conv{i}", conv)).ToList();
    parameters.Add(new("$limit", limit));
    parameters.Add(new("$offset", offset));
    queries.Add(new(name, page.Replace("{where}", where, StringComparison.Ordinal), "SELECT COUNT(*) FROM sessions s\n" + where, parameters.ToArray(), true));
}
static string Where(int count, bool inactive) => "WHERE " + (inactive ? "" : "lower(coalesce(s.status, 'active')) NOT IN ('sealed', 'expired', 'closed') AND ") + "s.conversation_id IN (" + string.Join(", ", Enumerable.Range(0, count).Select(i => $"$conv{i}")) + ")";
static string Id(int i) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("synthetic-3839-" + i))).ToLowerInvariant();
static string RawAfter(string source, string marker, bool includeContaining = false, int start = 0)
{
    var markerAt = source.IndexOf(marker, start, StringComparison.Ordinal);
    if (markerAt < 0) throw new InvalidOperationException("Source marker not found: " + marker);
    var open = includeContaining ? source.LastIndexOf("\"\"\"", markerAt, StringComparison.Ordinal) : source.IndexOf("\"\"\"", markerAt + (marker.EndsWith("\"\"\"", StringComparison.Ordinal) ? marker.Length - 3 : marker.Length), StringComparison.Ordinal);
    var end = source.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
    if (open < 0 || end < 0) throw new InvalidOperationException("SQL raw string not found");
    var lines = source[(open + 3)..end].Trim('\r', '\n').Split('\n');
    var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Min(l => l.Length - l.TrimStart().Length);
    return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd('\r') : l.TrimEnd('\r'))).Trim();
}
static SqliteConnection Open(string path)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    db.Open();
    Exec(db, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA wal_autocheckpoint=1000; PRAGMA journal_size_limit=67108864");
    return db;
}
static void Exec(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
static long Scalar(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture); }
static string TextScalar(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToString(c.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "NULL"; }
static string[] ReadStrings(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; using var r = c.ExecuteReader(); var rows = new List<string>(); while (r.Read()) rows.Add(r.GetString(0)); return rows.ToArray(); }
static SqliteCommand Command(SqliteConnection db, string sql, KeyValuePair<string, object>[] parameters)
{
    var c = db.CreateCommand(); c.CommandText = sql;
    foreach (var p in parameters) if (sql.Contains(p.Key, StringComparison.Ordinal)) c.Parameters.AddWithValue(p.Key, p.Value);
    return c;
}
static string[] Plan(SqliteConnection db, string sql, KeyValuePair<string, object>[] parameters)
{
    using var c = Command(db, "EXPLAIN QUERY PLAN " + sql, parameters); using var r = c.ExecuteReader();
    var rows = new List<string>(); while (r.Read()) rows.Add($"{r.GetInt64(0)}|{r.GetInt64(1)}|{r.GetString(3)}"); return rows.ToArray();
}
static long Consume(SqliteConnection db, Query q)
{
    if (q.CountSql is not null) { using var count = Command(db, q.CountSql, q.Parameters); _ = count.ExecuteScalar(); }
    using var c = Command(db, q.Sql, q.Parameters); using var r = c.ExecuteReader();
    long rows = 0;
    while (r.Read()) { for (var i = 0; i < r.FieldCount; i++) _ = r.GetValue(i); rows++; }
    return rows;
}
static string Fingerprint(SqliteConnection db, Query q)
{
    using var c = Command(db, q.Sql, q.Parameters); using var r = c.ExecuteReader();
    var rows = new List<string>();
    while (r.Read()) { var values = new object[r.FieldCount]; r.GetValues(values); rows.Add(JsonSerializer.Serialize(values)); }
    if (!q.Ordered) rows.Sort(StringComparer.Ordinal); // original id enumeration has no tie contract
    if (q.CountSql is not null) { using var count = Command(db, q.CountSql, q.Parameters); rows.Add(Convert.ToString(count.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "NULL"); }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
}
static Distribution Stats(IEnumerable<double> samples)
{
    var original = samples.ToArray(); var sorted = original.Order().ToArray();
    return new(sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[0], sorted[^1], original);
}
internal sealed record Query(string Name, string Sql, string? CountSql, KeyValuePair<string, object>[] Parameters, bool Ordered);
internal sealed record Candidate(string Name, string? Sql);
internal sealed record Distribution(double Median, double P95, double Min, double Max, double[] Samples);
