# Provider usage and Copilot quota

Developer reference for local Usage reads. Account quota and gateway-wide scheduled activity require an authenticated **admin** identity. Agent-scoped and satellite identities receive `403`; hiding a panel is not the authorization boundary. The ordinary gateway authentication middleware still applies.

## Routes

| Method and route | Purpose |
| --- | --- |
| `GET /api/providers/usage?windowMinutes=60` | Existing process-local response-bearing request/failure counts and rate-limit-counter-derived burn. Window clamps to 1–1440 minutes; bounded retention may truncate it. These are not authoritative per-response model-token measurements. |
| `GET /api/copilot/quota?instance=github-copilot` | Admin local account-cache state. Does not fetch from GitHub. |
| `POST /api/copilot/quota/refresh?instance=github-copilot&force=false` | Admin explicit, bounded account refresh. |
| `GET /api/providers/usage/details?instance=github-copilot&startUtc=...&endUtc=...` | Admin companion read: separate cached account state, current-generation header observations, configured instance names/types, and bounded scheduled-run activity. Does not fetch from GitHub. |

The companion route leaves existing Usage callers unchanged. `instance` selects a configured Copilot instance, not an API contract. `copilot` and `github-copilot` share the canonical default scope. Unknown, disabled, incompatible, unsupported-authority or unstable credentials yield unavailable account/header state; they never select the default credential as a fallback. Available-instance entries expose names and types only, not credentials or endpoints.

## Account API cache

Successful fetches are cached for five minutes. Failed attempts and forced refreshes have a one-minute minimum interval. A scope has at most one upstream flight; concurrent requests return local state rather than queueing fetches. The upstream deadline is ten seconds, its JSON body is capped at 256 KiB, and the process cache admits at most 64 scopes. A cancellation-ignoring transport retains its flight slot until it drains, preventing duplicate work.

The allowlisted dimensions are `chat`, `completions` and `premium_interactions`. Decimal entitlement, remaining, percentage and overage values retain fractional precision. Missing, invalid and unlimited-sentinel numeric fields remain unknown; explicit unlimited state remains separate. Units are labelled **provider quota units**, not tokens, dollars or billed credits. The response includes source, observation/reset information, partial coverage, last-success/attempt times and attempt state. A transient failure preserves the last success as stale only for the same credential generation. Credential replacement invalidates the previous account's observations.

## Response-header observations

Headers are captured through the real Messages, Completions and Responses transports, including Responses WebSocket handshakes, before response-body reads. Capture does not require an Activity listener and cannot fail model execution. The bounded latest-only process store orders logical requests and subsequent attempts, so an older request cannot replace a newer snapshot and a later retry can replace its earlier attempt.

Header facts remain a separate source from account API facts. `rem` is presented as a percentage; `totRem` as a provider-reported count. Neither is silently substituted for API `quota_remaining`. The recognized `ent=-1&totRem=-1` sentinel pair is explicitly unlimited; malformed or unsupported mappings remain unknown. API/header disagreement is visible, not reconciled into an invented total. After restart, header state is unavailable until another attributed response arrives. Legacy responses without verified scope are counted separately and never attributed to the default account.

## Scheduled-run activity

`startUtc` is inclusive and `endUtc` exclusive. Offsets normalize to UTC. With neither supplied, the window is the last 24 hours. Invalid/reversed explicit intervals return a fixed `400` error. The SQL store clamps the interval to now and its conservative configured retention horizon, returning requested/effective bounds and truncation flags even for an empty window.

`scheduled` is gateway-wide and **provider-unattributed**. Its totals cover every matching job, independently of the ten displayed top jobs. It reports measured/unmeasured runs, prompt/completion subtotals, turns, tools, duration, each field's run coverage and running/unfinalized counts. A token-measured run has at least one token field; unknown measurements are not zero. Two aggregate SQL reads share one read transaction; neither reads transcripts nor loads all runs into application memory. Exact totals outside Int64 range fail closed to unavailable activity rather than becoming floating-point estimates or fabricated zeros.

Retention purges by completion time and protects running rows; the start-time clamp is conservative, not a claim that all retained historical rows are included. Scheduled runs can include overlapping delegated activity. These measurements are not all-conversation consumption or per-account billing. Historical provider attribution, cache-token splits and all-conversation burn are explicitly unsupported. Current job/agent configuration cannot establish the provider of an old run.

## Response boundaries

The companion response contains `instance`, `availableInstances`, `account`, `headers`, `legacyUnattributedHeaderResponses`, `scheduled` and an explicit unknown quota-mapping description. No raw headers/upstream bodies, account profile fields, credentials, endpoints or credential-generation identifiers are returned. Store errors become generic unavailable scheduled state; request cancellation propagates.

Opening the panel can request one refresh. Ordinary polling reads local state only, manual refresh uses the throttled POST, and closing/disposal cancels panel-owned work. No quota-polling cron or second usage ledger is introduced.
