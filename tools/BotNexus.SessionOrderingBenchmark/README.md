# Sessions ordering evidence (#3839)

For contributors deciding whether a sessions ordering index is worth its write cost. **Measured outcome: no production index adopted.** Source verified at `7c361d0d713f1e19231d1ce9a98a43cab66822da`.

## Decision

Keep the production schema and queries unchanged. Retain this standalone diagnostic and its evidence so the decision can be revisited against representative workloads. A later adoption decision needs measured request frequency, conversation-set sizes, inactive-row fraction, offsets, and update frequency. Counting source call sites is not traffic evidence. No live database was read or mutated.

The original issue's id-only 55x result does **not** describe the current portal SQL. The actual portal path uses a conversation predicate, a separate total count, a materialized session window, and indexed history counts. Both blanket candidates are ignored for that scoped page in this fixture. The conversation-prefixed composite helps, but is not a partial index: **it indexes every session and must be maintained on every `updated_at` update**.

## Reproduce safely

Prerequisites: the repository-selected .NET SDK (`global.json`) and restored packages. The project inherits the target framework and the centrally pinned `Microsoft.Data.Sqlite` version. It references no gateway, store, service, or test project. This is a product SQL diagnostic, not a delivery/validation automation helper.

Supply an absolute repository root and an external scratch directory with about 6 GB free during execution. The harness creates a unique child directory, creates only synthetic databases there, and refuses scratch paths inside the repository or `.botnexus`. It never accepts an existing database path. Successful completion deletes only its generated databases and leaves `results.json`. If it fails, stop and inspect its reported child directory; do not run a gateway or test host to diagnose it. Remove that child directory manually when finished.

Example commands from the repository root (PowerShell; replace paths for your checkout):

```powershell
$repo = (Get-Location).Path
$scratch = 'D:/benchmark-scratch/session-ordering'
dotnet build tools/BotNexus.SessionOrderingBenchmark/BotNexus.SessionOrderingBenchmark.csproj -v:minimal
dotnet tools/BotNexus.SessionOrderingBenchmark/bin/Debug/net10.0/BotNexus.SessionOrderingBenchmark.dll $repo $scratch
dotnet tools/BotNexus.SessionOrderingBenchmark/bin/Debug/net10.0/BotNexus.SessionOrderingBenchmark.dll $repo $scratch --reverse
```

For Linux, supply an absolute Linux scratch path instead. The measured runs below used Windows; Linux execution was not measured.

The retained measurements used the provider pinned at the measured revision, not the current checkout's `Microsoft.Data.Sqlite` 10.0.13. Reproduction commands inherit the current central version; a rerun may load a different native SQLite and must be reported separately. The historical timings do not establish current-provider performance. Retained `MeasuredAtUtc` values use explicit local offsets; treat them as offset-qualified instants rather than UTC-formatted strings.

The diagnostic extracts raw SQL from the current `SqliteSessionStore.cs`: enumeration, the complete grouped-history summary projection, the materialized page projection, and the unbounded page fallback. It substitutes only `{where}` and supplies parameters. Source-marker changes fail instead of silently choosing an id-only surrogate. Final additive history columns and existing indexes are included. Auxiliary tables and their triggers are omitted because no auxiliary writes occur; the write probe measures the timestamp update only.

## Source-verified query and caller inventory

Line references below are for the named HEAD, not stable API anchors. `S` is `src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs`; `C` is `src/gateway/BotNexus.Gateway.Conversations/SqliteConversationStore.cs`.

### Original six issue sites, mapped to current symbols

| Site | Emitted SQL | SQL bound and caller context |
| --- | --- | --- |
| C:202, `ListAsync(null)` | `SELECT id FROM conversations ORDER BY updated_at DESC` | Unbounded. Search all-scope and consistency checks enumerate all conversations. Materializes conversations after reading IDs. |
| C:203, `ListAsync(agent)` | `SELECT id FROM conversations WHERE agent_id=$agentId ORDER BY updated_at DESC` | Unbounded within the agent. Session-page ownership resolution, search, routing and tools use it; later in-memory selection does not add a SQL bound. |
| C:246–267, `ListForCitizenAsync` | UNION of `(id,updated_at)` from initiator equality, agent equality gated by `$isAgent=1`, and participant join filtered by citizen kind/id; outer `SELECT id ... ORDER BY updated_at DESC` | Unbounded. `ConversationsController.List(agent)` filters active/root conversations and sorts in memory, without pagination. Existing branch indexes constrain membership, not result count. |
| S:1073, `EnumerateSessionsAsync` | `SELECT id FROM sessions ORDER BY updated_at DESC` | Unbounded, followed by cached/full session hydration. `SessionStoreBase.ListAsync` enumerates before agent filtering. CLI `SessionCommands.ExecuteListAsync` calls it before `Take(limit)`; `SessionTool.ListAsync` calls it without a limit. |
| S:2008, `MigrateOrphanedSessionsAsync` | `SELECT id FROM sessions WHERE conversation_id IS NULL AND agent_id=$agentId ORDER BY updated_at DESC LIMIT 1` | SQL bounded, **legacy startup migration only**, guarded by presence of legacy `agent_id`, before that column is dropped. Not a steady-state fresh-schema query and not benchmarked against the final schema. |
| S:1168, `ListSummariesAsync` (formerly documented as paged portal) | Complete eight-column metadata/count projection with `LEFT JOIN (SELECT session_id,COUNT(*) cnt FROM session_history GROUP BY session_id) h`, ordered by `s.updated_at DESC,s.id ASC` | Conditional `LIMIT $limit OFFSET $offset` only when `updatedAfter==MinValue` and limit is non-null. It still groups all history before joining. **The current API uses `ListSummaryPageAsync` instead.** |

### Newer variants

- **S:1345/1417, bounded `ListSummaryPageAsync`:** `SELECT COUNT(*) FROM sessions s {where}`, then `WITH bounded_sessions AS MATERIALIZED (SELECT` seven metadata columns `FROM sessions s {where} ORDER BY s.updated_at DESC,s.id ASC LIMIT $limit OFFSET $offset) SELECT` all metadata plus `(SELECT COUNT(*) FROM session_history h WHERE h.session_id=b.id)` from the bounded rows, with the same outer ordering. Status predicate is `lower(coalesce(s.status,'active')) NOT IN ('sealed','expired','closed')` unless inactive rows are requested. Conversation predicate is `s.conversation_id IN ($conv0,...)`.
- The bounded branch needs **all three**: `UpdatedAfter==MinValue`, a non-null resolved conversation set, and non-null limit. Agent ownership, an explicit conversation, or supplied eligibility IDs can provide that set. Empty ownership/intersection returns an empty page without SQL.
- **S:1356, fallback `ListSummaryPageAsync`:** the eight-column metadata projection and correlated indexed history count run over all SQL-predicate-matching sessions, ordered by `updated_at DESC,id ASC`, **without LIMIT**. Managed code resolves agents, filters parsed timestamps and applies the requested window/count. This is used for global API requests without a conversation set, non-MinValue time windows, or null limits.
- **`SessionsController.List`:** validates positive limit/nonnegative offset, defaults to 50 and clamps to 200; passes MinValue. Agent/conversation-scoped requests can be SQL bounded. Unscoped global requests still use the unbounded SQL fallback, despite a bounded response.
- **`SessionSearchContributor.SearchAsync`:** supplies MinValue, inactive inclusion, eligible conversation IDs, and a limit of **500**. SQL bounded even for all-scope search, but eligibility discovery first calls unbounded conversation listing. The fixture tests 4,000 eligible IDs; this is not a claimed production size.
- **Summary callers:** `MemorySessionReconciler`, `SessionConsistencyChecker`, `CronSessionStartupReconciler`, and the conversation cost-ranking degraded path pass MinValue with **null limit**. `SessionWarmupService` passes `UtcNow - RetentionWindowHours` with null limit, both for all-agent and per-agent refresh. `SkillReviewCronAction` passes a lookback cutoff with null limit, then filters by agent and takes `MaxSessions`. All are SQL unbounded. No currently found production caller of `ListSummariesAsync` requests its SQL-bounded branch.
- **C:1027, `ResolveByBindingAsync`:** binding join, active status, agent/channel/address predicates, `ORDER BY c.updated_at DESC LIMIT 1`. Bounded single result, used by routing/dispatch and the conversation API; not a sessions-index beneficiary.
- **C:1081, `GetSummariesAsync`:** full conversation/binding-count projection, active root rows, GROUP BY, `ORDER BY c.is_pinned DESC,c.pinned_at DESC,c.updated_at DESC`. Unbounded; current global conversation API and cost ranking use it. An updated-only index does not match its full ordering.
- **C:1114, `GetRetentionCandidatesAsync`:** metadata projection, active/source predicate, `ORDER BY updated_at ASC`, no limit. Retention services select candidates after this read.
- **C:1158, `GetPendingAskUserCheckpointsAsync`:** `SELECT id,pending_ask_user_json FROM conversations WHERE {PendingAskUserPredicate} ORDER BY updated_at DESC`, no limit. Startup checkpoint reconciliation; existing specialized partial index is outside this issue's scope.
- **S:1488, `ListByConversationAsync`:** `WHERE conversation_id=$conversationId ORDER BY created_at ASC,id ASC`, unbounded within one conversation. Existing `(conversation_id,created_at,id)` index must remain; the proposed updated-order composite is not its replacement.
- New maintenance/crash-sentinel pages order by **id/session_id**, not update time; an updated-order index does not match those cursors.

No SQL timestamp comparison or caller bound was changed. Timestamps retain their original offsets; substituting a lexicographic time predicate for the managed parsed-time filter would change semantics. Existing null/unknown status and deterministic session-ID tie behavior remain intact.

## Executed method

Harness build: **success, zero warnings/errors**. Two final runs use opposite candidate order. An earlier exploratory run is not included in the durable evidence. Both final runs completed 13 shapes × 4 configurations, each with 3 warmups and **25 timed repetitions**. All eight projected values are consumed, and page timings include the matching total-count query and per-command parameter binding. Hashes cover complete projected rows plus the page count; all candidates matched. The original id enumeration has no SQL tie contract, so that query's hash compares sorted IDs rather than inventing deterministic tie ordering.

Fixture: deterministic seed 3839, **17,000 sessions**, **428,782 history rows**, approximately **899.8 MB** base database. History counts range 0–1,000, median 9: 90% short histories, 9% medium, 1% long. Payloads are 128/1,024/4,096 characters by history bucket. Session metadata varies from 256–1,279 repeated characters plus title. Conversation skew: 30% concentrated in `c0000` (5,066 actual rows), 25% across the next 39, remainder across IDs 40–3,999. Active, suspended, sealed, expired and null statuses are present. Randomized hashed IDs, duplicate timestamps, and UTC/+05:30/−07:00 offsets are included. This is a **synthetic stress distribution**, not a sampled installation or invented traffic mix.

Provider: centrally pinned `Microsoft.Data.Sqlite` **10.0.0-preview.3.25171.6**; loaded assembly 10.0.0.0; native SQLite **3.46.1**, .NET **10.0.13**, Windows **10.0.26300**. WAL, 4,096-byte pages, synchronous=2, cache_size=−2000, temp_store=0, busy_timeout=5000, foreign_keys=1, WAL autocheckpoint=1000 and journal size limit=64 MiB. No ANALYZE is issued (none was found on this session-store initialization path). Environment, native source ID and compile options are preserved in `evidence.json`.

Each configuration starts from a copy of the identical base fixture. No index hint is used. Reads happen before writes. Write probe: **2,000** `UPDATE sessions SET updated_at=$updatedAt WHERE id=$sessionId` statements, deterministic random IDs, five repetitions, once with a single transaction and once with one transaction per update. Values change each repetition. WAL is truncated before each repeat; automatic checkpoints are disabled **only for this diagnostic** so physical WAL growth is observable. Median WAL sizes therefore measure log generation without checkpoint recycling, not production WAL high-water marks. Index setup is excluded from timings.

## Read latency

Ranges below are the minimum and maximum of the **two run medians**, in milliseconds; they are not confidence intervals. Full samples, p95 and plans are in [evidence.json](evidence.json). `U` = `(updated_at DESC)`; `UI` = `(updated_at DESC,id ASC)`; `CUI` = `(conversation_id,updated_at DESC,id ASC)`. All candidates are added separately while retaining existing indexes.

| SQL shape | Baseline | U | UI | CUI |
| --- | ---: | ---: | ---: | ---: |
| ID enumeration, all 17,000 | 29.521–52.325 | 95.325–114.612 | 5.185–5.282 | 10.305–11.024 |
| Grouped summaries, 50 | 162.135–190.539 | 51.292–53.723 | 51.114–52.521 | 162.536–165.707 |
| Grouped summaries, 50 offset 5,000 | 183.478–207.509 | 90.582–93.666 | 55.873–59.972 | 183.579–202.501 |
| Grouped summaries, unbounded/time fallback | 212.878–229.259 | 210.792–223.929 | 205.649–207.662 | 212.243–215.408 |
| Hot conversation, active page 50 | 73.515–74.268 | 67.615–71.137 | 69.814–81.023 | **29.177–30.380** |
| Hot conversation, all statuses page 200 | 48.633–51.914 | 41.670–50.207 | 41.992–45.984 | **3.446–4.158** |
| Hot conversation, active page 50 offset 1,000 | 69.083–92.106 | 69.236–83.022 | 70.076–71.350 | 44.604–46.949 |
| Sparse conversation, active page 50 (2 rows) | 0.135–0.178 | 0.118–0.126 | 0.123–0.125 | 0.127–0.127 |
| 40 conversations, active page 50 | 125.468–132.240 | 117.637–121.218 | 119.297–123.773 | **62.728–65.266** |
| 40 conversations, active page 50 offset 1,000 | 132.067–145.170 | 137.161–138.622 | 129.473–131.795 | 61.632–62.410 |
| 4,000 eligible conversations, all statuses page 500 | 354.539–356.245 | 357.582–359.655 | 357.708–369.804 | 259.887–267.431 |
| Global active fallback (13,714 rows) | 178.943–182.666 | 241.037–271.414 | 240.720–282.658 | 180.897–183.924 |
| 40-conversation active/time fallback (7,548 rows) | 140.374–142.516 | 149.032–153.389 | 145.452–159.318 | 132.307–142.333 |

For the final forward run, representative baseline→CUI p95 values: hot active page **110.191→37.406 ms**; 40-conversation active page **144.048→71.422 ms**; search page **376.002→287.239 ms**. The baseline global-summary p95 was 210.925 ms versus UI 57.181 ms. These are warm repeated diagnostic calls, not end-to-end HTTP percentiles.

### Before/after plan findings

- **Grouped summaries:** baseline materializes `h` by scanning the covering history session-ID index, scans sessions via its ID index, uses an automatic join index and a temp order B-tree. U changes the session scan to its candidate index but keeps `USE TEMP B-TREE FOR LAST TERM OF ORDER BY`; UI removes that sort. **Both still aggregate all 428,782 history rows.** CUI leaves the baseline plan unchanged.
- **Bounded scoped page:** baseline `MATERIALIZE bounded_sessions` → `SEARCH s USING INDEX idx_sessions_conversation_created (conversation_id=?)` → inner temp sort → bounded `SCAN b` → correlated `SEARCH h USING COVERING INDEX idx_session_history_session_id (session_id=?)` → outer temp sort. U and UI leave this plan unchanged; comparing their small latency differences cannot establish an index benefit.
- **CUI single conversation:** `SEARCH s USING INDEX candidate_conversation_updated_id (conversation_id=?)`; the **inner** temp sort disappears. The **outer bounded-row sort remains**, intentionally preserving the projection's order. Count uses the same candidate; inactive-inclusive count is covering, active-only count still fetches status for every matching session.
- **CUI multiple conversations:** candidate searches each conversation, but the inner global update-order merge still requires a temp B-tree. The measured improvement is real for this fixture even though the inner sort remains. The separate count is still proportional to matching sessions.
- **Unbounded ID enumeration:** U removes the sort but performs noncovering table lookups and is slower. UI is covering and faster. This does not measure the subsequent full session hydration, which can dominate the actual caller.
- **Global page fallback:** U/UI scan through the candidate and fetch projected metadata/status for the entire result. Despite eliminating all or part of the sort, latency regresses. CUI leaves its full-scan plan unchanged.

The complete before/after EXPLAIN detail for every measured query and every count is preserved, not inferred from latency.

## Update and storage cost

| Candidate | 2,000 updates in one transaction, median ms (two runs) | Regression vs same-run baseline | 2,000 separate transactions, median ms | Regression vs same-run baseline | Median WAL bytes, one / separate transactions |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 61.900–70.235 | — | 1,069.311–1,078.811 | — | 6,521,992 / 8,240,032 |
| U | 71.122–74.397 | +5.9–14.9% | 1,194.689–1,198.166 | +10.7–12.1% | 6,690,912 / 35,444,392 |
| UI | 70.626–87.225 | +0.6–40.9% | 1,230.356–1,234.052 | +14.0–15.4% | 6,830,992 / 39,502,592 |
| CUI | 100.690–103.345 | **+43.4–67.0%** | 1,221.080–1,308.099 | **+13.2–22.3%** | 7,448,992 / 23,232,712 |

WAL generation is stable between runs: U/UI/CUI amplify the single-transaction WAL by approximately 1.026x/1.047x/1.142x and separate-transaction WAL by 4.301x/4.794x/2.819x. Larger batched timing variance (especially UI) prevents a precise CPU-cost claim. Before any writes, added database bytes are U **712,704**, UI **1,273,856**, CUI **1,372,160**. Existing indexes were not replaced.

## Fidelity and completion boundary

- This uses the real provider/native dependency and full SQL projection/count, not Python SQLite or an id-only proxy. SQL extraction plus source hash ties query text to the inspected revision.
- It does **not** execute `SqliteSessionStore`, its factory integrity/world checks, retry/cache code, conversation resolution, managed timestamp parsing/filtering/windowing, asynchronous row mapping, transcript hydration, concurrent writers, network mounts, HTTP serialization or real storage/device telemetry. Timings consume provider values synchronously and include command setup. Rows named “managed-time” measure the actual unbounded SQL that feeds the managed path, not the managed work itself.
- Synthetic constant-character payloads exercise row widths and overflow pages, but are not real tool transcripts. Only one distribution and one Windows host/native SQLite version were measured. Warmups are not an OS cache flush; order-reversed runs help expose, not eliminate, cache/JIT/device noise. No production read/write ratio is asserted.
- Fingerprints prove candidate result parity for this fixture, including counts and summary ordering. They are **not** a substitute for the existing store parity suites. Those suites and all production files are unchanged. The pull request records authoritative store-suite validation separately from these diagnostic measurements.
- The retained evidence JSON is reduced from the two full scratch outputs: repeated query texts/parameter maps are omitted to avoid duplicating 4,000-parameter lists. The harness regenerates full SQL/parameters; evidence retains native environment, fixture, candidate order, every plan/count plan, every sample, row counts, fingerprints, index DDL, database sizes and WAL measurements.

**Recommendation:** retain benchmark + documented non-adoption. If measured traffic later justifies a sessions index, CUI is the candidate to evaluate for scoped pages; UI is the candidate for bounded global grouped-summary reads, which currently have no found production caller. Do not replace the created-order conversation index, add an `agent_id` index to the final sessions schema, change mixed-offset filtering, or broaden this task into a general conversation-index audit.
