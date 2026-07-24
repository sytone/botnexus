# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

- **security:** Enable dev-origin enforcement by default (#1946)

  The dev-mode browser-Origin guard (`GatewayDevOriginEnforcement`) is now **ON by default**.
  A keyless gateway reached from a browser now enforces the `Gateway.Cors.AllowedOrigins`
  allow-list out-of-the-box, defending the auto-granted `gateway-dev` admin identity against
  DNS-rebind / CSRF. Localhost (`http://localhost:5005`) is allowed by default.

  **Upgrade note:** if you run a keyless gateway reached over a **non-localhost origin** (LAN
  hostname, reverse proxy, netbird, or any `https://` fronting), add that origin to
  `Gateway.Cors.AllowedOrigins` **before upgrading**, or the browser will be rejected on the
  next gateway start. Operators can explicitly opt out with
  `FeatureManagement.GatewayDevOriginEnforcement: false`. Flag-evaluation faults still fail
  open (guard disabled) so a misconfiguration can never lock you out.

## [0.34.0] - 2026-07-23

### ✨ Features

- **scripts:** Break-glass gateway recovery script with interactive Copilot handoff (#2223)
- **#2232:** Add server-side Tools model, persistence, and CRUD API (#2238)
- **tools:** Add optimistic concurrency token to read and edit (#2239)

### 🐛 Bug Fixes

- **extensions:** Share System.IO.Abstractions with host to preserve IFileSystem identity (#2218)
- **#2226:** Stop dropping conversations past LRU cache capacity in list materialiser (#2227)
- **security:** Expand SecretRedactor to cover Basic/Bot/Proxy-Authorization/X-Api-Key and standalone Bearer (#2222)
- **tools:** Quarantine invalid agent descriptors instead of blocking tools (#2228)
- **gateway:** Preserve crash sentinel across multi-turn streams (#2229)

### 📖 Documentation

- **#2224:** Reflect apphost-exe gateway launch as default (post #2199) (#2225)

### 🔨 Refactor

- **#1382:** Replace isolation service locator with explicit tool providers (#2230)

## [0.33.0] - 2026-07-23

### ✨ Features

- **agents:** Support per-parent sub-agent budget overrides (#2207)
- **portal:** Show agent display name instead of generic assistant label (#2210)
- **#2126:** Generate provisional conversation title from first user message (#2211)

### 🐛 Bug Fixes

- **provider:** Classify provider-specific token-limit errors as context overflow (#2192)
- **extensions:** Emit managed dependency closure for extension assemblies (#2193)
- **sessions:** Skip and self-heal orphaned sessions with deleted conversations (#2194)
- **cli:** Stop doctor config prompt from hanging without interactive stdin (#2198)
- **agents:** Normalize pathological token-per-line sub-agent completion summaries (#2200)
- **platform:** Initialize watchdog state on first run (#2201)
- **portal:** Recover stuck turn-active input when RunEnded is missed (#2202)
- **#2199:** Launch gateway via apphost exe to avoid name-based dotnet kills (#2203)
- **gateway:** Verify scheduler responsiveness before fatal liveness alert (#2204)
- **cli:** Suppress self-referential command suggestions and qualify matches (#2205)
- **gateway:** Suppress unchanged config reload notifications and no-op writes (#2206)
- **#2136:** Stop registering sub-agent archetypes as named conversational agents (#2209)
- **tools:** Treat identical edit replacement as idempotent no-op (#2212)
- **config:** Preserve per-agent fields when merging agent defaults (#2215)
- **security:** Centralize tool-audit projection for blocking trigger runs (#2216)

### 📖 Documentation

- Backfill v0.29.0/v0.30.0/v0.32.0 release pages and wire azure runner sidebar (#2214)
- **tools:** Clarify todo as generic agent execution checklist (#2217)

## [0.32.0] - 2026-07-23

### ✨ Features

- **#1888:** Make agents stat card focus the agent filter (#2187)
- **gateway:** Raise default agent tool timeout to 300s with defaults inheritance (#2191)

### 🐛 Bug Fixes

- **cli:** Stop OAuth provider setup from overwriting baseUrl with Ollama defaults (#2180)
- **portal:** Shrink conversation title so header actions stay visible (#2186)
- **provider:** Strip transport CRLF on GPT-5.6 Copilot WebSocket deltas (#2189)
- **cron:** Persist tool call and result history for agent-prompt runs (#2190)

## [0.31.0] - 2026-07-22

### 🐛 Bug Fixes

- **validation:** Add selectable local validation mode (#2183)

### 🧪 Testing

- **maintenance:** Prove throughput orchestration (#2182)

## [0.30.0] - 2026-07-22

### ✨ Features

- **maintenance:** Decouple autonomous worker capacity (#2172)

### 🐛 Bug Fixes

- **agents:** Preserve sub-agent timeout terminal reason (#2156)
- **agents:** Make sub-agent tool execution write-ahead durable (#2160)
- **validation:** Reject incomplete playwright runs (#2162)
- **providers:** Normalize mistral-family tool-call ids (#2166)
- **channels:** Preserve copilot messages deltas (#2171)
- **cli:** Extend gateway startup readiness timeout (#2175)

### 📖 Documentation

- **cli:** Correct agent creation quick start (#2176)

### 🧪 Testing

- **channels:** Cover service bus stream consolidation (#2165)

### ⚙️ Miscellaneous

- **validation:** Make remote validation authoritative (#2161)

## [0.29.0] - 2026-07-21

### ✨ Features

- **portal:** Make conversations stat clear activity filters (#2098)
- **portal:** Add conversation actions to chat header (#2129)
- **build:** Add Azure worktree validation runner (#2146)
- **agents:** Add bounded agent converse timeout (#2151)

### 🐛 Bug Fixes

- **deps:** Pin AngleSharp past security advisory (#2095)
- **tests:** Lease and clean up testhost firewall rules (#2142)
- **webhooks:** Preserve pinned conversations across deliveries (#2144)

### 📖 Documentation

- Daily documentation grooming 2026-07-18 (#2096)
- **getting-started:** Use copilot in agent example (#2153)

## [0.28.0] - 2026-07-18

### ✨ Features

- **config:** Drive secret redaction from [ConfigField] annotations (#2018)
- **portal:** Add Cron Jobs management page under Agents nav (#2024)
- **#1888:** Make Activity scheduled stat card toggle cron visibility (#2027)
- **cron:** Promote Cron Jobs to top-level nav with detail view, history, and provider/model pickers (#2031)
- **cron:** Use single-page job editor and history (#2044)
- **signalr:** Add desktop chat attachments (#2053)
- **channels:** Add opt-in service bus streaming replies (#2054)
- **portal:** Expose configured default agent (#2068)
- **portal:** Pretty-print structured JSON tool results (#2081)
- **providers:** Add capability-aware Copilot WebSocket transport (#2082)

### 🐛 Bug Fixes

- **cli:** Prune stale extension files on deploy (#2017)
- **providers:** Treat blank env api keys as unconfigured (#2023)
- **#2025:** Route auto-title and compaction LLM calls through shared GatewayAuthManager credential seam (#2026)
- **gateway:** Recover orphaned crash sentinels via post-registration scan and write-time self-heal (#2033)
- **config:** Quarantine invalid agent definitions (#2050)
- **copilot:** Normalize gpt-5.6 response delta CRLF (#2052)
- **auto-title:** Preserve streamed title token boundaries (#2051)
- **cron:** Terminalize completed sessions (#2067)
- **gateway:** Keep auth available during invalid config reloads (#2070)
- **portal:** Prevent doubled mobile markdown spacing (#2080)

### 📖 Documentation

- Document servicebus FullyQualifiedNamespace managed-identity option (#2020)

### 🔨 Refactor

- **blazor-client:** Collapse duplicated best-effort hydration into one helper (#2028)

### 🧪 Testing

- **portal:** Eliminate received-call race in pin-button bUnit tests (#2019)
- **config:** Add fitness function for secret-shaped config properties (#2029)

## [0.27.0] - 2026-07-15

### ✨ Features

- **servicebus:** Managed-identity auth and deploy NuGet dependencies (#2003)
- **signalr:** Attach channel identity to user across connection lifecycle (#2004)
- **config:** Complete [ConfigField] annotation coverage across config POCOs (#2016)

### 🐛 Bug Fixes

- **copilot:** Validate advertised endpoints.api host before routing bearer token (#2007)
- **portal:** Stop prior reply flashing as raw markdown on send (#2008)
- **servicebus:** Self-bind channel options from config on late load (#2011)
- **extensions:** Share configuration assemblies with dynamically-loaded extensions (#2015)

### 📖 Documentation

- Daily documentation grooming 2026-07-15 (sub-agent observability endpoint + v0.26.0 release page) (#2005)

### ⚡ Performance

- **conversations:** Rewrite ListForCitizen OR-join as sargable UNION (#2009)

### 🧪 Testing

- **e2e:** Add channel e2e test structure with portal playwright and stubs (#1998)

## [0.26.0] - 2026-07-15

### ✨ Features

- **api:** Sparse fieldsets via ?fields= (#1782) (#1974)
- **tools:** Add field-selection to conversation tool (#1783) (#1975)
- **cli:** Add agent template import (#1978)
- **channels:** Add per-command approval hook to shared slash-command core (#1980)
- **api:** Add read-only sub-agent observability endpoint (#1986)
- **agent365:** Route OTel telemetry to Agent 365 observability via direct OTLP (#1991)
- **mobile:** Mirror desktop slash-command palette on mobile chat (#1993)
- **prompts:** Add cross-agent fabrication trip-wire to tool-use enforcement (#1996)

### 🐛 Bug Fixes

- **portal:** Replace placeholder PWA icons and fix favicon glyph (#1973)
- **portal:** Populate session debug system prompt from live handle (#1981)
- **auto-title:** Log the silent no-persist guards in GenerateAndSaveAsync (#1979) (#1982)
- **signalr:** Enforce per-method least-privilege scope on hub control methods (#1987)
- **exectool:** Block endpoint-redirection env vars in ValidateEnvKey (#1990)
- **gateway:** Enforce browser-origin allow-list on dev-mode auth path (#1943)
- **gateway:** Default titling model to gpt-5.6-luna and extract title from reasoning blocks (#1995)

### 📖 Documentation

- Daily documentation grooming 2026-07-14 (skill-review cron + v0.25.0 release page) (#1988)
- **agent365:** Fix 3 dead links in observability page (#2000)

### 🔨 Refactor

- **agents:** Make AGENTS.md discovery pull-based via get_agent_files tool (#1977)

### 🧪 Testing

- **arch:** Enforce src-tests mirror and no root-level test projects (#1976)
- **portal:** Dispatch pin-button clicks via InvokeAsync to fix CI flake (#1984)
- **integration:** Define seam-test convention and adopt config-save round-trip (#1985)
- **gateway:** Backfill unit-test mirror gaps for src projects (#1997)

### ⚙️ Miscellaneous

- **tests:** Delete ghost dirs and register solution orphans (#1992)

## [0.25.0] - 2026-07-14

### ✨ Features

- **portal:** Add pin/unpin conversation button to sidebar (#1970)
- **skills:** Self-sourcing periodic skill-review cron, on by default per user agent (#1882)
- **channels:** Lift slash-command registry and dispatcher into shared core (#1972)

### 🐛 Bug Fixes

- **portal:** Use agent emoji in panel header with robot fallback (#1968)
- **portal:** Exclude sub-agents and built-ins from SignalR agent list (#1966)
- **config-ui:** Preserve secrets and channel subtrees on config save (#1954 #1955 #1956) (#1957)
- **#1969:** Widen workspace tree, fix edit textarea height, align truncation cap (#1971)

## [0.24.0] - 2026-07-13

### ✨ Features

- **telemetry:** Add config-gated OTLP export for remote collection (#1925)
- **cli:** Add redacted agent export command (#1928)
- **api:** Filter sub-agents and built-ins from agent list by default (#1940)
- **#1888:** Add at-a-glance summary stat strip to Activity dashboard (#1945)
- **cli:** Add on-demand prune of terminal sub-agent workspaces (#1947)
- **portal:** Add pop-out modal for tool args and results (#1953)

### 🐛 Bug Fixes

- **docs:** Escape raw <id> breaking VitePress build (#1924)
- **security:** Add Telegram bot-token pattern to SecretRedactor (#1930)
- **gateway:** Stop liveness watchdog firing false FATAL alerts when idle (#1932)
- **security:** Use timing-safe comparison for gateway api key auth (#1938)
- **portal:** Close unclosed .agent-card-last-activity CSS rule breaking settings modal (#1944)

### 📖 Documentation

- Daily documentation grooming 2026-07-11 (v0.23.0 release page + sidebar orphans + vitepress build fix) (#1926)

### Build

- **deps:** Bump esbuild and vite (#1939)

## [0.23.0] - 2026-07-11

### ✨ Features

- **agent365:** Add Channels.Agent365 adapter for M365 Agents SDK (Register tier) (#1890)
- **portal:** Add desktop Home / Activity dashboard (#1891)
- **portal:** Add archive-confirm setting and fix settings cog rendering (#1895)
- **config:** Add sidebar section navigation to the platform config UI (#1896)
- **config:** Dynamic + static option sources for provider select widgets (#1898)
- **telemetry:** Add platform hot-path metrics (#1899)
- **gateway:** Add minidump-on-crash and last-chance fault handler (#1908)
- **telemetry:** Add extension SDK telemetry seam (#1920)
- **telemetry:** Add metrics read/scrape endpoint (#1923)

### 🐛 Bug Fixes

- **tests:** Pass typed ConversationId to ResolveInboundAsync in routing scenarios (#1874)
- **copilot:** Omit peer OAuth error_description from exception message (#1886)
- **portal:** Key ChatPanel message loop to stop streamed-message garble (#1897)
- **canvas:** Pin ConversationCanvasController route to api/conversations (#1902)
- **cli:** Discover all registered databases in debug db command (#1905)
- **auto-title:** Title live portal/streaming and agent-initiated conversations (#1907)
- **cli:** Use --no-local git clone for install to avoid object-store copy timeout (#1911)
- **docs:** Ignore repo scripts/ links in vitepress dead-link check (#1912)
- **conversations:** Tolerate concurrent delete during list enumeration (#1915)
- **config:** Bind SectionKey as expression so config sections actually filter (#1917)
- **portal:** Send credentials with PWA manifest fetch behind auth proxy (#1921)

### 📖 Documentation

- Daily documentation grooming 2026-07-10 (v0.22.0 release page + signalr-mobile-keepalive sidebar) (#1883)
- **channels:** Add managed-identity Service Bus deployment example (#1904)

### 🔨 Refactor

- **telemetry:** Split OTel-free primitives into Gateway.Telemetry.Abstractions (#1873)
- **persistence:** Unify SqliteConversationStore column migrations into race-tolerant EnsureColumnAsync (#1887)

### 🧪 Testing

- **cli:** Assert --no-local precedes the -- terminator in clone args (#1913)

### ⚙️ Miscellaneous

- **repo:** Pre-authorize testhost.exe firewall rules before running tests (#1906)

## [0.22.0] - 2026-07-10

### ✨ Features

- **mobile:** Add auto-retrying reconnect overlay for mobile client (#1857)
- **telemetry:** Add metrics core and OpenTelemetry SDK wiring (#1858)
- **providers:** Dynamic models declare thinking/context capabilities (#1859)
- **mobile:** Full PWA lifecycle handling and manifest hardening (#1860)
- **portal:** Expand agent editor to full AgentDefinitionConfig with clone action (#1865)
- **cron:** Prune noop cron sessions on configurable retention window (#1754) (#1869)
- **telemetry:** Add shared durable usage-telemetry primitive (#1871)

### 🐛 Bug Fixes

- **repo:** Add heartbeat output to pre-commit hook to prevent no-output starvation (#1847)
- **scripts:** Rebase PR branches onto main instead of merging (#1856)
- **gateway:** Keep agent loop alive across pre-compaction memory flush (#1855)
- **cli:** Terminate git clone args with -- to prevent option injection (#1864)
- **mobile:** Tune SignalR keepalive and reconnect for mobile hub path (#1872)

### 📖 Documentation

- Daily documentation grooming 2026-07-09 (v0.21.0 release page) (#1861)

### 🔨 Refactor

- **gateway:** Promote exchange-completion metadata to typed AgentExchangeCompletionState (#1863)
- **gateway:** Lift IConversationRouter.ResolveInboundAsync conversationId to typed ConversationId (#1866)
- **signalr:** Extract InboundMessage factory in GatewayHub (#1868)

### ⚙️ Miscellaneous

- **tests:** Consolidate Conversation.Tests into Conversations.Tests (#1867)

## [0.21.0] - 2026-07-09

### ✨ Features

- **sessions:** Preserve relevant files across compaction summary (#1812)
- **sessions:** Add opt-in render-time secret redactor for transcript export (#1815)
- **portal:** Add windows pwa deep-integration manifest members (#1818)
- **gateway:** Add agent-level thinking and context configuration (#1819)
- **persistence:** Add periodic WAL checkpoint hosted service (#1821)
- **prompts:** Require loading partially-relevant skills before acting (#1834)
- **skills:** Add create/patch quality guidance to skill_manage schema (#1835)
- **skills:** Add linked-file progressive disclosure for loaded skills (#1837)
- **skills:** Add explicit skill tool names for model ergonomics (#1842)
- **gateway:** Add conversation-level model, thinking, and context override (#1820)
- **skills:** Add skill usage telemetry with sqlite persistence (#1846)

### 🐛 Bug Fixes

- **portal:** Use surrogate-safe truncation in blazor preview helpers (#1813)
- **docs:** Stop excluding docs/api from vitepress build (#1817)
- **webtools:** Harden HtmlToText against unterminated tag tails (#1826)
- **mobile:** Liveness-verified hub reset on app resume (#1843)

### 📖 Documentation

- Daily documentation grooming 2026-07-08 (v0.19.0 + v0.20.0 release pages) (#1823)

### 🔨 Refactor

- **gateway:** Extract outbound fan-out delivery into IOutboundResponseDeliverer (#1814)
- **persistence:** Extract shared SqliteConnectionFactory (#1822)

## [0.20.0] - 2026-07-08

### ✨ Features

- **gateway:** Surface converse allow-list in agent_converse tool description (#1802)
- **#1804:** Humanize config labels and honor Display grouping so settings UI renders sections (#1805)
- **#1436:** Shared SqliteWalMaintenance helper with network-aware journaling (#1806)
- **gateway:** Centralize 3-layer model/thinking/context override resolver (#1810)

### 📖 Documentation

- Daily documentation grooming 2026-07-07 (v0.18.0 release page + web_fetch maxResponseBytes) (#1807)

## [0.19.0] - 2026-07-07

### ✨ Features

- **copilot:** Honor advertised supported_endpoints in model discovery (#1798)

### 🐛 Bug Fixes

- **webtools:** Bound web_fetch response body reads to prevent OOM/DoS (#1796)

### 📖 Documentation

- **#1550:** Correct Telegram config to real channels:telegram schema (#1786)
- **tools:** Surface PowerShell/Python point-of-use gotchas in shell and exec descriptions (#1788)
- **webhooks:** Add python sender example for all response modes (#1789)
- **webhooks:** Add javascript and powershell sender examples (#1790)
- **webhooks:** Add webhook guide and API reference (#1791)
- Document ContentDelta payload role field in SignalR hub contract (#1792)
- **webhooks:** Add csharp sender example for all response modes (#1794)

### 🔨 Refactor

- **copilot:** Resolve enterprise/individual endpoint at the registration seam (#1787)
- **copilot:** Resolve the copilot mcp endpoint at the registration seam (#1799)

### 🧪 Testing

- **gateway:** Deterministic poll for agent hot-reload apply to fix flake (#1801)

## [0.18.0] - 2026-07-06

### ✨ Features

- **channels:** Render agent posts with their stamped assistant role (#1768)
- **portal:** Cache-control headers for Blazor static assets (#1779)
- **gateway:** Enable brotli/gzip response compression for dynamic api responses (#1784)
- **portal:** Ship a service worker for the mobile pwa (#1785)

### 🐛 Bug Fixes

- **sessions:** Fence stale post-run session writes after delete/reset mid-run (#1767)
- **copilot:** Bound OAuth token-exchange response reads (OOM-DoS hardening) (#1774)
- **gateway:** Lock streaming-turn auto-title wiring with a regression test and observable no-fire diagnostics (#1777)
- **portal:** Register service worker so the PWA is installable in Edge (#1778)

### 📖 Documentation

- Daily documentation grooming 2026-07-05 (conversation speak_as + v0.17.0 release page) (#1771)

### 🧪 Testing

- **subagents:** Make retention/eviction assertions wait for the real retirement stamp (#1770)

## [0.17.0] - 2026-07-05

### ✨ Features

- **mobile:** Add settings page consuming shared schemaform (#1747)
- **channels:** Derive channel post role from sender kind and speak_as (#1748)

### 🐛 Bug Fixes

- **#1751:** Guard stored-column JSON reads in SQLite stores against corrupt rows (#1759)

### 📖 Documentation

- Daily documentation grooming 2026-07-01 (SignalR conn params, sidebar orphan, v0.16.0 release page) (#1750)

### 🔨 Refactor

- **#1625:** Collapse gateway hub boilerplate behind an application-service facade (#1749)
- **client:** Extract ask_user prompt parsing into AskUserPromptFactory (#1760)
- **channels:** Extract telegram message-splitting into TelegramMessageSplitter (#1765)
- **config:** Extract validation engine into PlatformConfigValidator (#1766)

## [0.16.0] - 2026-07-01

### ✨ Features

- **signalr:** Distinguish mobile vs desktop clients via connection metadata (#1737)
- **security:** Add trusted-only security-event read path (#1741)
- **portal:** Migrate desktop config panels to schema-driven form (#1742)
- **config:** Validate platform config via DataAnnotations with cross-field escape hatch (#1743)
- **provider:** Add context-size selection with anthropic 1M beta header and copilot 200K cap (#1744)
- **security:** Emit security events from sub-agent and secret-redaction boundaries (#1745)

### 🐛 Bug Fixes

- **tools:** Extend edit remediation diagnostics to all failure shapes (#1739)
- **tools:** Coerce stringified JSON array/object tool arguments (#1740)

### 📖 Documentation

- Daily documentation grooming 2026-06-30 (v0.14.0 + v0.15.0 release pages) (#1746)

## [0.15.0] - 2026-06-30

### ✨ Features

- **gateway:** Wire titling.timeoutSeconds + add titling.enabled switch (#1724)
- **provider:** Add ThinkingLevel.Max and capability-gated copilot mapping (#1728)
- **security:** Emit security events from auth and authorization boundaries (#1729)
- **skills:** Manage shared all-agent skills behind opt-in gate (#1730)
- **chat:** Paginate history with scroll-up load-more (#1733)
- **portal:** Add platform stats overview with live active-loop counts (#1735)

### 🐛 Bug Fixes

- **compaction:** Re-check ShouldCompact between agent-loop iterations (#1725)
- **conversations:** Stop persisting agent-name bindings + dedupe by channel address (#1726)
- **cron:** Skip persisting near-empty wake sessions (#1727)

### 📖 Documentation

- Backfill v0.13.0 release page (#1734)

### 🔨 Refactor

- **domain:** Move CitizenId composition onto value type (#1731)
- **api:** Extract canvas cluster into dedicated controller (#1732)

## [0.14.0] - 2026-06-29

### ✨ Features

- **config:** Add GET /api/config/schema reflection endpoint (#1680)
- **domain:** Add optional SpeakAs role override to InboundMessage (#1686)
- **provider:** Classify provider 401/403 as actionable ProviderAuthenticationException (#1689)
- **security:** Emit security events from exec approval boundary (#1693)
- **portal:** Add generic SchemaForm renderer in Core (#1714)
- **portal:** Add no-messages and load-error empty states to message view (#1697) (#1718)

### 🐛 Bug Fixes

- **blazor-client:** Use O(1) id->index map in HandleToolEnd instead of O(n) FindIndex (#1679)
- **#1681:** Degrade multi-bot Telegram outbound to allow-list routing instead of throwing (#1682)
- **cron:** Wire StreamSetupTimeoutMs so cloud compaction stalls fail fast (#1687)
- **#1698:** Strip leaked invoke/tool_use XML from assistant text in AssistantTextSanitizer (#1699)
- **mobile:** Show date on older chat messages (#1700)
- **gateway:** Wire AssistantTextSanitizer into delivery so leaked tool-call XML is stripped (#1698) (#1708)
- **titling:** Loosen auto-title guard so it can fire on a later turn (#1711)
- **#1602:** Add core.bare guard, versioned hooks, and config hygiene (#1712)
- **gateway:** Fold compaction summary into system prompt on resume so context survives (#1713)
- **agent:** Recover leaked invoke/tool_use XML into executable tool calls (#1709) (#1715)
- **conversations:** Make active-session reset best-effort on DELETE (#1719)
- **security:** Bound non-Copilot provider streaming reads (#1720)

### 📖 Documentation

- Daily documentation grooming 2026-06-28 (#1684)

### ⚡ Performance

- **persistence:** Prepare bulk-insert command once instead of rebuilding per row (#1683)
- **api:** List sessions via transcript-free summary read (#1716) (#1717)

### 🧪 Testing

- **#1602:** Fully isolate UpdateCommand git fixture from host repo (#1721)

## [0.13.0] - 2026-06-28

### ✨ Features

- **config:** Add ConfigField attribute and annotate PlatformConfig (#1677)

### 🐛 Bug Fixes

- **agent:** Audit claims per-turn so multi-turn fabrication is not masked (#1662)
- **telegram:** Deliver tool activity as standalone messages with shared cross-channel icon (#1664)
- **cron:** Purge retention on real terminal statuses (ok/error/timed_out) (#1669)
- **cron:** Scope cron create/update target agent to the calling agent (#1673)
- **copilot:** Bound streaming SSE body to prevent unbounded read OOM (#1674)
- **titling:** Apply provider API-endpoint override to auto-title model (#1675)
- **blazor-client:** Observe reconnect fire-and-forget and synchronize shared HashSet state (#1672)
- **agent:** Gate tool dispatch on ToolUse terminal to ignore truncated calls (#1676)

### 📖 Documentation

- Backfill v0.12.0 release page (#1665)

### 🔨 Refactor

- **prompt:** Hoist GetGatewayData, PascalCase publics, named section order (#1660)
- **blazor-client:** Extract ToChatMessage factory, split history loader, unify user echo (#1670)
- **subagent:** Decompose SpawnAsync into named helpers (#1671)
- **persistence:** Extract per-store row-mappers and drop dead FieldCount probing (#1678)

## [0.12.1] - 2026-06-27

### 🐛 Bug Fixes

- **security:** Cap rate-limit client-window dictionary to prevent unbounded growth (#1637)
- **channels:** Make Telegram streaming-buffer flush surrogate-safe (#1643)
- **subagent:** Make count-cap eviction deterministic with monotonic spawn sequence (#1655)
- **security:** Bound Copilot discovery and error-body JSON reads (#1656)
- **blazor-client:** Route failure paths through ILogger instead of Console.Error (#1658)

### 📖 Documentation

- Backfill v0.11.0 release page (#1644)

### ⚡ Performance

- **persistence:** Eliminate N+1 round-trips in conversation list endpoints (#1642)

### 🔨 Refactor

- **cron:** Introduce CronRunStatus constants and FinalizeRunAsync write-back (#1640)
- **config:** Parse config JSON once and share FinishLoad pipeline with backup recovery (#1657)

## [0.12.0] - 2026-06-26

### ✨ Features

- **sessions:** Trigger compaction on oversized single entries (#1607)
- **agent:** Add post-turn claim auditor for anti-fabrication (#1619)

### 🐛 Bug Fixes

- **#1635:** Apply provider API-endpoint override to compaction summary call (#1638)
- **channels:** Split Telegram messages on surrogate-pair boundaries (#1606)

### 📖 Documentation

- Backfill v0.10.1 release page (#1608)

### ⚡ Performance

- **blazor-client:** Coalesce streamed-delta renders and use StringBuilder buffers (#1634)

## [0.11.0] - 2026-06-25

### ✨ Features

- **channels:** Add Telegram Rich Message API client methods (#1591) (#1592)
- **channels:** Send Telegram outbound via Rich Markdown with MarkdownV2 fallback (#1591) (#1594)
- **channels:** Stream Telegram replies via Rich Message drafts (#1591) (#1604)
- **sessions:** Cap oversized tool results at write time (#1605)

### 🐛 Bug Fixes

- **qmd:** Kill orphaned qmd subprocess on caller cancellation (#1601)
- **security:** Bound untrusted external JSON HTTP response reads (#1603)

### 📖 Documentation

- Backfill v0.9.0 release page + document cron DeleteAfterRun config (#1593)

## [0.10.1] - 2026-06-24

### 🐛 Bug Fixes

- **gateway:** Build session summaries without loading transcripts (#1581) (#1582)
- **channels:** Route Telegram streaming replies by agent in multi-bot mode (#1583) (#1584)
- **portal:** Serve Blazor assets compressed and stop shipping stale generations (#1586)
- **channels:** Treat Telegram 'message is not modified' as benign no-op (#1588)

## [0.10.0] - 2026-06-24

### ✨ Features

- **portal:** Render ask_user prompts on mobile chat (#1572)

### 🐛 Bug Fixes

- **openai-responses:** Drop unpaired tool calls/results before send (#1577)
- **compaction:** Fall back to smaller PreservedTurns when split no-ops above threshold (#1578)

### 🧪 Testing

- **portal:** Deflake cron conversation-grouping sidebar tests (#1576)

## [0.9.0] - 2026-06-23

### ✨ Features

- **security:** Add SecurityEvent model and trusted ring-buffer sink (#1533)
- **cron:** Add opt-in DeleteAfterRun cleanup for ephemeral run sessions (#1571)

### 🐛 Bug Fixes

- **cli:** Align port-availability probe with the gateway wildcard bind (#1537)
- **tools:** Coerce losslessly-safe tool argument shapes before rejecting (#1562)
- **tools:** Return nearest-line diagnostic on edit 0-match instead of bare error (#1563)
- **memory:** Sanitize control/role-injection markup before indexing transcript to memory (#1569)

### 📖 Documentation

- Backfill v0.7.0-v0.8.1 release pages + CLI reference accuracy fixes (#1534)

### ⚡ Performance

- **persistence:** Bound SQLite session/conversation caches and lock pools (#1530)

### 🔨 Refactor

- **gateway:** Extract PrepareTurnAsync from ProcessAsync (#1531)
- **providers:** Unify duplicated completions converter into Core (#1543)
- **gateway:** Split cross-world federation routing out of AgentExchangeService (#1544)
- **providers:** Unify OpenAI/Copilot Responses stream parsers into Core (#1546)
- **config:** Split ConfigPathResolver.TryConvertValue into a dispatcher (#1567)
- **sessions:** Decompose LlmSessionCompactor.CompactAsync (#1568)
- **gateway:** Split DefaultSubAgentManager spawn/completion into testable helpers (#1570)

### 🔧 CI/Build

- **security:** Add a guard for security-sensitive boundary files (#1529)
- **workflows:** Add per-ref concurrency groups to stop stacked CodeQL/CI runs (#1549)

## [0.8.1] - 2026-06-21

### 🐛 Bug Fixes

- **ci:** Unblock main restore on unpatchable SQLite advisory and repair security auto-issue (#1539)

## [0.8.0] - 2026-06-17

### ✨ Features

- **gateway:** Persist pending ask_user prompt and hydrate it on connect (#1513)

### 🐛 Bug Fixes

- **signalr:** Reject hub session-key targeting reserved internal namespaces (#1514)
- **gateway:** Redact secrets for every config section, not just providers (#1527)
- **cron:** Defer scheduled heartbeat while an agent run is active (#1528)

### 📖 Documentation

- Add v0.3.0-v0.6.0 release pages and document provider copilot CLI (#1517)

### 🧪 Testing

- **security:** Add architecture fence for config/secret-echoing redaction (#1515)

## [0.7.0] - 2026-06-17

### ✨ Features

- **portal:** Warn when a run ends with in_progress todo items (#1486)
- **portal:** Replace redundant Conversations heading with activity filter buttons (#1510)

### 🐛 Bug Fixes

- **portal:** Key mobile chat loop and make tool pill a div to stop render crash (#1484)
- **portal:** Report unrecoverable #blazor-error-ui failures to diagnostics (#1485)
- **tools:** Decode read/edit file bytes UTF-8-first with system code page fallback (#1506)
- **gateway:** Evict completed sub-agent records with bounded retention (#1507)
- **security:** Redact secret-shaped values from DebugTool query and runtime output (#1508)
- **cron:** Record host-aborted runs as failed instead of leaving them stuck running (#1511)
- **portal:** Align mobile agent and conversation list ordering with desktop (#1512)

### 📖 Documentation

- Sync SignalR hub contract with IGatewayHubClient and dedupe ignoreDeadLinks (#1492)

## [0.6.0] - 2026-06-16

### ✨ Features

- **portal:** Show agent description in panel header with id on hover (#1457)
- **gateway:** Add RunStarted/RunEnded stream events for authoritative run-active signal (#1458)
- **portal:** Add Follow Up control and fix steer-button flicker between tool runs (#1459)
- **prompts:** Add anti-narration trip-wire to tool-use enforcement (#1463)
- **conversations:** Persist per-conversation todo state on the conversation row (#1472)
- **tools:** Add per-conversation todo tool (#1473)
- **prompts:** Re-inject conversation todo state into the system prompt each turn (#1474)
- **portal:** Add archive conversation action to the mobile overflow menu (#1476)
- **prompts:** Couple todo done-transition to a same-turn tool result (#1477)
- **portal:** Add per-conversation Todo panel with live SignalR updates (#1479)

### 🐛 Bug Fixes

- **conversations:** Populate participant roster in File and SQLite summaries (#1442)
- **docs:** Ignore dead links to srcExclude'd training pages in vitepress build (#1444)
- **config:** Accept Kestrel binding wildcards in gateway.listenUrl validation (#1445)
- **config:** Stop hydrating a default listenUrl into config.json (#1448)
- **persistence:** Set SQLite busy_timeout on every connection across stores (#1451)
- **mobile:** Always show Canvas menu item in mobile portal (#1452)
- **providers:** Retry transient HTTP 421 and transport faults on a fresh connection (#1454)
- **persistence:** Set SQLite busy_timeout on SqliteConversationStore connections (#1455)
- **gateway:** Make session compaction resilient to transient summary failures (#1456)
- **search:** Parse Web IQ webResults shape in MicrosoftAiSearchProvider (#1460)
- **portal:** Seed agent description into client state from REST on initial load (#1462)
- **portal:** Route steer/abort/compact to the displayed conversation's session (#1471)
- **portal:** Render user messages as Markdown like assistant messages (#1478)

### 📖 Documentation

- Add GitHub Models provider page and fix training dead-link check (#1449)

### 🔨 Refactor

- **providers:** Extract Completions ConvertMessages into *MessageConverter (#1443)
- **gateway:** Extract MapAgentEvent pure function from StreamCoreAsync (#1446)
- **providers:** Extract ParseSseStream into ResponsesStreamParser (#1447)
- **providers:** Promote shared Responses stream primitives to Core (#1453)
- **providers:** Collapse the four providers to thin shells over Core engines (#1461)

## [0.5.0] - 2026-06-14

### ✨ Features

- **prompts:** Wrap runtime-context block in internal delimiters (#1433)

### 🐛 Bug Fixes

- **tests:** Serialize provider-registry tests to stop static-registry race (#1421)
- **gateway:** Set explicit SignalR hub transport limits (#1428)
- **docker:** Install curl so container healthcheck reports healthy (#1434)

### 📖 Documentation

- Document debug memory and doctor config CLI commands (#1431)

### ⚡ Performance

- **gateway:** Avoid redundant session re-read in fan-out and extract DeliverToBindingAsync (#1423)
- **gateway:** Resolve conversation id once in InProcessIsolationStrategy (#1429)

### 🔨 Refactor

- **config:** Extract FinishLoad to dedup sync and async config loading (#1422)
- **providers:** Extract BuildRequestPayload into per-provider RequestBuilders (#1424)
- **gateway:** Consolidate sub-agent state into a single SubAgentRecord (#1425)
- **persistence:** Extract shared world-id and citizen logic for conversation stores (#1426)

## [0.4.0] - 2026-06-14

### ✨ Features

- **prompts:** Wrap system prompt sections in XML tags and consolidate tool enforcement (#1280)
- **extensions:** Add per-agent store scoping for QMD knowledge base (#1281)
- **sessions:** Add tool result trimming with age+size hybrid strategy (#1285)
- **gateway:** Add world event bus with subscription model and in-memory implementation (#1286)
- **extensions:** Enforce configurable row limit on DataStore query action (#1290)
- **tools:** Return response metadata in WebFetchTool output (#1295)
- **gateway:** Expose agent exchange budget diagnostics via REST endpoint (#1299)
- **tools:** Add per-call count parameter to WebSearchTool (#1302)
- **tools:** Add status filter to ConversationTool list action (#1303)
- **agent:** Add RunMetrics to AgentEndEvent for token and turn tracking (#1306)
- **gateway:** Add active loop tracking to diagnostics for capacity monitoring (#1307)
- **cli:** Add conversation management subcommands for list, inspect, and archive (#1311)
- **sessions:** Add markdown transcript export endpoint for session history (#1312)
- **gateway:** Add audit logging for conversation mutations (#1317)
- **extensions:** Integrate shared memory stores as QMD knowledge collections (#1318)
- **sessions:** Add delete action to SessionTool for expired and sealed sessions (#1327)
- **cli:** Add debug memory subcommand for agent memory diagnostics (#1328)
- **cron:** Enforce per-job execution timeout to prevent runaway jobs (#1291)
- **prompts:** Expose session identity in the agent runtime line (#1380)

### 🐛 Bug Fixes

- **gateway:** Add input length validation on conversation title, purpose, and instructions (#1315)
- **gateway:** Rename PlatformConfig.Version to PlatformVersion to avoid DOTNET_VERSION collision (#1283)
- **gateway:** Separate config and data directories to support read-only mounts (#1279)
- **gateway:** Make IConversationChangeNotifier optional to prevent DI crash in minimal configs (#1284)
- **tools:** Respect exchange access policy in ListAgentsTool CanConverse field (#1294)
- **tools:** Validate apiProvider against registered providers in agent creation and update (#1298)
- **gateway:** Skip fan-out for non-deliverable channel types like cron (#1321)
- **gateway:** Increase liveness watchdog thresholds to reduce false FATAL alerts (#1322)
- **extensions:** Reject multi-statement SQL in DataStore query action (#1331)
- **extensions:** Re-validate redirect targets to close WebFetch SSRF bypass (#1332)
- **extensions:** Evict exited processes from ProcessManager registry (#1335)
- **extensions:** Evict exited PIDs from ExecTool background-process registry (#1336)
- **tools:** Tolerate non-integer and out-of-range count in web search (#1339)
- **tools:** Reject multi-statement SQL in platform_debug raw_sql action (#1340)
- **tools:** Clamp agent_converse maxTurns to a configurable ceiling (#1342)
- **agents:** Clamp sub-agent spawn maxTurns and timeoutSeconds to configurable ceilings (#1346)
- **extensions:** Cap audio payload size in Whisper transcription handler (#1347)
- **tools:** Cap canvas set_state key length and value size (#1349)
- **memory:** Clamp memory_get limit and memory_search topK to configured ceilings (#1353)
- **tools:** Bound process output tail to a configurable ceiling (#1354)
- **gateway:** Harden Docker startup resilience for minimal and misconfigured environments (#1323)
- **tools:** Clamp shell command timeout to a configurable ceiling (#1355)
- **ci:** Add BotNexus.CodingAgent.Tests to solution so 224 tests run in CI (#1357)
- **tools:** Clamp grep limit to a configurable ceiling (#1359)
- **cron:** Tolerate non-integer and out-of-range limit in cron history (#1364)
- **extensions:** Tolerate non-integer and out-of-range limit in knowledge search (#1374)
- **gateway:** Enforce caller authorization on sub-agent kill endpoint (#1375)
- **extensions:** Reject multi-statement SQL in DataStore where-clause for delete, update, and count (#1418)
- **gateway:** Make ConversationRetentionHostedService change-notifier dependency optional via IEnumerable (#1376)
- **telegram:** Add secure webhook receiver with secret-token validation (#1409)
- **portal:** Tolerate non-integer delay seconds in tool-call description renderer (#1412)
- **extensions:** Tolerate non-integer and out-of-range limit in platform_debug pagination (#1419)

### 📖 Documentation

- Daily documentation grooming 2026-06-12 (#1310)
- Document conversation CLI command and fix debug gateway reference (#1361)

### ⚡ Performance

- **persistence:** Avoid full conversation hydrate for canvas-state existence checks (#1395)
- **persistence:** Scope FileSessionStore.ListByConversationAsync to matching sidecars (#1398)
- **providers:** Cache partial snapshot and tool-arg parse in OpenAIStreamProcessor (#1401)
- **sessions:** Compute GetStatsAsync with SQL aggregates instead of full hydration (#1416)

### 🔨 Refactor

- **skills:** Move SkillsController into the Skills extension as minimal API endpoints (#1287)
- **cli:** Collapse five duplicated git subprocess helpers into one RunGitAsync (#1396)
- **memory:** Extract shared filter-clause builder and bound the LIKE fallback (#1397)
- **config:** Split ConfigPathResolver.TryResolveToken into ResolveMember and ResolveIndex (#1399)
- **portal:** Consolidate stream-state reset into a single ConversationStreamState.Reset() (#1400)
- **gateway:** Extract shared RunExchangeLoopAsync for local and cross-world exchanges (#1402)
- **providers:** Move DetectCompat/CompatProfiles into Core CompatResolver (#1410)
- **api:** Extract conversation history assembly into a service (#1414)
- **gateway:** Extract conversation/session resolution from ProcessAsync (#1417)

### 🧪 Testing

- **cli:** Eliminate flaky CI failures from shared global test state (#1413)
- **cron:** Pin MissedRunDetection no-missed-runs test to a fixed time (#1415)

## [0.3.0] - 2026-06-12

### ✨ Features

- **canvas:** Add canvas state domain model and SQLite persistence (#1073)
- **provider:** Add GitHub Models integration test suite and CI workflow (#1082)
- **gateway:** Add Docker sandbox isolation strategy with lifecycle management (#1083)
- **cli:** Add gateway install/uninstall for OS service integration (#1087)
- **provider:** Split system prompt at cache boundary marker for Anthropic (#1092)
- **canvas:** Add canvas state REST API endpoints for CRUD operations (#1097)
- **providers:** Add claude-opus-4.8 to Copilot built-in models (#1098)
- **gateway:** Add per-agent Docker sandbox configuration and DI registration (#1099)
- **canvas:** Add set_state, get_state, and clear_state actions to canvas tool (#1104)
- **canvas:** Add postMessage bridge for iframe-to-server state persistence (#1115)
- **#1108:** Add Satellite domain model, config schema, and CLI registration command (#1116)
- **gateway:** Add Docker sandbox workspace synchronization (#1118)
- **gateway:** Add satellite authentication and connection tracking (#1120)
- **canvas:** Add SignalR notifications for canvas state changes (#1121)
- **cli:** Add Ollama provider setup and diagnostic subcommands (#1122)
- **memory:** Add memory dreaming cron action for periodic consolidation (#1124)
- **channels:** Wire JWT auth into SignalR hub with claims-derived user identity (#1123)
- **gateway:** Add Docker sandbox tool execution routing and gateway callback (#1128)
- **agents:** Add built-in internal agents for common sub-agent roles (#1130)
- **providers:** Add dynamic model discovery at startup with built-in fallback (#1132)
- **cli:** Add debug sessions subcommand for direct SQLite inspection (#1135)
- **cli:** Add debug logs subcommand for direct log file inspection (#1136)
- **portal:** Expose participant roster and agent attribution for multi-agent conversations (#1137)
- **ci:** Add maintenance automation scripts for PR status, logs, and branch sync (#1141)
- **gateway:** Add threadpool and activity diagnostics REST endpoints (#1142)
- **extensions:** Add update action to DataStoreTool for modifying existing rows (#1163)
- **cron:** Add history action to CronTool for inspecting run outcomes (#1164)
- **extensions:** Add QMD knowledge base extension scaffold with config and backend interface (#1178)
- **cli:** Add debug db subcommand for raw database introspection (#1179)
- **cron:** Add run history retention service to purge old completed records (#1184)
- **gateway:** Add memory statistics REST endpoint for per-agent store diagnostics (#1185)
- **memory:** Add IAgentMemory abstraction with DTOs and factory interface (#1187)
- **portal:** Persist thinking content in session history for reload (#1197)
- **memory:** Add shared memory store configuration and registry with access control (#1207)
- **portal:** Add new conversation button to mobile overflow menu (#1211)
- **extensions:** Add QMD CLI backend and knowledge_search tool (#1215)
- **gateway:** Hydrate config.json with default values from schema contributors on startup (#1217)
- **cli:** Add debug cron subcommand for scheduler diagnostics (#1220)
- **memory:** Wire MemorySaveTool and MemorySearchTool through IAgentMemory abstraction (#1221)
- **cron:** Detect and log missed cron runs on gateway startup (#1225)
- **extensions:** Add count action to DataStoreTool for row counting (#1229)
- **gateway:** Expose standard rate limit headers on every response (#1230)
- **portal:** Add canvas panel to mobile UI as bottom sheet overlay (#1216)
- **gateway:** Add provider health check REST endpoint for API connectivity validation (#1233)
- **gateway:** Add session statistics REST endpoint for aggregate metrics (#1234)
- **webhooks:** Add run retention service to purge old completed webhook runs (#1168)
- **gateway:** Add configurable access policy for agent-to-agent exchanges (#1252)
- **memory:** Add shared store scope to memory search and save tools (#1255)
- **extensions:** Add knowledge_stores and knowledge_get tools to QMD extension (#1256)
- **gateway:** Add conversation budget system with daily caps, loop detection, and cooldown (#1254)
- **cron:** Add agent-converse action for scheduled inter-agent conversations (#1258)
- **extensions:** Add QMD index hosted service with auto-update and health tracking (#1259)
- **memory:** Add learning extraction pipeline with turn classification and knowledge routing (#1260)
- **skills:** Add SHA-256 trust catalog verification for skill script integrity (#1261)
- **cli:** Add debug gateway subcommand for live gateway diagnostics (#1263)
- **search:** Add Microsoft AI Search provider (api.microsoft.ai) (#1270)
- **cli:** Add compaction model doctor checks for expensive and missing summarization models (#1274)
- **memory:** Add shared store promotion during dreaming consolidation (#1276)

### 🐛 Bug Fixes

- **hooks:** Use instance-based HookDispatcher registration to share with extension loader (#1090)
- **portal:** Render settings and debug panels as centered modal overlays (#1089)
- **tests:** Increase debounce test wait margin for slow CI runners (#1086)
- **compaction:** Force compaction when user explicitly requests /compact (#1084)
- **provider:** Filter non-Anthropic thinking signatures in cross-provider replay (#1081)
- **portal:** Map ToolExecutionUpdateEvent to UserInputRequired stream event (#1093)
- **portal:** Move modal panels outside app-shell to escape flex containment (#1095)
- **compaction:** Use PreservedTurns=0 for forced compaction to guarantee summarization (#1096)
- **tests:** Isolate SteeringLoopTests with per-test API provider names (#1102)
- **gateway:** Bypass orchestrator queue for steering to prevent serialization deadlock (#1103)
- **hooks:** Pass AgentDescriptor via BeforePromptBuildEvent to avoid stale DI references (#1105)
- **gateway:** Use GetOrCreateAsync in Steer to eliminate handle lookup race (#1114)
- **compaction:** Surface failure reason in portal and fix adaptive model empty response (#1113)
- **gateway:** Filter non-live entries from auto-title guard condition (#1117)
- **gateway:** Add threadpool watchdog, lock timeout logging, and health endpoint hardening (#1125)
- **tests:** Make GitHub Models integration tests resilient to API degradation (#1131)
- **compaction:** Add configurable timeout and circuit-breaker for hung LLM summarization calls (#1157)
- **security:** Extract SSRF validator as shared utility for reuse across tools (#1158)
- **channels:** Evict stale streaming and error-reply state in Telegram adapter (#1159)
- **gateway:** Resolve skill paths to sandbox-relative locations for Docker isolation (#1160)
- **channels:** Register SignalR hub auth policy in production via IServiceContributor (#1188)
- **webhooks:** Validate callback URLs against SSRF and use IHttpClientFactory (#1167)
- **portal:** Sort built-in agents to bottom of agent dropdown (#1194)
- **portal:** Skip empty message bubble when response is thinking-only (#1195)
- **canvas:** Inject bridge SDK before user scripts to eliminate async timing race (#1196)
- **gateway:** Silently drop thinking-only responses instead of showing confusing stall notice (#1200)
- **portal:** Expand thinking block by default instead of collapsed (#1214)
- **tools:** Reject unknown properties when additionalProperties is false (#1224)
- **tests:** Add 'you' to GenericWindowsAccounts allowlist for docs placeholder (#1241)
- **skills:** Validate symlink resolution before skill file writes (#1242)
- **gateway:** Skip session entry for NO_REPLY responses (#1243)
- **memory:** Add retry logic for transient SQLite and I/O failures on read operations (#1244)
- **providers:** Honor Retry-After header for short rate-limit windows instead of blind backoff (#1245)
- **compaction:** Resolve API key from GatewayAuthManager for summarization calls (#1253)
- **gateway:** Bind gateway.auxiliary.titling as object to stop PlatformConfig crash (#1257)
- **portal:** Include cron-assigned conversations in scheduled sidebar group (#1275)
- **config:** Add JsonConverter for TitlingConfig to handle legacy string format (#1278)

### 📖 Documentation

- Daily documentation grooming 2026-06-10 (#1140)
- **domain:** Fix HIGH and MED stale XML doc comments from post-579 audit (#1144)
- Daily documentation grooming 2026-06-11 (#1226)

### 🔨 Refactor

- **gateway:** Eliminate repeated .From() conversions in AgentsController (#1126)
- **gateway:** Construct typed AgentId and SessionId once at method entry in GatewayHost (#1127)
- **gateway:** Delegate daily memory loading to IAgentMemory.GetPromptContextAsync() (#1267)
- **memory:** Delegate session indexing to IAgentMemory.OnSessionCompleteAsync() (#1269)

### 🧪 Testing

- **agent:** Add comprehensive steering loop tests covering all injection scenarios (#1080)
- **gateway:** Add steering pipeline tests proving queue serialization bug (#1094)
- **cli:** Add unit tests for SatelliteCommand list, register, and remove operations (#1146)
- **security:** Verify SignalR inbound auth rejects writes before authentication (#1169)

## [0.2.2] - 2026-06-09

### ✨ Features

- **copilot:** Parse copilot_usage billing snapshot and emit Activity tags (Phase 5B, #810) (#980)
- **portal:** Add missing data-testid attributes to key UI elements (#984)
- **portal:** Group conversations in sidebar with collapsible scheduled section (#987)
- **portal:** Add descriptive tool call summaries with emoji and context in chat panel (#992)
- **portal:** Add ARIA dialog attributes and Escape key handling to overlay panels (#994)
- **prompts:** Add SectionId to IPromptSection and register tool-enforcement section (#1007)
- **e2e:** Add TCP-level readiness probe to E2E test harness (#961)
- **prompts:** Add shell-efficiency and skills-guidance prompt sections (#1009)
- **prompts:** Add model-guidance prompt section with per-family detection (#1010)
- **prompts:** Add IPromptOverrideResolver and FilePromptOverrideResolver for section overrides (#1012)
- **portal:** Render compaction boundaries as styled separators in conversation history (#1016)
- **tools:** Add platform debug tool for read-only session and runtime inspection (#1018)
- **portal:** Add PWA support with manifest, service worker, and offline caching (#1021)
- **portal:** Add steering queue panel with per-conversation pending display (#1022)
- **cli:** Add global --target option for multi-instance support (#1027)
- **gateway:** Add IExtensionStateStore with SQLite persistence (#1028)
- **cron:** Add command action type for shell-based cron jobs (#1047)
- **gateway:** Add memory pressure diagnostics with threshold monitoring and REST endpoint (#1062)
- **provider:** Apply system_and_3 multi-breakpoint cache strategy for Anthropic (#905)
- **provider:** Add GitHub Models inference provider with free-tier model catalog (#914)
- **agents:** Add workspace sharing and path grants for sub-agent isolation (#1026)

### 🐛 Bug Fixes

- **portal:** Add JsonStringEnumConverter to AskUserInputType for SignalR serialization (#979)
- **portal:** Preserve streamed text during history reconciliation and reconnect (#981)
- **gateway:** Prevent cron session reuse by interactive channels (#986)
- **portal:** Wrap burger toggle test clicks in InvokeAsync for async handler (#989)
- **portal:** Add confirmation dialog to mobile new session action (#990)
- **gateway:** Detect and clean up abandoned tool calls before dispatching new user messages (#991)
- **streaming:** Synthesize failed tool results for orphan tool calls and skip whitespace-only content (#1011)
- **portal:** Wrap update badge test clicks in InvokeAsync for async handler (#1015)
- **portal:** Show agent dropdown on narrow desktop viewport (#1017)
- **tests:** Isolate ProviderCommandTests from static AnsiConsole writer disposal (#1031)
- **tests:** Use delta assertion in MCP warmup cache test for parallel safety (#1032)
- **docs:** Upgrade vitepress to 2.x to resolve vite and esbuild security advisories (#1035)
- **tools:** Add 5-second regex timeout to GrepTool to prevent ReDoS (#1045)
- **provider:** Change Copilot limits dict to JsonElement for mixed-type values (#1048)
- **gateway:** Wire ToolEnforcement, ShellEfficiency, SkillsGuidance, and ModelGuidance sections into prompt pipeline (#1053)
- **tests:** Serialize AnsiConsole-dependent test classes to prevent static writer race (#1051)
- **tools:** Use ArgumentList for shell invocation to eliminate double-parse escaping (#1055)
- **tools:** Handle surrogate pairs in EditTool fuzzy text normalization (#1063)
- **streaming:** Persist tool starts immediately and add provider stall watchdog (#1058)
- **portal:** Wire SessionDebugPanel into MainLayout debug button (#1060)
- **gateway:** Debounce agent config reload notifications to prevent reload storm (#1076)
- **gateway:** Add liveness watchdog and activity tracking for deadlock detection (#1078)

### 📖 Documentation

- **gateway:** Correct stale comments referencing deleted phases and obsolete infrastructure (#1033)
- **training:** Fix stale BotNexus.Providers.* namespace references (#1034)
- **cli:** Document .NET 10 SDK requirement in README and NuGet package (#1036)
- Add provider pages, debug tool extension, update prompt pipeline and sidebar structure (#1037)
- **tools:** Add shell execution feature guide and update configuration docs (#1056)
- Add extension pages, SignalR channel docs, and CLI --target option (#1064)
- **channels:** Remove stale WebSocket terminology and align with SignalR architecture (#1079)

### 🔨 Refactor

- **gateway:** Remove dead session metadata conversationStatus shadow writes (#1020)

## [0.2.1] - 2026-06-07

### ✨ Features

- **portal:** Add Message History tab to SessionDebugPanel with role badges and paging (#933)
- **copilot:** Add CLI-fidelity request headers and response observability (Phase 5, #810) (#974)
- **conversations:** Add pin/unpin support with sorted display ordering (#973)
- **gateway:** Add log diagnostics ring buffer and REST endpoint for pattern monitoring (#975)

### 🐛 Bug Fixes

- **sessions:** Retry ReplaceHistoryAsync on phantom rollback errors with fresh connection (#963)
- **gateway:** Add compaction circuit breaker to skip after consecutive LLM failures (#964)
- **portal:** Style interrupt-steer button to match steer, stop, and send controls (#968)
- **gateway:** Truncate entry content in compaction prompt to prevent context overflow (#969)
- **cli:** Render startup banner before console encoding strands stderr writer (#978)

### 🔨 Refactor

- **domain:** Remove redundant Trim calls before domain primitive From and add architecture enforcement (#970)

### 🧪 Testing

- **portal:** Add vertical slice data flow tests for parent-child wiring (#971)
- **conversations:** Add abstract parity test base for IConversationStore with all 3 implementations (#976)

## [0.2.0] - 2026-06-06

### ✨ Features

- **gateway:** Introduce IInboundMessageOrchestrator + IInboundMessageProcessor seam (#696) (#701)
- **skills:** Add Skills Explorer portal section with gateway API (#712)
- **skills:** Add SkillManagerTool with create/edit/patch/delete/write_file/remove_file actions (#706) (#724)
- **#743:** Add MessageRole.Notification, TurnInterrupted event, and InterruptedTurnNotificationService (#748)
- **ui:** Remove mic button - audio recording not yet supported (#741)
- **#776:** Include skills world-default in generated config.json (#779)
- **#777:** Add `doctor config` sub-command for guided config migration (#781)
- **#745:** Add webhook domain models, store interfaces, secret helpers, and primitives (#782)
- **#632:** Add create_agent and update_agent as core gateway tools (#775)
- **#745:** Add SqliteWebhookRegistrationStore and SqliteWebhookRunStore (#784)
- **#407:** Add full agent editor panel with sidebar sub-tree and all config fields (#786)
- **#745/#746:** Add WebhooksController, DI wiring, and registration CRUD (#787)
- **#746:** Add inbound webhook endpoint with HMAC auth, async/sync/callback dispatch (#792)
- **gateway:** Add archive action to ConversationTool (#820)
- **gateway:** Fire SignalR push on ConversationTool metadata mutations (#821)
- **cli:** Scaffold heartbeat config by default in init and agent add (#835)
- **agents:** Add optional Order field to AgentDescriptor for consistent agent list sort (#844)
- **gateway:** Heartbeat enabled by default and config normalisation service (#834)
- **portal:** Protect core agent files from deletion in workspace tab (#843)
- **portal:** Render conversation list items as anchor elements for browser-native open in new tab (#858)
- **gateway:** Prepend current datetime to LLM messages when dateTimeInjection is enabled (#884)
- **channels:** Strip embedded thinking/reasoning XML tags from outbound text for channels without thinking display (#885)
- **gateway:** Add InterruptAndSteerAsync contract to IAgentHandle (Phase 1a) (#888)
- **sessions:** Add sub_agent_sessions table to sessions.db schema (#889)
- **skills:** Seed example-skill when global skills directory is first created (#892)
- **gateway:** Auto-archive inactive conversations after configurable retention period (#883)
- **copilot:** Add CopilotMessagesProvider for Phase 1a carve-out (#810) (#886)
- **gateway:** Implement InterruptAndSteerAsync in InProcessAgentHandle (#893)
- **sessions:** Write sub-agent session rows to sessions.db on spawn and completion (#894)
- **gateway:** Wire InterruptAndSteer hub method on GatewayHub (#895)
- **sessions:** Expose sub-agent session history via GET /api/sessions/{id}/subagents/history (#898)
- **gateway:** Expose CacheRetentionMode in AgentDescriptor and wire into InProcessIsolationStrategy (#900)
- **sessions:** Capture LastRenderedSystemPrompt on GatewaySession at dispatch time (#901)
- **portal:** Add Interrupt+Redirect button and InterruptAndSteer hub call (#902)
- **tools:** Add DataStoreTool domain model and DataStoreToolContributor registration (#903)
- **platform:** Introduce BotNexus.Yaml targeted frontmatter parser and migrate SkillParser (#904)
- **copilot:** Route Copilot Claude models via CopilotMessagesProvider (Phase 1b, #810) (#938)
- **gateway:** Auto-generate conversation title from first user+assistant exchange (#906)
- **skills:** Add /skills create and /skills delete slash commands (#907)
- **portal:** Surface cache token counts on assistant messages in chat panel (#910)
- **copilot:** Add CopilotResponsesProvider + CopilotCompletionsProvider (Phase 2a, #810) (#944)
- **copilot:** Route Copilot OpenAI-flavour models via carved-out providers (Phase 2b, #810) (#947)
- **sessions:** Add GET /api/sessions/{sessionId}/debug endpoint (#911)
- **gateway:** Auto-replay interrupted user turns on gateway restart with max-attempt guard (#918)
- **gateway:** Inject AGENTS.md files from repo tree hierarchy into system prompt (#919)
- **mcp:** Add auth provider reference to McpServerConfig for HTTP/SSE token injection (#917)
- **portal:** Add SessionDebugPanel with Overview and Metadata tabs (#920)
- **portal:** Add Debug Mode toggle to portal settings and debug icon in main banner (#921)
- **tools:** Implement SQLite storage backend with schema inference and size limit (#926)
- **mobile:** Add refresh button and auto-reconnect on app resume (#930)
- **portal:** Add System Prompt tab to SessionDebugPanel with copy-to-clipboard (#927)
- **gateway:** Auxiliary compression model and iterative summary for compaction Phase 2 (#946)
- **copilot:** Add provider copilot CLI surface (Phase 4, #810) (#962)

### 🐛 Bug Fixes

- **mobile:** Use relative NavigateTo paths to stay within /mobile/ base (#698)
- **ui:** Remove tool processing status bar from chat panel (#720)
- **canvas:** Remove duplicate /api/ prefix in GetConversationCanvasAsync URL (#733)
- **cli:** Suppress banner when stdout is redirected (#685) (#734)
- **cli:** Scaffold agent workspace on agent add and wizard (#331) (#735)
- **cli:** Agent/agents alias, --display-name/--description/--emoji/--disabled flags, help text clarity (#599) (#737)
- **e2e:** Audit and align all E2E tests to current UI design (#751)
- **#633:** Block direct config.json writes from agent file tools (#774)
- **#383:** Fetch canvas for auto-selected conversation on initial load (#778)
- **#793:** Mobile chat auto-scroll not working (#795)
- **portal:** Remove broken Restart Gateway button (#816)
- **portal:** Suppress NO_REPLY cron turns from conversation history (#817)
- **cron:** Reject out-of-range NextRunAt and CreatedAt timestamps with 400 response (#818)
- **signalr:** Classify pre-handshake WebSocket close as benign in GatewayHub (#819)
- **gateway:** Isolate session transcript save from channel send delivery (#826)
- **portal:** Route TurnEnd event through SignalR to clear streaming state on tool-only turns (#827)
- **portal:** Wrap async agent-switch assertion in WaitForAssertion (#829)
- **cli:** Distinguish auth failure from unreachable in gateway status (#830)
- **gateway:** Replace blocking DispatchAsync with Post in ConversationTool (#832)
- **gateway:** Deduplicate user turns in cross-world relay on cancel-and-retry (#833)
- **cron:** Defer user entry save to after PromptAsync to prevent duplicate message (#836)
- **agents:** Replace Task.Delay timing heuristics with deterministic probes in ToolExecutorTimeoutTests (#837)
- **gateway:** Fall back to ActiveSessionId in GetHistory when session lacks conversation_id (#839)
- **tests:** Make Post_WhenQueueFull_ReturnsFalse deterministic with processorStarted gate (#871)
- **portal:** Clear stale streaming state and reload history on SelectConversation (#840)
- **portal:** Re-apply tab deep-link after agent data loads via store change (#841)
- **gateway:** Emit system stall entry when stream completes with thinking only and no visible text (#857)
- **provider:** Reject out-of-range Copilot OAuth exp claims to prevent bypass or crash (#861)
- **cron:** Fire SignalR notify when CronTrigger reactivates an archived pinned conversation (#866)
- **gateway:** Seal session when CloseAfterResponse forces archive in CrossWorldFederationController (#868)
- **gateway:** Write workspace files as BOM-free UTF-8 to prevent YAML parse failures (#874)
- **skills:** Emit LogWarning when SkillDiscovery skips a skill due to invalid frontmatter (#873)
- **portal:** Surface client-side errors with collapsible detail and gateway reporting (#842)
- **tools:** Block LD_*, DYLD_*, and PATH env var overrides in ExecTool agent input (#862)
- **cron:** Guard ActiveSessionId so cron sessions never evict a human session (#867) (#872)
- **skills:** Support YAML block scalars in SkillParser frontmatter (#880)
- **sessions:** Add continuity regression tests for session compactor (audit #665) (#881)
- **scripts:** Strip history block from diff check to prevent no-op timestamp patches
- **gateway:** Bump conversation UpdatedAt on every completed message turn (#891)
- **gateway:** Detect suspicious startup config and recover from most recent valid backup (#896)
- **mobile:** Bypass service worker cache for top-level navigate requests (#897)
- **gateway:** Send stall notice when agent returns thinking-only response (#899)
- **gateway:** Inject CONTEXT COMPACTION guardrail prefix before compaction summary and update structured template to 5-section spec (#908)
- **gateway:** Wire /compact slash command in BuiltInCommandContributor (#912)
- **gateway:** Add RuntimePinnedTools to DefaultToolPolicyProvider that bypass all deny-list checks (#909)
- **gateway:** Debounce agent config file watcher and filter to definition files only (#943)
- **gateway:** Filter NO_REPLY assistant entries from conversation history API (#913)
- **security:** Block SSRF in WebFetchTool by rejecting private and IMDS addresses (#915)
- **portal:** Remove underline from conversation list anchor links (#948)
- **portal:** Pass active ConversationId to CanvasPanel so canvas renders conversation-scoped HTML (#953)
- **skills:** Change AllowSkillCreation and AllowSkillDeletion defaults to true (#949)
- **mobile:** Preserve active conversation on refresh, render Unicode icons, add error boundary (#958)
- **portal:** Style token usage stats below message bubble with human-readable labels (#954)
- **tests:** Replace unregistered noop tool in TOOL_CALL_SEQUENCE with get_datetime (#932)
- **gateway:** Forward update channel to CLI spawned process (#928)
- **tests:** Add assistant to E2E AgentIds to match botnexus init scaffold (#931)
- **sessions:** Add retry on transient SQLite errors and 503 for session store unavailable (#942)

### 📖 Documentation

- **architecture:** Document channel-binding conventions (#729) (#730)
- **readme:** Replace phantom scripts with real CLI install flow (#738)
- **agents:** Require test-impacted.ps1 before every push, clarify pre-commit hook gap

### 🔨 Refactor

- **signalr:** Wire GatewayHub onto IInboundMessageOrchestrator and drop obsolete JoinSession/LeaveSession (#714) (#715)
- **signalr:** Slim GatewayHub.ResolveOrCreateSessionAsync to pure resolution (#721) (#726)

### 🧪 Testing

- **e2e:** Mobile chat coverage - scroll and error bar regression tests (#722 #723) (#727)
- **e2e:** Full portal coverage expansion - 18 new test files, 86 tests (#796)
- **agents:** Add Copilot wire replay harness + path-leak fence (Phase 0b, #810) (#811)
- **agents:** Add Copilot request-body snapshot harness (Phase 0a, #810) (#812)
- **agents:** Pin direct-Anthropic request snapshots (Phase 0c, #810) (#860)
- **agents:** Pin gateway-level Copilot routing (Phase 0d, #810) (#863)
- **gateway:** Pin pre-carve-out config.json compatibility (Phase 0e, #810) (#875)
- **gateway:** Pin pre-carve-out auth.json compatibility (Phase 0f, #810) (#878)

### ⚙️ Miscellaneous

- **scripts:** Add ci-pr-comment and maintenance-pr-comment templating scripts
- **copilot:** Drop dead helpers and assembly-name suffixes (Phase 3, #810) (#950)
- **gateway:** Remove FileAgentConfigurationSource and FileAgentConfigurationWriter (#956)

### 🔧 CI/Build

- Exclude live-gateway and Playwright tests from full-tests run (#929)

## [0.1.14] - 2026-06-01

### ✨ Features

- **domain:** Introduce Citizen abstraction and migrate SessionParticipant (#522)
- **domain:** Add Citizen registry foundation (User, ChannelIdentity, IUserRegistry, ICitizenRegistry) (#525)
- **gateway:** Typed CitizenId Sender on InboundMessage + channel-boundary resolution (#528)
- **gateway:** Add Conversation.Initiator + ListForCitizenAsync (Phase 2b) (#530)
- **gateway:** Convert compaction to mark-not-delete with SessionEntry.IsHistory (#531) (#533)
- **gateway:** Canonical IConversationResetService + delete systemPromptInitialized flag (#536, #537) (#538)
- **scenarios:** Add VirtualWorld harness + first-wave bug-probing citizen scenarios (#543)
- **sessions:** Add ISessionStore.ListByConversationAsync + fix FileSessionStore orphan-on-restart bug (Phase 4.3 / F-7) (#546)
- **subagents:** Require ConversationId + eager pin (Phase 4 / F-6) (#547)
- **agents:** Route named↔named exchanges through IConversationStore (Phase 4 / F-3) (#548)
- **domain:** Add AgentKind on AgentDescriptor + route sub-agent isolation gate via typed property (Phase 5 / F-6 step 1) (#556)
- **gateway:** Migrate GatewayHost.ResolveSessionType off SessionId.IsSubAgent substring (Phase 5 / F-6 step 2) (#557)
- **gateway:** Migrate SessionsController.Seal off SessionId.IsSubAgent substring (Phase 5 / F-6 step 2b) (#559)
- **gateway:** SubAgentSpawnMode = Embody | Mirror discriminated union (#563)
- **agents:** Add integration-mock LLM provider with scripted responses (#588)
- **cli+tests:** Non-interactive provider commands, integration-mock provider, and CLI integration test harness (#594)
- **domain:** Add WorldId to Conversation (#613 P9-A) (#614)
- **sessions:** Backfill orphan sessions to per-agent legacy conversations (#616)
- **gateway:** Auto-archive A↔A conversations on exchange end (P9-C) (#625)
- **portal:** Update browser tab title to agent - conversation name (#610)
- **portal:** Show conversation ID in title tooltip for debug and e2e tests (#609)
- **gateway:** Flip Session.ConversationId to non-nullable (P9-B-2, closes #627) (#641)
- **cron:** Invert CronJob ↔ Conversation ownership (P9-D, closes #643 closes #640) (#644)
- Collapse SessionType — delete Cron/Soul/Heartbeat values (P9-E, closes #645) (#646)
- **channels:** Replace string conversationId with typed ChannelStreamTarget on stream adapter methods (PR1 of W-5, #677) (#679)
- **cli:** Add branded banner and refresh readme (#683)
- **channels:** Add ConversationId to ChannelStreamTarget; route SignalR by conversation (#682) (#684)
- **channels:** Drop hardcoded RequestedSessionId from TuiChannelAdapter (#691) (#695)

### 🐛 Bug Fixes

- **portal:** Add missing comma after resetTextareaHeight in chat.js (#469)
- **agents:** Inherit parent conversation id in sub-agent sessions (#468) (#470)
- **gateway:** Rebase concurrent additions over compaction (closes #532) (#540)
- **gateway:** Add caller authorization to Delete / Suspend / Resume session endpoints (closes #558) (#561)
- **gateway:** Per-session lock on cross-world relay write→prompt→reload window (#564)
- **gateway:** Cancellation must not seal cross-world relay session (#553) (#565)
- **gateway:** Heartbeat ack-prune must not clobber concurrent session activity (#573) (#574)
- **compaction:** Fix rogue cron conversation, add user notification, and evict stale handle (#602)
- **portal:** Cron sessions bleed into new conversation session ID (#607)
- **portal:** Replace literal x buttons with icons; title-case agent ID fallback (#639)

### 📖 Documentation

- **isolation:** Frame strategies as user-protection security boundary (#511)

### 🔨 Refactor

- **domain:** Migrate AgentId to Vogen value object (#513)
- **domain:** Migrate ConversationId and SessionId to Vogen value objects (#517)
- **gateway:** Centralise LLM-visible session-history projection (#534) (#535)
- **domain:** Remove ThreadId; fold native threads into ChannelAddress (#539)
- **domain:** Introduce JobId/RunId Vogen value objects (closes #501) (#541)
- **gateway:** Route cross-world receiver through IConversationStore + delete dead communicator + remove SessionId factories (#549)
- **gateway:** Replace IsObjectiveMet substring heuristic with finish_agent_exchange tool (#550)
- **gateway:** Close GatewaySession proxy gap; ban reach-through (#570)
- **gateway:** Extract SessionStreamReplay from GatewaySession (#575) (#576)
- **dispatching:** Lift InboundMessage overrides into typed InboundMessageContext (#580) (#581)
- **gateway:** Migrate router + host to typed InboundMessage routing hints (#582) (#585)
- **gateway:** Delete legacy InboundMessage routing fields; promote typed RoutingHints (#586) (#593)
- **gateway:** Unify compaction paths behind ISessionCompactionCoordinator (#608)
- **gateway:** Move Participants from Session to Conversation (P9-F, closes #657) (#659)
- **gateway:** List conversations by participant for responder-side visibility (P9-G, closes #661) (#663)
- **gateway:** Delete Session.AgentId; Conversation owns agent identity (P9-H, closes #662) (#664)
- **gateway:** Hydrate Session AgentId from Conversation; drop legacy agent_id column (P9-I, closes #674) (#675)

### 🧪 Testing

- **scenarios:** Add citizen scenario suite and virtual channel adapter (foundation) (#515)
- **channels:** Add SignalR reliability scenario suite (regression pins) (#542)
- **integration:** Add Copilot device-code probe + mock provider list verification (#597)
- **e2e:** CLI audit fixes + new BotNexus.Integration.E2E.Tests project (#600)
- **e2e:** Phase 1 — portal coverage expansion (#631)
- **e2e:** Phase 2 - portal settings, page title, tabs, config pages, AskUser, panels, header actions, agent dashboard (#638)
- **e2e:** Phase 3 - cron session isolation, parallel execution, session history tests (#642)
- **e2e:** Implement Playwright user-journey flows + portal data-testid hooks (#598) (#601)
- **e2e:** Phase 4 - compaction continuity tests (issue #655) (#658)

### ⚙️ Miscellaneous

- **gateway:** Rename single-shot CompletionReason "objectiveMet" to "singleShot" (#552) (#569)
- **tests:** Omit unvalidated loose tests/ projects from slnx (#617) (#619)
- **ci:** Add TIA script and reorganize workflows (#660)

## [0.1.13] - 2026-05-21

### ✨ Features

- Implement WORLD.md world-level shared agent instructions (#249) (#259)
- **canvas:** Add canvas tool event flow and portal tab UI (#278)
- Strip ANSI escape sequences from shell/exec/process tool output (#294) (#296)
- **mobile:** Initial mobile Blazor WASM client (#304)
- **scripts:** Cross-platform watchdog installer with cron and systemd support (#317)
- **blazor:** Extract shared services into BlazorClient.Core Razor Class Library (#316) (#318)
- **telegram:** Switch to MarkdownV2 parse mode for proper markdown rendering (#328)
- **security:** Implement ExecApprovalManager with hardened approval tokens (#330)
- **channels:** Enable image send and receive support for all channels (#319) (#327)
- **security:** Redact secrets at write time and enforce sub-agent spawn policy (#333)
- **mobile:** Render markdown in assistant messages using shared JS renderer (#339)
- **workspace:** Delete and inline text editing (#348)
- **#359:** Conversation list auto-refresh via SignalR (#361)
- **#312:** HeartbeatAction + HeartbeatTrigger + SessionType.Heartbeat (Phase 1) (#365)
- **#312:** Phase 2 - transcript pruning + updatedAt preservation on HEARTBEAT_OK (#369)
- **#312:** Phase 3 - ActiveHoursConfig + smart cron expression generation (#373)
- **#312:** Phase 4 - HEARTBEAT.md emptiness pre-check in HeartbeatAction (#386)
- **workspace:** Scaffold workspace files for existing agents on startup (#388)
- **command:** Add /context slash command with context window usage breakdown (#389)
- **agents:** Implement ask_user agent tool (#301)
- **canvas:** Scope canvas to conversation via ConversationId parameter (#421)
- **cli:** Add cron subcommand -- list, get, delete, run, enable, disable (#426)
- **canvas:** Persist canvas HTML per conversation (#427)
- **memory:** Add pre-compaction memory flush turn (#428)
- **conversation:** Add message action to dispatch to existing conversations (#429)
- **gateway:** Add server default timezone for get_datetime tool fallback (#430)
- **conversations:** Add per-conversation instructions injected into system prompt (#432)
- **agents:** Add list_agents tool for agent discovery (#434)
- **providers:** Add stream setup timeout to abort stalled provider connections (#435)
- **extensions:** Add config schema declaration and enabled flag to extension manifest (#437)
- **scripts:** Add initialization and build scripts for agent setup
- **skills:** Include source path for available skills in list response (#439)
- **portal:** Add client-side preferences system with auto-expanding chat input (#440)
- **portal:** Add agent dashboard homepage with card grid (#438)
- **provider:** Add ProviderLoggingHandler DelegatingHandler for provider HTTP tracing (#454)
- **portal:** Route canvas to active conversation in portal client (#448)
- **memory:** Flush memory on session reset before archiving (#462)
- **portal:** Add extensions config panel with schema display (#463)
- **agents:** Add targetAgentId to spawn_subagent to spawn real registered agents (#464)
- **agents:** Add SubAgentRoles for role-based agent_converse grants (#465)

### 🐛 Bug Fixes

- **cli:** Ship prompt samples as embedded CLI resources (#247)
- Show effective configuration in API and UI (#268)
- Remove redundant agent ID label from chat canvas header (#292) (#293)
- **portal:** Strip ANSI escapes, decode Unicode, and add copy buttons in tool output (#295)
- **mobile:** Make mobile publish non-fatal; fix StaticWebAssetBasePath; guard index.html (#308)
- **mobile:** Derive gateway URL from NavigationManager at runtime (#309)
- **mobile:** Notify state after LoadConversationsAsync so dropdown re-renders (#310)
- **mobile:** Guard HandleStateChanged against ObjectDisposedException (#311)
- **mobile:** Move base href before stylesheets; remove PWA manifest (#313)
- **mobile:** Async OnAgentChanged — reload conversations and clear state on agent switch (#315)
- **workflows:** Fix daily-doc-updater safe_outputs bundle failure (#300)
- **mobile:** Force conversation dropdown re-render on agent switch via @key (#334)
- **mobile:** Store baseUrl in MobileGatewayClient to fix ERR_NAME_NOT_RESOLVED (#336)
- **reports:** Fix false truncation notice; add configurable size limit (#340)
- **scripts:** Restore watchdog recovery flow and script help (#346)
- **cron:** Update tests for API-backed CronConfigPanel (#288) (#347)
- **cron:** Panel polish - section headings, ID badge, fix Mustache comment bleed (#350)
- **#235:** Truncate sub-agent task prompt in spawn notification (#351)
- **#356:** Mobile chat bubble alignment -- case-insensitive role comparison (#357)
- **#358:** Collapse mobile tool call entries; tap to open detail modal (#360)
- **#362:** Flush session entries to store on TurnEnd (Option B) (#364)
- **portal:** Restore sidebar default width and delete icon alignment (#371)
- **subagents:** Fix sub-agent conversation view 404s on session history, workspace, and reports (#352)
- **compaction:** Guard against empty summary before deleting session history (#380)
- **heartbeat:** Re-provision heartbeat cron job when agent registered/updated at runtime (#391)
- **agents:** Remove done heuristic from agent_converse objective detection (#392)
- **cron:** Cron list and manage allow target-agent access (#393)
- **mobile:** Guard mobile chat auto-scroll to active agent/conversation only (#395)
- **conversations:** Update conversation UpdatedAt and notify clients on message activity (#396)
- **portal:** Refresh Workspace and Reports panels on agent turn-end (#397)
- **portal:** Exclude sub-agents from top-level agent sidebar dropdown (#398)
- **conversation:** Trigger agent turn when conversation(action=new, message=...) is called (#399)
- **cron:** Use job name as conversation title and pin conversation id after first run (#405)
- **gateway:** Preserve compaction summary when agent handle is recreated (#408)
- **cron:** Run multiple due jobs concurrently to prevent serial blocking (#410)
- **mobile:** Add URL routing to mobile Chat.razor -- restore agent/conversation on refresh and deep links (#404)
- **signalr:** Update stale session ConversationId on conversation switch (#422)
- **gateway:** Persist user message and crash sentinel before LLM call (#424)
- **portal:** Eliminate steering toggle flicker between tool calls (#431)
- **channels:** Resolve correct adapter for multi-instance same-type channel registrations (#433)
- **gateway:** Discard steering when agent is not running instead of falling through (#436)
- **tests:** Add two-arg IChannelManager.Get mock setup for fan-out tests
- **sessions:** Use long for token count intermediate sum to prevent int32 overflow (#451)
- **mobile:** Remove ApplyRouteSelectionAsync from HandleStateChanged to fix streaming lag (#452)
- **conversations:** Add bind-on-first-use to prevent duplicate portal conversations on reconnect (#443)
- **ci:** Use .NET 10 SDK in publish-cli workflow and add global.json (#457)
- **portal:** Hide desktop agent dropdown on mobile and route to /mobile/ path (#458)
- **portal:** Defer conversation list refresh while agent is streaming (#460)

### 📖 Documentation

- **user-guide:** Document ANSI escape stripping in shell tool output (#306)
- **agents:** Add conventional commit rules for PR titles (#324)
- **readme:** Update project structure, features, and fix stale links (#368)
- Fix stale memory/daily/ path in getting-started-release (#372)

### 🔨 Refactor

- **mobile:** Replace MobileState/MobileGatewayClient with Core services (#338)
- **gateway:** Remove legacy in-core skills prompt pathway (#461)

### 🧪 Testing

- **#349:** Stub BotNexus.splitter.init in WorkspacePanelTests bUnit fixture (#353)
- **gateway:** Replace Task.Delay(20) race with SemaphoreSlim ready-signal for #374 (#394)
- **gateway:** Fix sad-path test assertion for SignalR originator guard (#423)

### ⚙️ Miscellaneous

- Finalize examples layout and remove root agents samples (#277)
- Adding agentic workflows
- **scripts:** Install npm deps in repo init (#450)
- **extensions:** Add configSchema and enabled to all extension manifests (#449)

## [0.1.12] - 2026-05-15

### 📖 Documentation

- Azure Service Bus channel — user guide and integration reference (#243)

### ⚙️ Miscellaneous

- Remove obsolete solution files for BotNexus.Probe

## [0.1.11] - 2026-05-14

### ✨ Features

- **cli:** Warn when update detects newer CLI version (#214)
- **channels:** Add Service Bus queue channel extension (#215)
- **agents:** Hot-reload agent edits and additions (#237)

### 📖 Documentation

- **agents:** Migrate planning references from docs/planning to GitHub Issues (#238)

### ⚙️ Miscellaneous

- Archive planning specs migrated to GitHub issues (#233)

## [0.1.10] - 2026-05-13

### ✨ Features

- **gateway:** Inject memory prompt guidance from config (#190)
- **webui:** Manage locations from configuration UI (#205)
- **blazor-client:** Support URL-routed portal state (#209)
- **cli:** Show applied commit subjects on update (#213)

### 🐛 Bug Fixes

- **gateway:** Harden SignalR extension dispatcher activation (#186)
- Preserve latest conversation history on refresh (#187)
- Hydrate latest Quill history after refresh (#189)
- Route steering to active session and conversation (#191)
- **cron:** Surface scheduled runs and model overrides (#193)
- **cron:** Stable per-job conversation — all runs of a job share one conversation (#195)
- Enable conversation cleanup and sidebar scrolling (#197)
- Archive old cron conversation cleanup ids (#200)
- Keep sub-agent output in originating conversation (#201)
- Archive old cron conversation cleanup ids (#202)
- **blazor:** Guard chat enter interop after refresh (#203)
- Stabilize cron conversations and sidebar scrolling (#204)
- Enforce warnings as errors (#206)
- **cli:** Deploy freshest extension build outputs (#207)
- **conversations:** Keep deleted cron conversations hidden (#212)

### 📖 Documentation

- Enforce worktree policy for all code changes (#210)

### 🔨 Refactor

- **tests:** Mirror source project structure (#188)
- **domain:** Remove GetOrCreateDefaultAsync — replace with explicit named conversations (#196)
- **config:** Unify runtime config options (#208)

### ⚙️ Miscellaneous

- **squad:** Optimize team context files (#198)

## [0.1.9] - 2026-05-08

### ✨ Features

- **gateway:** Align memory authoring with OpenClaw model (#179)

### 🐛 Bug Fixes

- **webtool:** Invalidate cached Copilot MCP client on 400/401 and retry once (#161)
- **webtool:** Add structured logging to WebSearchTool and CopilotMcpSearchProvider (#162)
- **gateway:** Steer returns session ID; router reuses Expired sessions instead of creating new (#164)
- **portal:** Toggle CSS, archive conversation, steering feedback (#166)
- **portal:** Archived conversations reappear; sidebar not scrollable (#167)
- **portal:** Duplicate messages on default conversation; history not loading (#168)
- **crossworld:** Rename CrossWorldRelayRequest.ChannelAddress to ConversationId (#175)
- **cli:** Improve update git pull cancellation handling (#181)

### 📖 Documentation

- **architecture:** Gateway flow diagrams; refactor(domain): BindingId strong type (#170)
- **planning:** Preserve OpenClaw memory alignment planning branch (#182)
- **.squad:** Merge conversation project refactor session (#183)

### 🔨 Refactor

- **gateway:** Conversation-first routing — ConversationId on InboundMessage (#169)
- **domain:** ChannelAddress and ThreadId strong types; fix StaleChannelConnectionException (#174)
- **gateway:** Extract conversation stores into gateway conversations project (#178)
- **gateway:** Route conversations through dispatcher (#180)

### ⚙️ Miscellaneous

- Remove personal identifiers from tests and docs (#159)
- **squad:** Preserve tool timeout session state (#184)

## [0.1.8] - 2026-05-05

### 🐛 Bug Fixes

- **cli:** Correct update step order — pull → stop → build → deploy → start (#157)

### 🔧 CI/Build

- **release:** Add dynamic run-name with version to publish workflow (#156)
- Remove unsupported updateWorkflowRun call from Release workflow (#158)

## [0.1.7] - 2026-05-05

### ✨ Features

- **portal:** Multi-conversation support — route SendMessageToConversation via threadId (#155)

### 🔧 CI/Build

- Optimize workflow triggers to reduce unnecessary Actions runs (#154)

## [0.1.6] - 2026-05-05

### 🐛 Bug Fixes

- **signalr:** Use agentId as stable ChannelAddress, archive stale connection-id conversations (#153)

## [0.1.5] - 2026-05-05

### 🐛 Bug Fixes

- **cli:** Stop gateway before build to release file locks (#151)

## [0.1.4] - 2026-05-05

### ✨ Features

- **cli:** Rich visual feedback using Spectre.Console (#150)

## [0.1.3] - 2026-05-05

### 🐛 Bug Fixes

- **config:** Support toolIds ["*"] as all-tools wildcard (#141)
- **tests:** Make CancellationDuringStreaming deterministic (#142)
- **sessions:** Migrate orphaned sessions to default conversation on startup (#143)
- **portal:** Filter NO_REPLY sentinel from conversation UI (#144)
- **gateway:** Demote stale SignalR bindings on disconnect and fan-out failure (#145)

### 🔨 Refactor

- **gateway:** Decouple extension tool wiring via IAgentToolContributor (#146)
- **gateway:** Binding-first routing — remove default conversation fallback, add ReattachBindingAsync (#148)

## [0.1.2] - 2026-05-04

### ✨ Features

- **config:** Backup config.json before every write (#116)

### 🐛 Bug Fixes

- **config:** Remove mandatory type field validation for channel entries (#117)
- **config:** Populate JsonElement fields from raw JSON in PostConfigure (#122)
- **gateway:** Stamp BindingId after routing to prevent duplicate Telegram messages (#124)
- **gateway:** Carry OriginatingBinding through processing — fixes ThreadId on direct sends and streaming (#129)
- **portal:** Session reset preserves conversation history (#132)
- **telegram:** Harden inbound message security — allowedUserIds, reject channel posts, disable edited messages by default (#134)
- **sessions:** ResolveByBindingAsync null threadId must only match null-thread bindings (#136)
- **gateway:** Per-address conversation routing (#139)

### 📖 Documentation

- **channels:** Add Telegram configuration and security guide (#135)

### 🔨 Refactor

- **config:** Migrate from PlatformConfigLoader to IConfiguration (#119)
- **config:** Complete IOptionsMonitor migration — remove PlatformConfig singleton (#121)

## [0.1.1] - 2026-05-03

### ✨ Features

- **telegram:** Add botnexus-extension.json manifest so CLI deploys Telegram channel (#108)
- Replace manual changelog with git-cliff + migrate docs from MkDocs to VitePress (#107)
- **telegram:** Bind each Telegram bot to a configured agent (#109)
- **bootstrap:** Meaningful scaffold templates with first-run ritual (#113)

### 🐛 Bug Fixes

- **gateway:** Conversation-scoped session persistence, ThreadId routing, binding-aware fan-out (#106)
- **actions:** Make publish-cli workflow runner-compatible and branch-agnostic
- **ci:** Fix YAML syntax error in publish-cli.yml (#111)

### 📖 Documentation

- Sync user docs with current architecture (#110)

### ⚙️ Miscellaneous

- **workflow:** Select release type and auto-increment version

## [0.1.0] - 2026-05-02

### .squad

- Merge architecture review findings
- Orchestration log, session log, merge inbox decision
- Leela gateway crash fix (2026-04-03T06:55:00Z)

### ✨ Features

- Implement complete BotNexus C# solution
- Add WebSocket endpoint to BotNexus.Gateway
- Add activity stream, REST API endpoints, web UI project, and enhanced WebSocket monitor mode
- Add workflows for Squad project management
- Initialize BotNexus project with agent charters and history
- Update config.json with default model and agent overrides
- **core:** Add IOAuthProvider and IOAuthTokenStore abstractions
- **core:** Add IExtensionRegistrar and BotNexusExtension attribute
- **config:** Refactor configuration models for dynamic extensions
- **gateway:** Implement multi-agent message routing
- **providers:** Integrate ProviderRegistry into DI and agent resolution
- **loader:** Add dynamic extension assembly loader with tests
- **build:** Add MSBuild targets for extension output pipeline
- **tools:** Load tool implementations through extension system
- **providers:** Load LLM providers through dynamic extension system
- **channels:** Load channel implementations through extension system
- **copilot:** Add GitHub Copilot provider with OAuth device code flow
- **auth:** Add API key authentication to Gateway endpoints
- **security:** Add assembly validation and security hardening for extensions
- **observability:** Add health checks, metrics, and structured logging
- **slack:** Add webhook endpoint for Slack Events API
- **webui:** Show loaded extensions in system panel
- **config:** Consolidate configuration to ~/.botnexus/ home directory
- **core:** Add IContextBuilder and IAgentWorkspace interfaces
- **config:** Add agent workspace properties and BotNexusHome agents path
- **agent:** Implement AgentWorkspace for per-agent file management
- **core:** Add IMemoryConsolidator interface
- **tools:** Add memory_search, memory_save, and memory_get agent tools
- **agent:** Implement AgentContextBuilder for workspace-driven context assembly
- **agent:** Register memory tools when EnableMemory is true
- **di:** Register IAgentWorkspace and IContextBuilder in service collection
- **memory:** Implement LLM-based memory consolidation
- **heartbeat:** Integrate memory consolidation trigger
- **core:** Add ICronJob and cron type abstractions
- **config:** Add centralized CronConfig model
- **agent:** Add IAgentRunnerFactory for programmatic runner creation
- **core:** Add ISystemActionRegistry for pluggable system actions
- **cron:** Implement centralized CronService with tick loop and job execution
- **cron:** Implement AgentCronJob with runner factory and channel routing
- **cron:** Implement SystemCronJob with built-in system actions
- **cron:** Implement MaintenanceCronJob for memory consolidation and cleanup
- **cron:** Add CronJobFactory for config-driven job registration
- **cron:** Update CronTool to use new ICronService API
- **cron:** Wire centralized cron service into Gateway DI
- **cron:** Add legacy AgentConfig.CronJobs migration
- **api:** Add REST endpoints for cron job management
- **observability:** Add cron metrics, health check, and activity events
- **logging:** Add file logging to ~/.botnexus/logs/
- **core:** Add IHealthCheckup diagnostic interface
- **diagnostics:** Create BotNexus.Diagnostics project with CheckupRunner
- **cli:** Create BotNexus.Cli dotnet tool with System.CommandLine
- **cli:** Add ConfigFileManager, GatewayClient, and ConsoleOutput utilities
- **diagnostics:** Add configuration checkups
- **diagnostics:** Add security checkups
- **diagnostics:** Add connectivity checkups
- **diagnostics:** Add extensions, permissions, and resources checkups
- **diagnostics:** Add auto-fix capability to doctor checkups
- **api:** Add status, doctor, and shutdown Gateway endpoints
- Enhance configuration management and agent routing
- **cli:** Add backup create/restore/list commands
- **cli:** Add backup command + foolproof BOTNEXUS_HOME test isolation
- **scripts:** Add pack, install, update packaging scripts
- **cli:** Add installable tool bootstrap and native install/update commands
- **cli:** Use %LOCALAPPDATA%\BotNexus as default install path
- **cli:** Track package source in version.json and status output
- **cli:** Add Spectre.Console for rich terminal UI
- **webui:** Add agent selector dropdown to chat
- Add extension version info to API, CLI, and install manifest
- **cli:** Add 'webui' command with dev and open subcommands
- **webui:** Show agent name and channel in session details
- Add ICommandRouter to AgentRunnerFactory and register CommandRouter in service extensions
- Implement command palette for chat input with command suggestions
- Create dev-loop script to streamline build and installation process
- Auto-register internal tools for agent sessions
- **webui:** Add model selector for new and existing sessions
- Make temperature, max tokens, and context window tokens nullable
- **webui:** Redesign tool call display with summary/detail pattern
- Add agent continuation prompting when intent detected without tool calls
- **gateway:** Add REST API endpoints for session hiding and agent CRUD
- **audit:** Add comprehensive logging for config and token operations
- **webui:** Improve agent editor form UX
- Add model listing support to providers and API
- Implement skills system with global and per-agent skill loading
- **webui:** Populate model dropdowns from /api/models
- **webui:** Add system message handling with device auth support
- **auth:** Add auto-reauth and surgical config updates
- **webui:** Add thinking indicator to chat UI
- Broadcast Copilot device auth to WebSocket clients
- Add system message infrastructure
- Add startup auth validation for providers
- **agent:** Add real-time streaming and tool progress events
- **webui:** Add real-time tool progress streaming via WebSocket
- **agent:** Stream tool progress and processing indicators to WebSocket clients
- **providers:** Implement provider response normalization layer
- Add repeated tool call detection with configurable limits
- Add streaming chat chunks with tool call support
- Add model registry for provider architecture
- Add API format handlers for provider architecture
- Complete provider architecture port to BotNexus
- **providers:** Add detailed logging for Anthropic Messages API requests
- **providers:** Add BotNexus.Providers.Core with unified LLM abstractions
- **providers:** Add OpenAI Chat Completions provider
- **providers:** Add Anthropic Messages API provider
- **providers:** Add GitHub Copilot provider with OAuth support
- **providers:** Add OpenAI-compatible provider for local inference
- **providers:** Add OpenAI-compatible provider for local inference
- **quickstart:** Enhance example with tool definition and context handling
- **agent-core:** Scaffold BotNexus.AgentCore project and test project
- **agent-core:** Add core enums (ThinkingLevel, ToolExecutionMode, AgentEventType, AgentStatus)
- **agent-core:** Add AgentMessage hierarchy and AgentToolResult types
- **agent-core:** Add AgentEvent hierarchy (10 event types)
- **agent-core:** Add AgentState, AgentContext, and configuration types
- **agent-core:** Add IAgentTool interface, hook types, and delegates
- **agent-core:** Add MessageConverter for AgentMessage ↔ Message conversion
- **agent-core:** Add ContextConverter for AgentContext → Context bridge
- **agent-core:** Add StreamAccumulator for streaming response processing
- **agent-core:** Add ToolExecutor with sequential and parallel modes
- **agent-core:** Add AgentLoopRunner — core agent loop engine
- **agent-core:** Add PendingMessageQueue for steering and follow-up messages
- **agent-core:** Add Agent class — stateful wrapper with full public API
- **coding-agent:** Scaffold BotNexus.CodingAgent project
- **coding-agent:** Add PathUtils for path resolution and safety
- **coding-agent:** Add ReadTool
- **coding-agent:** Add WriteTool
- **coding-agent:** Add EditTool
- **coding-agent:** Add ShellTool
- **coding-agent:** Add GlobTool
- **coding-agent:** Add CodingAgentConfig for configuration management
- **coding-agent:** Add SystemPromptBuilder for coding-optimized prompts
- **coding-agent:** Add GitUtils and PackageManagerDetector
- **coding-agent:** Add SafetyHooks for pre-tool-call validation
- **coding-agent:** Add AuditHooks for post-tool-call logging
- **coding-agent:** Add SessionManager for session lifecycle
- **coding-agent:** Add CodingAgent factory
- **coding-agent:** Add OutputFormatter for rich terminal output
- **coding-agent:** Add InteractiveLoop — prompt/response REPL
- **coding-agent:** Add ExtensionLoader for assembly-based tool plugins
- **coding-agent:** Add SkillsLoader for AGENTS.md context files
- **coding-agent:** Add CommandParser and wire Program.cs entry point
- **coding-agent:** Add GrepTool for file content search
- **coding-agent:** Add SessionCompactor for context management
- **coding-agent:** Add /login /logout with Copilot OAuth and auth.json persistence
- **providers-core:** Add built-in GitHub Copilot model catalog
- Add token-aware session compaction with LLM summarization
- Expand extension lifecycle with event hooks
- Upgrade system prompt builder to dynamic composition
- Port session tree model with JSONL entries and branching
- Implement OpenAI Responses provider with streaming support
- **providers:** Add context overflow detection utility
- **anthropic:** Port Claude Code OAuth stealth mode
- **openai:** Port auto-detect compat for non-OpenAI providers
- **agent-core:** Add default message converter
- **providers:** Add model registry identity utilities
- **coding-agent:** Add directory listing and context discovery
- **coding-agent:** Add list directory tool
- **coding-agent:** Add thinking level CLI option
- **coding-agent:** Record session model/thinking metadata
- **coding-agent:** Add thinking slash command
- **agent:** Add HasQueuedMessages property
- **agent:** Add runtime queue mode setters
- **providers:** Add direct Anthropic and OpenAI model registrations
- **agent:** Add MaxRetryDelayMs configuration for retry backoff cap
- **coding-agent:** Auto-persist session on assistant message completion
- **providers:** Add tool call argument validation against JSON Schema
- **providers:** Add shortHash utility for tool call ID normalization
- **coding-agent:** List directory entries up to 2 levels deep
- **coding-agent:** Discover context files via ancestor directory walk
- **coding-agent:** Fix enterprise Copilot endpoint + add CLI test matrix
- **coding-agent:** Add model/provider to session header, preserve test sessions
- **coding-agent:** Add --log option for console output mirroring
- **gateway:** Add Gateway Service architecture and project structure
- **gateway:** Add default communicator and auth handler
- **channels:** Add Telegram channel adapter stub
- **channels:** Add TUI channel adapter stub
- **gateway:** Add thinking delta stream events
- **webui:** Phase 2 enrichment — thinking, tools, sessions, agents, activity
- **gateway:** Add agent configuration source contracts
- **gateway:** Add agent descriptor validator
- **gateway:** Add file-based agent configuration loading
- **gateway:** Implement local cross-agent calling
- **webui:** Add error states, loading indicators, and reconnection support
- **gateway:** Add steering and follow-up queuing support
- **gateway:** Add sandbox, container, and remote isolation strategy stubs
- **gateway:** Add platform configuration system (.botnexus/config.json)
- **gateway:** Add config validation endpoint
- **gateway:** Add multi-tenant API key support
- **webui:** Enhance thinking/tool display and steering UX
- **gateway:** Wire provider registration, auth manager, and platform config agent source
- **gateway-abstractions:** Add channel capability flags to IChannelAdapter
- **gateway:** Add session lifecycle with status and cleanup service
- **gateway:** Add agents directory to BotNexus home
- **gateway:** Add config.json file watcher with hot reload
- **webui:** Add thinking toggle, tool inspector, session reconnection, agent selector, activity feed, and steering controls
- **gateway-api:** Add auth middleware to ASP.NET pipeline
- **gateway-api:** Add OpenAPI/Swagger specification
- **gateway:** Enforce MaxConcurrentSessions in agent supervisor
- **gateway:** Validate isolation strategy exists before creating agent
- **gateway-api:** Add session locking for WebSocket connections
- **gateway:** Implement agent workspace manager and context builder
- **cli:** Create BotNexus CLI with config validation commands
- **channels:** Add WebSocket channel adapter integrating with channel pipeline
- **gateway-api:** Add /ws/activity endpoint for activity stream subscriptions
- **channels-tui:** Add console input loop for TUI channel
- **gateway:** Implement cross-agent calling
- **webui:** Enhance session management, UX, and production quality
- **gateway-api:** Add paginated session history endpoint
- **gateway:** Enforce max call chain depth
- **gateway:** Add cross-agent timeout protection
- **gateway:** Support configurable session store selection
- **gateway:** Add websocket reconnect sequencing
- **gateway:** Add session suspend and resume endpoints
- **gateway:** Add session queueing and tui steering
- **webui:** Add processing status bar and tool error display
- **gateway:** Add agent descriptor update endpoint
- **gateway:** Add IExtensionLoader interface and extension models
- **gateway:** Implement AssemblyLoadContext-based extension loader
- **gateway:** Integrate extension loading into Gateway startup
- **gateway:** Add config schema validation and path resolver
- **telegram:** Add Telegram Bot API HTTP client
- **telegram:** Implement long polling and message routing
- **telegram:** Implement send with markdown and streaming support
- **gateway:** Add GET /api/channels endpoint
- **gateway:** Add GET /api/extensions endpoint
- **gateway:** Add session metadata GET/PATCH endpoints
- **webui:** Add channels panel with capability display
- **gateway:** Add config version field for schema evolution
- **webui:** Add extensions panel with loaded extension info
- **gateway:** Add per-client rate limiting middleware
- **gateway:** Add correlation ID middleware
- **gateway:** Add agent health check endpoint
- **gateway:** Add SQLite session store implementation
- **gateway:** Add agent lifecycle events to activity stream
- **gateway-api:** Add Serilog and OpenTelemetry foundation
- **providers:** Add OTel activity spans for provider streaming
- **observability:** Add gateway and agent tracing spans
- **observability:** Add channel and session tracing spans
- **gateway:** Persist API-managed agent configs
- **gateway:** Enrich platform agent/provider config schema
- **gateway:** Support prompt file arrays and workspace templates
- **gateway:** Add layered model filtering
- **gateway:** Port BotNexus system prompt builder
- **gateway:** Regenerate system prompt on session reset
- **memory:** Add core memory store and lifecycle indexing
- **memory:** Wire agent memory config and tool integration
- **webui:** Add agent debug info panel
- **gateway:** WebUI client versioning + server-side client log endpoint
- **gateway:** Auto-version from build timestamp — no manual updates
- **webui:** Replace agent debug modal with full agent page panel
- **webui:** Complete agent config fields + add cron canvas view
- **cron:** Add per-job timezone support
- **webui:** Add timeline view with session dividers
- **gateway:** Add SessionTool for agent session management
- **webui:** Add Stop Gateway button to sidebar footer
- **skills:** Add BotNexus.Skills library with TDD tests
- **skills:** Add SkillTool and wire into gateway context builder
- **webui:** Show notification when agent loads a skill
- **gateway:** Add extension hook system with BeforePromptBuild and tool call hooks
- **gateway:** Add extension hook system with BeforePromptBuild and tool call hooks
- **tools:** Add process tool extension for background process management
- **gateway:** Wire BeforeToolCall/AfterToolCall hooks into agent execution
- **gateway:** Add dangerous tool registry and tool policy system
- **skills:** Add security scanner for skill scripts (ported from OpenClaw)
- **scripts:** Add deploy-extensions.ps1 for runtime deployment
- **mcp:** Add MCP extension with stdio transport and tool bridging
- **mcp:** Add HTTP/SSE transport for remote MCP servers
- **mcp:** Add security integration for MCP tools
- Ran squad nap to cleanup
- **mcp-invoke:** Add invoke_mcp tool extension for skill-driven MCP access
- **web:** Add WebTools extension with CopilotMcp search provider
- Enhance JoinSession with resume detection and session metadata
- Auto-reactivate expired sessions on new message
- Archive sessions on reset instead of deleting
- Add session compaction model, interface, and store support
- Implement LLM-powered session compactor
- Wire session compaction into gateway host and DI
- **gateway:** Add sub-agent spawning abstractions and configuration
- **gateway:** Implement DefaultSubAgentManager with background execution
- **gateway:** Add sub-agent spawn/list/manage tools
- **gateway:** Wire sub-agent DI registration and tool resolution
- **api:** Add sub-agent REST endpoints and WebSocket lifecycle events
- **webui:** Add sub-agent status panel with real-time updates
- **squad:** Sub-Agent Spawning feature delivery complete (Wave 1-4)
- Add deliver-spec prompt for full squad delivery cycles
- **webui:** Add per-session state management for streaming
- **gateway:** Add multi-session subscription foundation
- **webui:** Multi-session client model — zero-server-call switching
- **gateway:** Add agent delay/wait tool for in-session pausing
- **gateway:** Add file watcher tool for change-triggered agent loops
- **api:** Add cross-session channel history endpoint for infinite scrollback
- **webui:** Infinite scrollback with IntersectionObserver
- **webui:** Add floating 'New messages' button when scrolled up
- **gateway:** Add IPathValidator and FileAccessPolicy for per-agent file permissions
- **tools:** Integrate IPathValidator into all file tools
- **gateway:** Add glob pattern support to file access permissions
- **docs:** Add comprehensive domain model documentation for BotNexus
- Add BotNexus.Domain project with value objects and smart enums
- Complete wave2 session model and value object adoption
- Add sub-agent archetype identity to replace parent ID reuse
- **sessions:** Implement existence dual-lookup in all session stores
- **domain:** Add WorldIdentity record type
- **gateway:** Improve startup logging with version and component info
- **gateway:** Add world identity configuration and startup logging
- **api:** Add GET /api/world endpoint
- **webui:** Display world identity in header
- **api:** Add structured log viewer endpoint
- **gateway:** Create BotNexus.Gateway.Contracts project
- **domain:** Add WorldDescriptor, Location, and CrossWorldPermission types
- **domain:** Add ConversationRequest and agent-to-agent session types
- **domain:** Add SoulSession types and TriggerType.Soul
- **gateway:** Implement AgentConversationService for peer agent communication
- **gateway:** Populate WorldDescriptor from config and runtime discovery
- **prompts:** Create BotNexus.Prompts with IPromptSection pipeline
- **api:** Expand /api/world endpoint with full WorldDescriptor
- **gateway:** Implement SoulTrigger with daily session lifecycle
- **tools:** Add agent_converse tool
- **gateway:** Add conversation cycle detection
- **domain:** Add cross-world communication types
- **channels:** Implement CrossWorldChannelAdapter
- **gateway:** Add cross-world message relay and authentication
- **api:** Add cross-world federation endpoints
- **sessions:** Create shared session primitives library
- **gateway:** Implement session visibility filtering by SessionType
- **gateway:** Add sealed channel continuation pruning
- **webui:** Extract api.js — shared API client, channel helpers, version check
- **webui:** Extract ui.js — DOM refs, utilities, status, modals, mobile sidebar
- **webui:** Extract session-store.js — SessionStore, StoreManager, state accessors
- **webui:** Extract hub.js — SignalR connection builder, hubInvoke, reconnect
- **webui:** Extract events.js — SignalR event handlers, sub-agent state
- **webui:** Extract sidebar.js — sessions, agents, channels, config, activity, cron
- **webui:** Extract chat.js — chat canvas, messages, commands, sub-agents
- **webui:** Add module entry point, rename original, update index.html
- **webui:** Add hash-based URL routing for agent channels
- **webui:** Update page title to show current agent and channel
- **webui:** Add sidebar collapse toggle with persistent state
- **gateway:** Inject ISessionStore into DefaultAgentSupervisor for history loading
- **gateway:** Populate AgentExecutionContext.History from session store on agent creation
- **gateway:** Inject prior history into agent initial state in InProcessIsolationStrategy
- **webui:** Add collapsed sidebar rail with section icons
- **webui:** Route client debug events to server log endpoint
- Add start-probe.ps1 launch script for BotNexus.Probe
- **api:** Add git commit hash to /api/version endpoint
- **gateway:** Add FileAccessPolicyConfig to agent configuration
- **gateway:** Map file access policy in agent config sources
- **gateway:** Add world-level file access policy with per-agent override
- **gateway:** Add LocationConfig to gateway configuration
- **gateway:** Implement ILocationResolver with config-based resolution
- **gateway:** Merge config locations into WorldDescriptor
- **gateway:** Resolve @location references in file access policies
- **cli:** Add botnexus locations list|add|update|delete commands
- **cli:** Add botnexus doctor locations health check
- **api:** Add locations CRUD and health check endpoints
- **webui:** Add locations management view with CRUD
- **webui:** Add location health check UI
- Sub-agent sidebar visibility and seal endpoint (Wave 1)
- Read-only sub-agent conversation view and seal tests (Wave 2)
- **subagent-ui:** Wave 3 — edge cases, reactive updates, docs
- **webui:** Add gateway uptime display to sidebar footer
- Add research document on session lifecycle fragmentation
- **commands:** Extension-contributed commands — Waves 1-4
- **config:** World-level extension config defaults with agent-level deep merge
- Add MkDocs Material documentation site infrastructure
- Add memory backfill CLI command and refactor MemoryIndexer
- Add site polish and first-agent tutorial
- Add media pipeline domain types and contracts (Wave 1)
- Implement media pipeline core with handler dispatch and telemetry (Wave 2)
- Add SendMessageWithMedia hub method and Whisper transcription extension (Wave 3a)
- Add WebUI audio recording with MediaRecorder API (Wave 3b)
- Add audio playback and transcription progress indicator in WebUI (Wave 4a)
- Wake idle parent agent on sub-agent completion
- Add typed SubAgentCompletionMessage and completion deduplication
- Add heartbeat config models and system prompt wiring (Wave 1)
- Heartbeat cron provisioning, quiet hours, and HEARTBEAT_OK handling (Wave 2)
- Add includeSystem parameter to cron list for debugging heartbeat jobs
- Process-based integration test harness with JSON scenarios
- Dynamic config reload and config CRUD API
- Config API integration test scenarios + REST action support
- Config management API + dynamic reload + integration tests (7/7 pass)
- Test client mirrors WebUI behavior + file logging
- Tool-blocking + stress scenarios with timing assertions
- Parallel track execution model for integration tests
- Parallel MCP server startup + MCP ping integration test
- Context diagnostics API for LLM context inspection
- **gateway:** Add IEndpointContributor and IApiContributor extension interfaces
- **channels:** Extract SignalR into BotNexus.Channels.SignalR channel extension
- **gateway:** Remove hardcoded SignalR hub mapping from Program.cs (Wave 4)
- **channels:** Add Blazor WASM client for SignalR gateway hub
- **blazor:** Add multi-agent concurrent sessions (Phase 3)
- **blazor:** Phase 4 feature parity — markdown, tools, history, steer
- **blazor:** Host Blazor WASM SPA at /blazor/ from gateway
- **blazor:** Add Restart Gateway button to sidebar footer
- **blazor:** Collapsible agent tree sidebar with channels + sub-agents
- **blazor:** Full feature parity — thinking, sub-agents, audio, commands, config, toggles
- **cli:** Add install and build commands
- **cli:** Add serve command with gateway and probe subcommands
- **cli:** Register install, build, and serve commands in Program.cs
- **tools:** Add configurable shell preference for ShellTool
- **blazor:** Add platform configuration page
- **gateway:** Serve Blazor UI at root and auto-init config on first serve
- **cli:** Add reusable wizard framework with Spectre.Console prompts
- **cli:** Add provider command with setup wizard and OAuth flow
- **cli:** Auto-build on serve and skip test projects in build
- **blazor-client:** Restructure layout with banner, sidebar, and agent dropdown
- **blazor-client:** Move agent dropdown under Chat heading in sidebar
- **blazor-client:** Add config section sub-nav in sidebar
- **cli:** Stream MSBuild output with live progress rendering
- **blazor-client:** Add Agents page and CLI wizard for agent management
- **cli:** Add gateway process manager (Wave 1)
- **cli:** Refactor gateway command with start/stop/status/restart subcommands
- **blazor:** Add read-only sub-agent session viewing
- Enhance dynamic configuration reload and improve CLI gateway management
- **cli:** Expose gateway as top-level command in addition to serve gateway
- **config:** World-level agent defaults with field-level inheritance (#12) (#13)
- **conversations:** Conversation Model — Waves 1-3 (domain, routing, REST API, live tests) (#20)
- **cli:** Add --target to all remaining CLI commands (#56)
- **cli:** Make BotNexus.Cli a publishable dotnet global tool (#59)
- **cli:** Add botnexus update command (#65)
- **blazor:** Refactor config page into dedicated panel components (#74)
- **blazor:** Add FeatureFlagsService backed by localStorage (#77)
- **portal:** Wave 1 — IGatewayRestClient + IPortalLoadService + IsReady gate (#81)
- **blazor-client:** Add client state store (#82)
- **blazor:** LocalStorage conversation history cache (v2 — current architecture) (#98)

### 🐛 Bug Fixes

- **bus:** Remove sync-over-async deadlock hazard in MessageBusExtensions
- **cron:** Update last run properties to use LastRunStartedAt and NextOccurrence
- **config:** Use minimal default config for clean first-run
- **loader:** Treat missing extension folders as warnings not failures
- **health:** Distinguish unconfigured from broken in health checks
- Cross-platform test failures and BOTNEXUS_HOME isolation
- **copilot:** Correct API endpoint path from /v1/chat/completions to /chat/completions
- Install script config update + CopilotProvider test endpoint path
- Install-cli script detection and dev docs to use scripts
- Resolve all build warnings across solution
- **cli:** Handle non-interactive stdin for Spectre.Console prompts
- **copilot:** Clear stale token on 401/403 for re-authentication
- **gateway:** Only show 'default' agent when no named agents configured
- Update tests for agent API and encoding improvements
- **cli:** Detach gateway process from CLI console on start
- **webui:** Tighten chat message spacing and remove whitespace bloat
- **copilot:** Add missing token exchange step for API authentication
- Include tool_calls and tool_call_id in message history
- **session:** Persist agent name and derive timestamps from history
- **webui:** Remove excessive whitespace and handle tool calls in live responses
- **webui:** Eliminate empty assistant bubbles and orphaned streaming indicators
- Enable incremental builds in dev-loop
- Clean stale nupkg files and make install.ps1 resilient to unknown packages
- **gateway:** Correct invalid route pattern for session hide/unhide endpoints
- Remove hardcoded Temperature/MaxTokens defaults for nullable config
- Model resolution - ensure settings reflect configured model
- Remove hardcoded gpt-4o defaults to respect agent-configured models
- Resolve nullable model warnings and update tests for streaming
- Consistency audit - API and docs alignment
- **gateway:** Add WebSocket agent query parameter support and enhanced provider logging
- **copilot:** Handle both JSON string and object formats for tool call arguments
- Merge multiple choices from Copilot API response
- Use absolute URIs in all API format handlers
- Resolve nullability warnings in test code
- **anthropic:** Implement complete tool call support
- **agent-core:** Replace BotNexus.Core/Providers.Base refs with Providers.Core
- **providers-core:** Add Details field to ToolResultMessage
- **coding-agent:** Write default config.json on first run and add Copilot OAuth setup guide
- **coding-agent:** Register built-in API providers at startup
- **coding-agent:** Register built-in models at startup and fix model resolution
- **providers:** Disable OpenAI completions store flag
- **providers:** Include Openai-Intent in copilot dynamic headers
- **providers:** Narrow adaptive thinking model detection
- **providers:** Use bearer auth for copilot token exchange
- **coding-agent:** Align edit tool schema with pi-mono
- **coding-agent:** Rename shell tool to bash
- **agent-core:** Handle thinking stream, tool result lifecycle, and runtime reasoning
- **coding-agent:** Replace reflection-based message conversion
- Resolve provider registration for Copilot models
- Align MessageTransformer thinking and tool-result handling with pi-mono
- Align Anthropic and OpenAI provider behavior with pi-mono
- Prevent ExtensionRunner crash when extension throws
- Resolve mutable dictionary leak and add null guards on providers
- **anthropic:** Remove obsolete fix summaries and analysis documents
- **providers:** Correct Anthropic protocol fidelity
- **coding-agent:** Correct tool truncation, fuzzy edit, BOM, and token estimation
- **providers:** Add OpenAI Responses reasoning, caching, and xhigh clamping
- **agent-core:** Correct event emission, hook ordering, and loop guards
- **providers:** Skip empty content blocks in Anthropic message conversion
- **coding-agent:** Add image support and byte limit to ReadTool
- **coding-agent:** Add context lines, case-insensitive search to GrepTool
- **coding-agent:** Improve compaction, skills validation, and tool safety
- **coding-agent:** Add file mutation queue, glob limits, edit no-change detection
- **providers:** Correct tool strict mode and thinking budget defaults
- **anthropic:** Add ExtraHigh model guard for max effort
- **providers:** Add metadata, empty message skip, cache TTL, maxTokens defaults
- **openai-responses:** Use input messages for system prompt
- **agent-core:** Add handleRunFailure with synthetic error message and agent_end event
- **agent-core:** Restore steering-first queue priority in ContinueAsync
- **agent-core:** Case-sensitive tool lookup and QueueMode default
- **coding-agent:** Auto-trigger compaction in non-interactive mode
- **coding-agent:** Validate compaction cut-point preserves tool pairs
- **coding-agent:** Map SystemAgentMessage in convertToLlm delegate
- **coding-agent:** BOM stripping in EditTool and grep default max
- **anthropic:** Use original reasoning level for adaptive effort mapping
- **docs:** Consistency review — align docs with code after port audit sprint
- **agent:** Preserve assistant content blocks and converter parity
- **agent:** Align continue and lifecycle semantics with pi-mono
- **agent:** Emit parallel tool completions inline
- **providers:** Align core thinking and stream utilities
- **providers:** Align anthropic thinking and stop-reason handling
- **providers:** Restore openai compat and signature round-trips
- **providers:** Remove BaseUrl from ModelsAreEqual comparison
- **agent:** Log swallowed listener exceptions on failure/abort paths
- **providers:** Wire StopReason.Refusal and Sensitive to provider mappers
- **agent:** Defer assistant message state add to MessageEndEvent
- **providers:** Add apiKey fallback to SimpleOptionsHelper
- **agent:** Make TransformContext optional with identity default
- **agent:** Auto-default ConvertToLlm to DefaultMessageConverter
- **tools:** Align byte limits to 50*1024 matching TypeScript
- **tools:** Align line truncation suffix to match TypeScript
- **tools:** Implement proper context-based unified diff in EditTool
- **tools:** Detect and use Git Bash on Windows for ShellTool
- **tools:** Resolve bashEscaped variable scoping in ShellTool
- **consistency:** Align docs and code comments with Phase 4 implementation
- **agent:** Wrap listener dispatch in try/catch for exception safety
- **coding-agent:** Add symlink resolution to path validation
- **agent:** Wrap hook invocations in try/catch for graceful degradation
- **docs:** Post-sprint consistency review — sync docs with P0/P1 code changes
- **agent:** Use case-insensitive tool name lookup
- **anthropic:** Include thought signature in tool_use blocks
- **anthropic:** Support object-typed toolChoice for parallel control
- **coding-agent:** Handle explicit cancellation in ShellTool
- **coding-agent:** Respect .gitignore patterns in skills discovery
- **test:** Align port audit tests with actual implementations
- **coding-agent:** Truncate shell output from tail instead of head
- **coding-agent:** Make shell timeout configurable with 600s default
- **agent:** Re-run context transforms on each retry attempt
- **test:** Resolve test failures from Phase 5 implementation
- **providers-core:** Enforce stream termination and api matching
- **providers-anthropic:** Align thinking and header behavior
- **providers-openai:** Correct completions message and reasoning handling
- **providers-openai:** Align responses payload and header precedence
- **providers-openaicompat:** Add finish reason mappings and tool history guard
- **agent:** Only skip steering poll for steering queue
- **agent:** Keep streaming partials in context timeline
- **coding-agent:** Enforce skill metadata validation rules
- **coding-agent:** Align dynamic system prompt behavior
- **coding-agent:** Add context patterns and piped stdin prompts
- **anthropic:** Omit is_error field when false instead of sending null
- **tests:** Rename mismatched test files and seal test classes
- **gateway:** Address P1 issues from design review
- **gateway:** Persist tool events in streaming session history
- **gateway:** Support runtime default-agent updates via options monitor
- **gateway:** Use ChannelManager for adapter lifecycle and lookup
- **gateway:** Add session-store startup guidance and docs
- **gateway:** Standardize cancellationToken naming in websocket handler
- **gateway:** Document and enforce ConfigureAwait in file session store
- **gateway:** Capture streaming history in WebSocket handler
- **channels:** Align stubs to ChannelAdapterBase
- **telegram:** Register options via DI pattern
- **gateway:** Add thread-safe session history access [P0]
- **gateway:** Handle subscription exceptions in agent streaming [P0]
- **webui:** Replace per-element listeners with event delegation [P0]
- **gateway:** Block path traversal in SystemPromptFile resolution [P0]
- **gateway:** Add recursion guard to cross-agent calls
- **gateway:** Prevent duplicate supervisor create races
- **gateway:** Cap websocket reconnection retries
- **gateway:** Remove sync-context risk in agent config startup
- **gateway:** Register platform config via options pattern
- **gateway:** Improve config error messages
- **gateway:** Consistency fixes from Phase 4 review
- **gateway:** Override model BaseUrl from auth endpoint for enterprise Copilot
- **gateway:** Resolve StreamAsync task leak and auth bypass edge case
- **gateway:** Standardize CancellationToken naming and add XML docs to abstractions
- **docs:** Align API reference, README, and sample-config with actual controllers and config schema
- **webui:** Align WebSocket message handler with gateway protocol
- **channels:** Revert WebSocket DI registration to explicit type overload
- **gateway:** Validate chat input and return proper errors
- **gateway:** Migrate HttpClient to IHttpClientFactory
- **gateway:** Add configurable CORS policy
- **api:** Return 400 on mismatched agentId in PUT endpoint
- **api:** Restrict CORS methods in production
- **gateway:** Close Path.HasExtension auth bypass in middleware
- **gateway:** Sanitize AssemblyPath in extensions endpoint to prevent path leak
- **gateway:** Align SupportsThinking to SupportsThinkingDisplay in channel DTO
- **gateway:** Add stale-entry eviction to rate limiting middleware
- **gateway:** Add caller authorization to session metadata endpoints
- **gateway:** Add providers endpoint and sort models
- Add XML doc comments to suppress CS1591 warnings
- Align Add Agent form JSON with AgentDescriptor API model
- **webui:** Filter chat header models by agent provider to remove duplicates
- **webui:** Fix agent switch confirm dialog UX
- **gateway:** Default agent config to ~/.botnexus/agents/
- **gateway:** Relax provider validation — apiKey/baseUrl not required
- **webui:** Load model list on new chat start
- **webui:** Vendor marked + DOMPurify locally — no external CDN calls
- **webui:** Show agent + channel in chat title
- **tests:** Update provider validation tests for optional apiKey
- **webui:** Defer queued message display until processing starts
- **websocket:** Set control metadata for steer messages
- **webui:** Stop old agent instance on new chat + refresh panel
- **webui:** Eliminate sessions panel flicker on refresh
- **webui:** Hide agent dropdown in active sessions, show only for new chat
- **gateway:** Scope built-in tools per agent workspace
- **gateway:** Graceful cancellation handling + logs to ~/.botnexus/
- **gateway:** Decouple agent work from WebSocket lifetime
- **webui:** Paginate session history — load only recent messages
- **gateway:** Exempt WebSocket + static files from rate limiting
- **gateway:** Move rate limiter after UseWebSockets() in pipeline
- **gateway:** Reset WS reconnect throttle on clean disconnect + WRN log level
- **gateway:** Exempt localhost from WebSocket reconnect throttling
- **gateway:** Keep all session connections alive during session switch
- **webui:** Filter WebSocket messages by current session
- **webui:** Show processing indicator when switching to busy agent
- **gateway:** Hourly log rollover + decouple ChatController from request token
- **gateway:** Fix GatewayHost IHostedService DI registration
- **webui:** Add hubInvoke guard + client-side debug logging
- **gateway:** File logs at Information level, console stays Warning
- **signalr:** JoinSession returns session data + fix race condition
- **webui:** Declare healthCheckInterval — fixes init crash
- **webui:** Prevent joinSession infinite recursion
- Declare gatewayHealthy + eliminate SessionJoined callback loop
- **webui:** Defer joinSession to next tick on Connected/reconnected
- **webui:** HubInvoke was calling itself instead of connection.invoke
- **webui:** Replace join guard with version counter for race-safe switching
- **gateway:** Add diagnostic logging to channel resolution + adapter
- **webui:** ContentDelta property name mismatch — server sends contentDelta not delta
- **webui:** Strip [[reply_to]] tags + finalize prev message on MessageStart
- **webui:** Strip [[reply_to]] from history + prevent empty bubbles
- **gateway:** Pass tool arguments through to stream events
- **cron:** Add execution logging + set NextRunAt at creation time
- **webui:** Cron table uses correct property names + working action buttons
- **gateway:** Remove unused /webui auth bypass and route
- **cron:** Recompute NextRunAt on schedule change and prevent clobber
- **webui:** Remove New Chat section and rename SignalR to Web Chat
- **webui:** Eliminate channel list flicker on poll refresh
- **webui:** Move processing status from header bar to inline chat
- **gateway:** Fix steering never being injected or recorded
- **webui:** Channel display names, selection, and auto-reload
- **webui:** Fix chat canvas only using half viewport height
- **webui:** Pin chat messages to bottom of canvas
- **webui:** Remove stray </div> breaking chat-view layout
- **webui:** Move processing status to chat header
- **skills:** Address Nibbler P1 review findings
- **skills:** Address Adversary-found bugs (prompt injection, BOM, limits, duplicates)
- **skills:** Make skill discovery dynamic instead of static snapshot
- **sessions:** Persist tool_name and tool_call_id in SQLite store
- **gateway:** Replace stale exec/process tool references with bash in system prompt
- **gateway:** Nibbler consistency fixes for extension integration
- **scripts:** Deploy extensions after gateway build
- **gateway:** Fix extension type identity mismatch in AssemblyLoadContext
- **webui:** Rename Stop Gateway to Restart, use OK/Cancel dialog
- Resolve GatewayHost session queue deadlock and testhost hang
- Resolve P0-P2 Skills bugs — prompt injection, thread safety, BOM, limits
- Harden test timing patterns — increase timeouts and replace Thread.Sleep
- Complete System.IO.Abstractions audit — plug remaining raw IO gaps
- Resolve compaction provider mismatch — use configured provider for LLM calls
- Consistency review — sub-agent spawning feature alignment
- Remove unused tools from deliver-spec prompt
- **webui:** Remove redundant 'Agent is thinking' bottom indicator
- **webui:** Align steer button with input field during streaming
- **webui:** Fix session switching during active agent work
- Consistency review — session switching alignment
- **webui:** Prevent message send during session switch race window
- **test:** Fix Playwright E2E test host to use real Kestrel server
- **webui:** Fix session switching bugs caught by Playwright E2E tests
- **webui:** Fix stuck UI after session switch — flag and input recovery
- **webui:** Add safety-net timeout to prevent stuck session switch UI
- **gateway:** Fix SessionWarmupService DI registration — use factory for IHostedService
- **gateway:** Include reason in DelayTool success message
- **gateway:** Handle float/string integer parameters in DelayTool and FileWatcherTool
- **tools:** Handle float/string integer parameters across all tools
- **tools:** Fix remaining integer parsing — SessionTool, SubAgentSpawnTool, ExecTool, WebFetchTool
- **docs:** Correct SubAgentArchetype values in ddd-patterns.md
- **docs:** Correct TriggerType values in ddd-patterns.md
- **docs:** Correct TriggerType values in architecture overview
- **docs:** Remove 'Closed' from Sealed status description
- **docs:** Update design-spec status to reflect Wave 1-4 completion
- **docs:** Add BotNexus.Domain to README project structure
- **hub:** Correct value object conversion in GatewayHub methods
- **signalr:** Fix channel adapter type conversions
- **e2e:** Update test infrastructure for simplified connection model
- **e2e:** Fix scrollback tests for current DOM structure
- **e2e:** Fix session switching tests for channel-centric sidebar
- **e2e:** Fix chat sending tests for new SendMessage signature
- Make start-gateway powershell-compatible
- **scripts:** Guard Add-Type to prevent duplicate type error on re-run
- **signalr:** Include sessionId in all ContentDelta events to prevent cross-session bleed
- **webui:** Use storeManager for session ID in sendMessage
- **webui:** Disable send during session switch to prevent misrouting
- **domain:** Add channel alias resolution to ChannelKey
- **webui:** Make loading indicators per-session
- **webui:** Restore loading state on session switch
- **tests:** Use record with-expression for init-only StreamOptions.CancellationToken
- **api:** Flatten /sessions response to prevent nested session object
- **webui:** Fix channel history API paths and session data handling
- **api:** Include toolName and toolCallId in channel history response
- **webui:** Suppress auto-scroll during history batch rendering
- **webui:** Parse tool names from content as fallback
- **webui:** Apply toggle state after history render
- **webui:** Persist sidebar collapsed state to localStorage
- **webui:** Persist tool/thinking toggle state to localStorage
- **webui:** Use actual message timestamp in history rendering
- Strip [[reply_to_current]] control tags from history API and client rendering
- **webui:** Clear display state immediately on agent switch
- **webui:** Defer activeViewId until session resolved
- **webui:** Verify event handler isolation in all handlers
- **webui:** Left-align sidebar hamburger button
- **webui:** Complete storeManager to channelManager migration
- **scripts:** Add missing web and mcp-invoke extensions to deploy script
- **webui:** Persist last channel context for session restore on reload
- **e2e:** Update all test selectors from #chat-messages to per-channel containers
- **webui:** Fix steer/follow-up, scrollback, streaming, and reset bugs
- **webui:** Add init error logging + deploy script dynamic discovery
- **gateway:** Ensure user message triggers LLM call after history injection
- **gateway:** Fix session visibility to exclude cron sessions
- **gateway:** Show cron sessions as read-only in sidebar + add SessionType to SessionSummary
- **gateway:** Exempt polling endpoints from rate limiter + bump default to 300/min
- **gateway:** Filter tool entries from history injection to prevent LLM rejection
- **webui:** Add event drop logging + version commit hash + rate limit opt-in
- **probe:** Handle nested API response in correlate UI
- **scripts:** Use manifest ID for extension deploy paths
- **webui:** Clear stream state on message end regardless of active channel
- **webui:** Remove '(active)' label — all channels are always live
- **probe:** Fix correlation search to cover all data surfaces
- **probe:** Make correlate log entries expand inline with details
- **docs:** Correct architecture overview — AgentCore/Providers are independent libraries
- **docs:** Align architecture docs with correct dependency and session model
- **docs:** Replace DOM-swap model with per-channel containers in webui-connection
- **docs:** Correct stale references in development docs
- **docs:** Minor consistency fixes in planning and user-guide
- **docs:** Fix broken links to nonexistent architecture docs in training
- **probe:** Fix empty logs in web UI — JS didn't read 'items' from API response
- **docs:** Restore original comprehensive domain model document
- PathUtils.GetGitIgnoredPaths no longer throws on out-of-workspace paths
- Preserve JSON arrays as JsonElement in StreamingJsonParser
- Resolve EditTool double-parse of prepared edits argument
- Use path-safe separator in sub-agent AgentId format
- Persist sidebar section and agent-group collapse state across page refresh
- **subagent-ui:** Consistency review fixes (Nibbler)
- **commands:** DI multi-registration and case-insensitive normalization
- **extensions:** Add Gateway.Contracts + Domain to host assemblies, improve discovery logging
- Deduplicate extensions by ID during discovery to prevent topo-sort crash
- Register MemoryIndexer as hosted service to enable conversation indexing
- Bridge sub-agent lifecycle events to SignalR for real-time web UI updates
- Correct tutorial config format and replace placeholder URLs
- Enable multi-handler media pipeline and add extension manifest
- Add InternalChannelAdapter for sub-agent completion delivery
- Make hub dispatch non-blocking to prevent cross-agent session blocking
- Reactivate sealed sessions on new messages (like expired sessions)
- Integration test harness — process-based with real providers
- Capture Context.ConnectionId before fire-and-forget dispatch
- Non-blocking MCP server startup and memory initialization
- MCP transport auto-detects Streamable HTTP vs legacy SSE
- Guard WhisperTranscriptionHandler.DisposeAsync against missing assembly
- /new creates fresh session instead of reactivating sealed one
- MCP SSE transport hangs on HTTP/2 — force HTTP/1.1
- Stream events misrouted when switching agent tabs
- **blazor:** Use publish output for WASM hosting — fixes fingerprint placeholders
- Blazor SPA serving + non-collectible ALC for endpoint extensions
- Blazor SPA serving via inline middleware + non-collectible ALC
- **blazor:** SessionStatus enum serializes as string for SignalR
- **blazor:** ContentDelta handler expects AgentStreamEvent not ContentDeltaPayload
- **blazor:** AgentStreamEventType enum serializes as string for SignalR
- **blazor:** Add message timestamps and verify chronological ordering
- Deploy script skips projects without manifest — no derived IDs
- Deploy script scrubs stale extension directories after deploy
- **blazor:** History ordering + autoscroll — newest at bottom
- **blazor:** Force-scroll to bottom on session switch
- **blazor:** Defer force-scroll to next animation frame
- **blazor:** Scroll to bottom on session switch via DOM query
- **test:** Resolve flaky ShellTool and GatewayStartup tests
- **build:** Resolve all compiler and NuGet warnings
- **cli:** Always show build output to prevent apparent hang
- **build:** Strip Using items and override OutputType when SkipTests=true
- **build:** Suppress CS2008 warning from empty test assemblies
- **gateway:** Add 'copilot' → 'github-copilot' alias in ModelRegistry
- **gateway:** Invalidate cached agent instances on descriptor change
- **webui:** Restore auto-scroll by reordering OnAfterRenderAsync
- **webui:** Update comments and docs to match autoscroll fix
- **cli:** Enhance gateway PID recycling guard with MainModule validation
- **consistency:** Align docs with code for subagent session view
- **gateway:** Register MemoryStoreTool when memory is enabled
- **gateway:** Sub-agent completion now wakes parent agent session
- **blazor:** Remove stale CSS isolation link and fix enumeration crash
- Prevent security scan from flagging its own grep patterns (#7)
- Remove redundant DeployExtension target from SignalR csproj (#4)
- Cross-platform test compatibility wave 2 (#9)
- **tests:** Cross-platform test compatibility wave 3 (#10)
- **cli:** Make GatewayProcessManager cross-platform (Linux/macOS) (#11)
- Suppress NO_REPLY in SignalR adapter; expose skill path on load (#15)
- **critical:** Tool execution timeout + SQLite session store concurrency (#16)
- Ollama support, mobile UI improvements, session history, Linux fixes (#17)
- **cli:** Thread --target home path into GatewayProcessManager (#42)
- **cli:** Embed git commit SHA into gateway build via SourceRevisionId (#49)
- **config:** Default session and conversation store to SQLite not InMemory (#52)
- **blazor-client:** Fix conversation UI bugs — sidebar, title rename, agent refresh, conversation history (#50)
- **cli:** Set BOTNEXUS_HOME on spawned gateway process (#54)
- **portal:** Clear stale session ID when switching to a new conversation (#57)
- **cli:** Skip building CLI project when invoked via gateway start (#60)
- **portal:** Consolidate new session buttons — one button in chat header (#62)
- **portal:** Ensure conversation history loads on first page load (#66)
- **portal:** Show tool results correctly when loading conversation history (#68)
- **blazor-client:** Reliable conversation history load on first page visit (#73)
- **gateway:** Three bugs - conversationId in sessions list, inbound conversation routing, cron config test (#85)
- **gateway:** Probe-round3 — title validation, archive filtering, sealed session guard (#87)
- **cli:** Check StopAsync result and port availability in update command (#88)
- **portal:** Subscribe all components to IClientStateStore.OnChanged (#89)
- **cli:** Change InitCommand default listenUrl to 0.0.0.0:5005 (#96)
- **e2e:** Fix test selectors, agent, and add history persistence tests (#97)
- **portal:** Streaming and session state scoped to conversation not agent (#103)
- **ci:** Restore contents: write on release job to unblock git push (#105)

### 📖 Documentation

- **ai-team:** Merge implementation plan and team directives
- **ai-team:** Log Sprint 1 completion — all 7 foundation items done
- **ai-team:** Log Sprint 2 completion — dynamic loading fully wired
- **config:** Add comprehensive configuration guide
- **extensions:** Add extension development guide
- **architecture:** Add system architecture overview
- **ai-team:** Log Sprint 4 completion — all sprints done, 192 tests passing
- Align all documentation and code comments with current architecture
- **ai-team:** Log consistency audit and Nibbler onboarding
- **testing:** Create E2E scenario registry
- **workspace:** Add workspace and memory model documentation
- **ai-team:** Log Sprint 5 completion — workspace, memory, deployment tests done
- **cron:** Add cron and scheduling documentation
- Update Fry history with cron observability deliverables
- Align cron documentation and code comments
- **zapp:** Update history with 100% scenario coverage sprint
- **ai-team:** Log full session completion — 71 items, 395 tests, 100% scenario coverage
- **getting-started:** Add comprehensive getting started guide
- **ai-team:** Log getting started guide and Kif onboarding
- **getting-started:** Update guide to reflect first-run fixes
- **getting-started:** Add WebUI setup and usage section
- **ai-team:** Log Sprint 7 completion — CLI, doctor, hot reload done
- **scenarios:** Add CLI, doctor, and hot reload scenarios
- Align documentation with Sprint 7 CLI, doctor, and hot reload
- **.squad:** Merge Hermes test fixes + critical directives
- **getting-started:** Add extension deployment step for provider setup
- Split getting-started into release and dev guides
- Add pack step to dev workflow in getting-started guide
- Update leela history with internal tools review notes
- Record parallel publish fix in Bender's history
- Update Farnsworth history with nullable generation settings learning
- **squad:** Orchestration log for sprint 4 parallel ui and config
- Update Leela history with CLI agent add fix
- Update Leela investigation log with test validation results
- Add agent continuation prompting to history and decisions
- **squad:** Cross-agent updates for tool UI and multi-turn work (2026-04-03T04:50Z)
- **squad:** Leela investigation of token deletion and audit logging decision
- Comprehensive documentation sweep for 12 new features
- Update Bender history with model resolution fix
- Add skills system architecture and implementation summary
- Add comprehensive Skills system documentation
- **.squad:** Skills sprint completion — agent orchestration and team updates
- Update Leela history and create auth decision document
- Document agentic streaming architecture decision
- **bender:** Document streaming gateway integration
- Scribe post-sprint orchestration (2026-04-03T20:23:07Z)
- Add disabledSkills to API reference examples
- **squad:** Document multi-turn investigation findings and WebSocket routing fix
- **squad:** Build failure retrospective and prevention rules
- **squad:** Multi-turn tool call bug fix retrospective
- Add Responses API migration and loop detection documentation
- Document Pi-style provider architecture with model-aware routing
- Verify AgentLoop and Gateway integration with Pi provider architecture
- Post-sprint consistency review for Pi provider architecture
- Update Nibbler history with Pi provider review session
- Pi comparison sprint orchestration logs
- **providers:** Add comprehensive README for provider system
- **agent-core:** Add multi-sprint plan for pi-mono agent port
- **agent-core:** Enrich XML documentation to match pi-mono quality
- **agent-core:** Add comprehensive README
- **coding-agent:** Add comprehensive README
- **squad:** Archive complete + CodingAgent build log
- **audit:** AgentCore vs pi-agent-core alignment report
- **audit:** Providers.Core vs pi-ai alignment report
- **audit:** CodingAgent vs pi-coding-agent alignment report
- Merge port audit decisions — Farnsworth, Bender, Leela re-audits
- Add training documentation structure and overview
- Add provider system and streaming training docs
- Add agent loop and tool execution training docs
- Add coding agent and build-your-own training guides
- Update training docs to reflect Wave 1-2 code changes
- Log Wave 4 final results and close audit session
- Add training documentation for agent architecture
- Scribe orchestration log (P0 sprint completion)
- **squad:** Port audit sprint retro and now.md update
- **training:** Update training documentation for agent and provider architecture
- **squad:** Scribe log for port audit sprint 2
- **squad:** Record bender learnings
- Phase 3 training documentation — context discovery, thinking levels, custom agent, tool dev
- Post-sprint 3 consistency review — fix 22 discrepancies across 7 files
- Post-sprint 3 consistency review report and history update
- **squad:** Phase 3 port audit retrospective — findings, now.md update, learnings
- **audit:** Port audit findings for pi-mono vs BotNexus
- **training:** Add architecture deep-dive and provider development guide
- Fix stale tool names, parameter names, and type references across training docs and READMEs
- Update Nibbler learnings from consistency review
- **retro:** Port audit retrospective
- **agent:** Document error message sync contract between state and message
- **providers:** Add XML doc comments to StopReason enum values
- **training:** Add provider architecture training documentation
- **training:** Add agent event system training documentation
- **training:** Add tool security model training documentation
- **training:** Add building a coding agent training documentation
- **training:** Update README with focused deep-dive documentation links
- Orchestration logs and session log for port-audit remediation sprint
- Post-sprint consistency review for P0/P1 fixes
- **training:** Update tool name lookup from case-sensitive to case-insensitive
- Post-audit consistency review
- Log phase5 audit orchestration
- Update coding agent docs for Phase 5
- Agent core context transform changes
- Update provider docs for Phase 5
- Update building your own agent for Phase 5
- Create Phase 5 migration guide
- **training:** Update CodingAgent.CreateAsync signatures and built-in tools
- Consistency review — mark 16 fixed findings, align training docs and READMEs with code
- Log consistency review ceremony results
- **review:** Gateway Service architecture design review — grade A-
- **scribe:** Merge gateway reviews into decisions log
- **webui:** Update Fry history and add WebUI build decision
- **squad:** Update Bender history with P1 fix learnings
- **scribe:** Log Gateway P1 sprint + merge decisions
- **review:** Gateway P1 sprint design review
- **scribe:** Merge decision inbox into decisions.md
- **squad:** Update now.md and agent histories post P1 sprint
- **fry:** Record Phase 2 WebUI enrichment learnings
- **gateway:** Add example agent configuration
- **squad:** Log Phase 2 reviews and finalize sprint
- **squad:** Update agent histories for P0 remediation sprint
- **squad:** Update agent histories for Phase 2 architecture gaps
- **squad:** Phase 3 sprint design review — grade B+
- **squad:** Finalize Phase 3 sprint — update now.md and histories
- **hermes:** Record live integration learnings
- **squad:** Capture gateway phase4 runtime learnings
- **gateway:** Capture config and auth learnings
- **gateway:** Add gateway module README and update root docs
- **fry:** Update history with WebUI enhancements sprint
- **gateway:** Add module READMEs for abstractions, sessions, and channels
- **squad:** Update kif history with module README delivery
- **gateway:** Update README with phase 5 features and auth/lifecycle/workspace docs
- Add dev loop and deployment documentation
- Phase 5 consistency review — 0 P0, 3 P1, 6 P2, 3 P3
- **channels-tui:** Update README to reflect implemented input loop
- Add developer guide and update API reference
- **squad:** Merge gateway-phase6-batch1 decisions + inbox cleanup
- **squad:** Update cross-agent history notes for phase6-batch1
- **squad:** Merge leela phase6 design review
- **squad:** Add Phase 6 retro notes and update focus
- Phase 7 gateway gap analysis and sprint plan
- **squad:** Record sprint 7a gateway learnings
- **api:** Add OpenAPI spec generation and export
- **squad:** Record sprint 7a openapi learnings
- **bender:** Record sprint7a runtime learnings
- **squad:** Log Sprint 7A batch 1 orchestration
- **squad:** Add core context summaries to agent history files
- **squad:** Sprint 7A design review
- **squad:** Log Sprint 7A review + retro
- **squad:** Record WebUI protocol verification results in Fry history
- Update dev setup guide for Gateway Service (port 5005, config.json structure, auth flow)
- **review:** Full Gateway design review — Grade A-
- **consistency:** Fix Gateway docs-code alignment gaps post Phase 7+
- **squad:** Phase 8 retro and identity update
- Overhaul dev loop documentation, fix pre-commit hook
- **squad:** Phase 9 Wave 1 — orchestration log, session log, decisions merge
- **squad:** Phase 9 Wave 2 — orchestration log, session log, decisions merge
- **squad:** Phase 9 reviews — design A-, consistency Good
- Add CLI command reference and update getting-started guides
- **consistency:** Fix Phase 10 alignment gaps
- **squad:** Phase 10 — orchestration log, session log, decisions merge
- Add module READMEs for gateway projects and update test coverage
- Update Kif history with XML comments and module READMEs delivery
- **consistency:** Fix Phase 11 alignment gaps
- **scribe:** Wave 3 orchestration logs, session log, cross-agent history updates
- **channels:** Add WebSocket channel README
- **scribe:** Phase 12 Wave 1 orchestration logs, session log, decision merge
- **consistency:** Fix Phase 12 Wave 1 alignment gaps
- **kif:** Record API reference update learnings
- Update fry history with Wave 2 channels/extensions learnings
- **scribe:** Phase 12 Wave 2 orchestration logs, session log, decision merge
- **consistency:** Fix Phase 12 Wave 2 alignment gaps
- Add WebSocket protocol specification
- Add configuration reference guide
- Add developer guide for local development
- **scribe:** Phase 12 Wave 3 orchestration logs, session log, decision merge
- Reorganize documentation structure
- Session log - tool system refactor complete (Bender)
- Add observability architecture guide (Wave 4)
- Memory system architectural review + implementation plan
- **gateway:** Update all references from /webui to root URL
- **skills:** Add Adversary security audit report
- **skills:** Rewrite skills guide to match current implementation
- Add MCP extension architectural design document
- **squad:** Orchestration log + session log + decision merge
- **squad:** Log CopilotMcpSearchProvider session
- Draft sub-agent spawning feature documentation
- Nibbler history — session switching consistency review
- Architecture proposal — multi-session connection model
- Add design spec for agent delay/wait tool
- Design spec for file watcher tool
- Design item — per-agent file system permission model
- Design spec — infinite scrollback history across sessions
- Add per-agent file permission model design spec + review
- Update permission model spec to include glob pattern support
- Add introduction for Agent domain object in BotNexus
- Revise domain model with refined agent concepts
- Add DDD patterns developer reference guide
- Add deferred phases reference for future DDD delivery cycles
- Update architecture docs for session model changes
- Update architecture docs for session model changes
- Record kif learnings from wave 2-3 doc update
- Wave 4 DDD refactoring orchestration & decision merge (2026-04-12T04)
- **squad:** Update nibbler history with DDD consistency review
- Update infinite scrollback spec for DDD types and simplified model
- Update multi-session connection spec for DDD types and SubscribeAll pattern
- Remove duplicate SessionStatus documentation line
- Update SignalR subscribe-all contract
- Mark session switching bug as likely obsolete
- Mark multi-session connection and session visibility as implemented
- Update remaining specs for DDD types and connection model
- Add planning specs assessment after DDD/WebUI refactoring
- **domain:** Add XML docs to all Domain public types
- **contracts:** Add XML docs to Gateway.Contracts interfaces
- **prompts:** Add XML docs to Prompts public API
- **sessions:** Add XML docs to Sessions.Common public API
- **api:** Add XML docs to controllers and hub
- **agentcore:** Add XML docs to AgentCore public API
- **providers:** Add XML docs to Providers.Core abstractions
- **channels:** Add XML docs to Channels.Core abstractions
- **tools:** Add XML docs to Tools public API
- **architecture:** Add message routing, agent execution, and triggers documentation
- **architecture:** Add comprehensive system documentation after DDD refactoring
- **user-guide:** Add getting started guide
- **user-guide:** Add configuration reference
- **user-guide:** Add agent setup and extension development guides
- **user-guide:** Add troubleshooting guide
- **webui:** Document multi-tab localStorage safety in storage.js
- Update session resumption spec with post-DDD review findings
- Log BotNexus.Probe completion — 33 tests passing
- Update session-debug skill to prefer BotNexus Probe CLI
- Merge Probe CLI decision into decisions.md
- Mark file access policy spec as delivered
- Add file access policy and rate limit settings to configuration guide
- Add location management design spec and research
- **architecture:** Consolidate to concise high-level reference
- **architecture:** Add new overview.md
- Add copilot-instructions with BotNexus config location
- Update subagent-ui-visibility spec to align with current codebase
- Add design review for feature-subagent-ui-visibility
- Incorporate design review conditions into spec
- Mark feature-subagent-ui-visibility as delivered
- Update Copilot instructions and add agent document ownership guidelines
- Scribe orchestration log — Extension-Commands Wave 1 design review & delivery
- **commands:** Mark extension-contributed-commands as delivered
- Planning management skill, INDEX.md, archive consolidation, spec updates
- **config:** Mark extension-config-inheritance as delivered
- Add design review for feature-user-documentation
- Migrate existing content for MkDocs Material compatibility
- Add media handler extension guide and audio recording user guide (Wave 4b)
- Add specs for SQLite session lock bug and dynamic config reload
- Add spec for config management API with extension metadata
- Add never-guess-time rule to AGENTS.md
- Add context diagnostics spec — /context command + debug API
- Mark feature-context-diagnostics as delivered
- Phase 1 design review — SignalR extraction + extension loader
- Update blazor-webui spec with full delivery status (Phases 1-4)
- Add AGENTS.md to src/agent/ — no external dependencies rule
- Add AGENTS.md to domain/ and gateway/ with dependency rules
- Add AGENTS.md to common/ with dependency audit findings
- Document gateway hook types — clarify layer boundary with Agent.Core hooks
- **agents:** Add conventional commits guidance to AGENTS.md
- **cli:** Add install, build, and serve to CLI reference
- **getting-started:** Update setup guides to use CLI commands
- **dev-guide:** Update quick start and scripts reference for CLI
- **webui:** Mark legacy WebUI as reference-only
- **tools:** Document tool architecture and fix ProcessTool coupling
- Consolidate developer docs and update WebUI references to Blazor
- **test:** Add AGENTS.md with Shouldly conventions and test infrastructure
- Require XML doc comments on all public API with context-over-code rule
- Add guidance for private member comments in AGENTS.md
- **cli:** Add wizard dev guide, provider command reference, and alias setup
- **squad:** Add Blazor layout restructure decision and Fry history update
- **build:** Add MSBuild conventions section to AGENTS.md
- **planning:** Design review for bug-blazor-autoscroll
- **planning:** Design review for gateway detached process
- **squad:** Record Wave 1 gateway process manager completion
- **squad:** Document Wave 2 gateway command refactor completion
- **squad:** Document Wave 3 test suite completion
- **cli:** Document gateway lifecycle management commands
- **squad:** Update Leela history with Blazor subagent design review
- **squad:** Hermes Wave 2 test completion for read-only sub-agent view
- **decisions:** Document read-only banner design decisions
- **webui:** Document read-only sub-agent session viewing
- **history:** Add learning about System.CommandLine command sharing
- **planning:** Fix stale FollowUpAsync references and bug spec status
- **planning:** Mark bug-subagent-completion-wakeup as delivered
- Add no-worktrees directive to copilot instructions
- **planning:** Archive delivered bugs and add new bug specs
- **planning:** Update specs — session-switching done, config-ui partially-delivered (#14)
- **planning:** Conversation topics / omnichannel continuity design spec (#18)
- **design:** Portal load sequence redesign — architecture, interfaces, sequence diagrams (#80)
- **design:** Auto-update feature — gateway self-update from portal UI (#92)

### ⚡ Performance

- Add xunit.runner.json to all test projects + optimize GlobTool gitignore batching
- **test:** Refactor E2E tests to share Kestrel server + Playwright browser
- **test:** Share Kestrel server + Playwright browser across E2E tests

### 🔨 Refactor

- **memory:** Migrate MemoryStore to agent workspace path structure
- **agent:** Wire AgentLoop to use IContextBuilder for context assembly
- **heartbeat:** Migrate HeartbeatService to thin cron adapter
- Move channel projects to src/channels/ directory
- Archive old src/ projects to archive/src/
- Archive old test projects to archive/tests/
- Replace static registries with instance-based
- Convert Usage and StreamOptions to immutable records
- Standardize HttpClient injection across all providers
- **anthropic:** Extract message converter, request builder, and stream parser
- **providers:** Standardize JSON construction across providers
- **providers:** Replace SupportsExtraHigh string hack with LlmModel property
- **providers:** Align MessageTransformer normalizer with pi-mono signature
- **gateway:** Introduce IChannelManager abstraction
- Remove obsolete scripts and decision documents for channel stubs and WebUI rebuild
- **gateway:** Extract shared streaming session helper
- **gateway:** Extract SessionReplayBuffer from GatewaySession
- **gateway:** Decompose GatewayWebSocketHandler into focused components
- **cli:** Extract ValidateCommand handler
- **cli:** Extract InitCommand handler
- **cli:** Extract agent command handlers
- **cli:** Extract config command handlers
- **cli:** Slim Program.cs to command registration only
- **gateway:** Move SessionHistoryResponse to Abstractions.Models
- **gateway:** Inject IWebHostEnvironment in auth middleware instead of service locator
- WorkspaceContextBuilder delegates to SystemPromptBuilder
- **webui:** Single persistent WebSocket connection pattern
- **gateway:** Support websocket session switching on single connection
- **gateway:** Replace raw websocket API with SignalR hub
- **skills:** Use BeforePromptBuild hook instead of hardwired context builder
- **gateway:** Decouple extension config from core abstractions
- Adopt System.IO.Abstractions in Tools, Skills, Memory, Cron, ExecTool
- Adopt System.IO.Abstractions in CodingAgent for testable file I/O
- Adopt System.IO.Abstractions in Gateway for testable file I/O
- Extract shared OpenAI streaming logic to Providers.Core
- Eliminate NormalizeLineEndings duplication in Tools
- Clean PlatformConfig legacy root-level duplication
- Decouple cron from IChannelAdapter into IInternalTrigger
- **domain:** Improve AgentId and SessionId value object validation
- **abstractions:** Adopt AgentId and SessionId in gateway contracts
- **sessions:** Adopt typed IDs in session store implementations
- **gateway:** Adopt typed IDs in gateway runtime
- **api:** Adopt typed IDs in API controllers and hubs
- **sessions:** Extract SessionStoreBase with shared query logic
- **sessions:** Add status and null-safe shared filters
- **gateway:** Enhance OpenTelemetry instrumentation
- **prompts:** Extract shared prompt primitives to BotNexus.Prompts
- **gateway:** Move gateway interfaces to Contracts
- **gateway:** Decompose SystemPromptBuilder into section pipeline
- **coding-agent:** Delegate to shared prompt primitives
- **abstractions:** Add TypeForwardedTo for moved types
- **gateway:** Update direct project references to use Contracts
- **domain:** Extract Session domain type from GatewaySession
- **gateway:** Extract GatewaySessionRuntime for infrastructure concerns
- **gateway:** Update GatewaySession to compose Session + Runtime
- **gateway:** Update consumers to use split session types
- **sessions:** Extract JSONL parsing to shared library
- **coding-agent:** Delegate to shared session primitives
- **gateway:** Delegate FileSessionStore to shared primitives
- **webui:** Consolidate global state into SessionStoreManager
- **webui:** Remove deprecated sessionState Map
- **webui:** Remove dead code (loadEarlierMessages, loadOlderSessions)
- **webui:** Simplify event handlers to use single state source
- **hub:** Make SendMessage accept agentId+channelType with auto-session
- **hub:** Deprecate JoinSession and LeaveSession
- **webui:** Remove joinSession and simplify channel switching
- **webui:** Simplify reconnection to SubscribeAll-only
- **tests:** Extract shared MockMcpTransport to common test assets
- **gateway:** Use typed channel and hub identifiers
- **hub:** Remove redundant NormalizeChannelType
- **webui:** Extract storage.js — centralized localStorage management
- **webui:** Make tool/thinking toggles per-channel with clear storage separation
- **webui:** Move sidebar toggle into sidebar with section icon rail
- **webui:** Add ChannelContext and ChannelManager classes
- **webui:** Update index.html for channel-views container
- **webui:** Add CSS for per-channel container elements
- **webui:** Update DOM cache and scroll helpers for channel views
- **webui:** Route all rendering to channel-specific containers
- **webui:** Route events to channel contexts by sessionId
- **webui:** Update app.js event delegation for channel views
- **signalr:** Typed hub contracts and Hub<IGatewayHubClient>
- Gateway.Api has zero SignalR knowledge — fully extension-loaded
- Rename AgentCore and Providers to Agent.Core and Agent.Providers.*
- Move extensions from /extensions/ to src/extensions/ and flatten structure
- Channels become extensions — Channels.Core → Gateway.Channels, channel projects → Extensions.Channels.*
- Move CodingAgent to poc/ — proof of concept, not production code
- Move Cron, Memory, Tools to src/common/
- Move Prompts to src/common/ — core infrastructure, not extension
- Merge Sessions.Common into Gateway.Sessions — single consumer
- Move Cron, Memory, Tools to src/gateway/ — they depend on agent+gateway layers
- Move Prompts to gateway, rename to Gateway.Prompts — eliminate src/common/
- **scripts:** Simplify start-gateway.ps1 to delegate to CLI
- **scripts:** Update dev-loop to use CLI and remove redundant deploy-extensions
- **test:** Replace FluentAssertions with Shouldly (BSD-2-Clause)
- **cli:** Migrate all commands to Spectre.Console output
- **domain:** Rename InboundMessage/OutboundMessage.ConversationId to ChannelAddress (#19)
- **domain:** Rename agent-to-agent exchange types for clarity (#21)
- **cli:** Replace --path/--dev with --source and --target (#22)
- **signalr-blazor:** Replace session manager with event handler (#83)

### 🎨 Styling

- **blazor:** Polish read-only banner — accessibility and design consistency

### 🧪 Testing

- **loader:** Add comprehensive extension loader unit tests
- **e2e:** Add integration tests for dynamic extension loading
- **e2e:** Add multi-agent simulation environment with 5 agents and mock channels
- **agent:** Add comprehensive AgentWorkspace unit tests
- **tools:** Add comprehensive memory tools unit tests
- **agent:** Add comprehensive AgentContextBuilder unit tests
- **workspace:** Add workspace and context builder integration tests
- **memory:** Add comprehensive MemoryConsolidator unit tests
- **deployment:** Add deployment lifecycle E2E tests
- **cron:** Add comprehensive cron system unit tests
- **cron:** Add cron system integration tests
- **cron:** Add cron system E2E tests
- **scenarios:** Implement all remaining E2E scenarios for 100% coverage
- **e2e:** Add getting-started guide validation test
- **diagnostics:** Add unit tests for all health checkups
- **cli:** Add CLI integration tests
- **gateway:** Add config hot reload integration tests
- **cli:** Add backup command integration tests
- Update test mocks for StreamingChatChunk interface
- Add comprehensive tests for Pi-style provider architecture
- **agent-core:** Add test utilities (mock provider, test tools, helpers)
- **agent-core:** Add MessageConverter and ContextConverter tests
- **agent-core:** Add ToolExecutor tests
- **agent-core:** Add Agent class and PendingMessageQueue tests
- **coding-agent:** Add tool unit tests (Read, Write, Edit, Shell, Glob)
- **coding-agent:** Add session and config tests
- **coding-agent:** Add hooks and utility tests
- **coding-agent:** Add GrepTool and SessionCompactor tests
- Add registry isolation and resolution tests
- Add session compaction and extension lifecycle tests
- Add system prompt builder tests
- Add immutable options and provider alignment tests
- Add MessageTransformer and session tree model tests
- Add regression tests for port audit P0 fixes
- **providers:** Add regression tests for port audit P0/P1 fixes
- **agent-core:** Add regression tests for failure handling, queue priority, tool lookup
- **coding-agent:** Add regression tests for compaction, convertToLlm, BOM, grep defaults
- **coding-agent:** Update converter mapping test
- **coding-agent:** Align tool tests with new tool semantics
- **coding-agent:** Update session and loader behavior tests
- **agent:** Align streaming state assertions with MessageEnd add
- **providers:** Align assertions with updated stop and identity rules
- **providers:** Add ModelRegistry equality and StopReason mapping tests
- **agent:** Add streaming state, queue, and config default tests
- **coding-agent:** Add EditTool diff, ShellTool bash, and byte limit tests
- **agent:** Add listener exception safety tests
- **agent:** Add hook exception safety tests
- **agent:** Add retry delay cap tests
- **coding-agent:** Add symlink path rejection tests
- **providers:** Add model registry validation tests
- Add port audit verification tests
- **coding-agent:** Add shell tool truncation and timeout tests
- **providers:** Add tool call validator tests
- **coding-agent:** Add list directory and context discovery tests
- **agent:** Add per-retry transform tests
- **providers:** Add shortHash and normalizer tests
- **agent:** Add follow-up continuation steering poll coverage
- **coding-agent:** Add validation prompt and stdin coverage
- Scaffold gateway test specifications and stubs
- **gateway:** Add real unit tests for core implementations
- **gateway:** Expand and stabilize gateway test coverage
- **gateway:** Expand and stabilize gateway test coverage
- **gateway:** Add GatewayHost dispatch pipeline tests
- **gateway:** Add streaming pipeline integration coverage
- **gateway:** Cover supervisor store auth and channel gaps
- **gateway:** Add copilot integration test infrastructure
- **gateway:** Add configuration subsystem test coverage [P1]
- **gateway:** Add integration tests for cross-agent, steering, isolation, and platform config
- **gateway:** Expand copilot live integration coverage
- **gateway:** Add GatewayAuthManager and integration tests
- **gateway:** Add anticipatory test scaffolding for phase 5 features
- **gateway:** Activate anticipatory tests and add phase 5 integration tests
- **gateway:** Add cross-agent calling tests
- **gateway:** Add Sprint 7A comprehensive tests
- **gateway:** Cover websocket protocol and lifecycle gaps
- **providers:** Add provider conformance test suite
- **gateway:** Add deployment validation and startup tests
- **gateway:** Add config path resolver tests
- **gateway:** Add schema validation tests
- **gateway:** Add config loader edge case tests
- **cli:** Add CLI command handler tests
- **gateway:** Add extension loader tests
- **telegram:** Add Telegram adapter unit tests
- **gateway:** Add Wave 1 coverage — auth bypass, channels, extensions endpoints
- **gateway:** Add Wave 2 coverage — rate limiting, correlation IDs, session metadata, config versioning
- **gateway:** Add Wave 3 coverage — SQLite store, health, lifecycle, metadata auth, eviction
- Add providers and models controller tests
- **gateway:** Add wave 1 correlation id middleware scenarios
- Add OTel diagnostics and Serilog config tests
- Add memory system wave 1 coverage
- Skip deadlocking WebSocket tests after single-connection refactor
- **gateway:** Fix websocket deadlock coverage for single-connection flow
- Exclude WebApplicationFactory tests that hang test runner
- Fix integration tests — no more hangs, 470 passed
- **gateway:** Add comprehensive SignalR hub integration tests
- **cron:** Add tests for stale NextRunAt, timezone, and clobber fixes
- **cron:** Add TDD corner-case tests for scheduler and tool
- **gateway:** Add SessionTool tests for all access tiers
- **skills:** Add comprehensive SkillTool tests
- **skills:** Add additional coverage from Adversary report
- **mcp:** Expand MCP extension test coverage
- Add security and adversarial tests for CodingAgent, AgentCore, and MCP
- Expand ProcessTool (9→50+) and Memory (24→50+) test coverage
- Comprehensive E2E integration tests for session resume
- Comprehensive E2E and adversarial tests for session compaction
- **gateway:** Add sub-agent model and manager unit tests
- **gateway:** Add sub-agent tool and integration tests
- **gateway:** Add session switching behavior tests
- **gateway:** Expand SignalR session switching integration tests
- Add Playwright E2E test project for WebUI session switching
- **webui:** Add P0 Playwright E2E tests — 30+ interaction tests
- **webui:** Add P1 Playwright E2E tests — 42 interaction tests across 11 classes
- **webui:** Add P1 Playwright E2E tests — 40+ interaction tests
- **gateway:** Add Phase 1 multi-session tests — warmup + subscription
- **webui:** Update E2E tests for multi-session connection model
- **gateway:** Add DelayTool unit tests
- **gateway:** Add FileWatcherTool unit tests
- **gateway:** Add channel history pagination and ListByChannel tests
- **webui:** Add Playwright E2E tests for infinite scrollback
- **gateway:** Add PathValidator permission model tests
- Add BotNexus.Domain value object and smart enum unit tests
- Add Wave 2 session model and value object adoption tests
- Add Wave 3 sub-agent archetype, cron trigger, and typed ID tests
- Update tests for AgentId and SessionId value object adoption
- Add existence query dual-lookup tests
- Add SessionStoreBase contract tests
- **sessions:** Cover existence dual-lookup query behavior
- Add snapshot tests for current SystemPromptBuilder output
- Add agent-to-agent conversation and cycle detection tests
- Add soul session lifecycle tests
- Add world descriptor tests
- Add prompt section unit tests
- Verify all projects build with split abstractions
- Add GatewaySession behavioral snapshot tests before split
- Add cross-world federation tests
- Verify session split with existing test suite
- Add shared session primitives tests
- Verify session start/resume with typed IDs
- Add session visibility rule tests
- Update hub tests for simplified connection model
- **domain:** Add ConversationRequest and CrossWorldPermission coverage
- **gateway:** Add SoulTrigger and agent registry contract tests
- Add send-during-switch guard test
- Add channel alias resolution tests
- **integration:** Add multi-agent session isolation tests
- **hub:** Add SendMessage routing and auto-creation tests
- Comprehensive test coverage improvements across 10 projects
- Add session resumption context bridge tests
- Add file access policy configuration tests
- Add location registry configuration and resolver tests
- Add location reference resolution tests
- Add CLI location command tests
- Add MediaPipeline unit tests (Wave 2)
- Add sub-agent completion wake-up tests
- Add heartbeat service tests (Wave 3)
- Add InternalChannelAdapter tests
- Add multi-agent concurrency integration test harness
- Multi-MCP server loading scenario — 3 servers, all inject tools
- Context diagnostics integration scenario + session path substitution
- Extract shared SignalR channel registration helper for integration tests
- **cli:** Update assertions for Spectre.Console output and github-copilot default
- **blazor-client:** Update HomePageTests and add MainLayoutTests for new layout
- **webui:** Add verification report for autoscroll fix
- **cli:** Add comprehensive test suite for GatewayProcessManager and HttpHealthChecker
- **blazor:** Add unit tests for read-only sub-agent session view
- **blazor-client:** Restore component test coverage for Wave 3 architecture (#84)
- **probe:** Probe round 2 — 15 new tests across 3 surfaces (#86)
- **e2e:** Add BotNexus.E2ETests with Playwright (#93)

### ⚙️ Miscellaneous

- **squad:** Update Zapp history with scenario registry learnings
- **.squad:** Backup CLI implementation, test isolation pattern
- Add commit instructions to all agent charters
- Update Bender history with model logging learnings
- **.squad:** Merge decision inbox and update leela history
- **scribe:** Sprint orchestration & decision log
- **git:** Add pre-commit hook installer script
- Remove unused MCP configuration file
- Scribe orchestration log for pi port sprint
- Register new provider and test projects in solution
- Register new provider and test projects in solution
- **squad:** Update agent history for provider architecture work
- **.squad:** Merge decision inbox & session logs
- Merge squad decision inbox into decisions.md
- **.squad:** Merge audit reports into decisions
- **.squad:** Sprint 4 consolidation — decisions merged, orchestration logs created
- **squad:** Record provider alignment learnings
- **squad:** Record P0 safety fix learnings
- **squad:** Update hermes learnings
- **squad:** Add port audit remediation retrospective learnings
- Merge port-audit sprint decisions and orchestration logs
- **squad:** Phase 5 port audit retrospective
- **squad:** Log port audit session
- **squad:** Port audit retrospective
- Merge gateway architecture decision
- **scribe:** Log Phase 4 Wave 1 delivery — orchestration logs, session log, decision merge, agent updates
- **scripts:** Add dev scripts, sample config, and workflow docs
- **squad:** Update team history and decisions for gateway sprint
- **squad:** Log batch 1 session and merge decisions
- **squad:** Log batch 2 session and merge decisions
- **squad:** Update focus to Phase 5 complete
- **scripts:** Harden dev-loop and platform config validation
- **squad:** Record gateway P1 decisions and learnings
- Phase 11 Wave 1 orchestration & session logs
- Phase 11 Wave 2 cross-agent history updates
- Update kif history with Wave 3 learnings
- Update now.md for Phase 12 complete
- **.squad:** Clean up temporary commit message file
- Update squad state — history, decisions, OTel proposal merged
- Update squad state — directives and decisions
- Remove unused package.json + fix error response in GatewayHost
- Add XML doc comments to SignalR hub/adapter, suppress CS1591
- Capture agent page UX directive
- **gateway:** Final extension wiring cleanup
- Remove /extensions/ from gitignore
- Add MCP extension to deploy script
- Add System.IO.Abstractions NuGet packages for testable file I/O
- Archive tool permission model spec — done
- Save session state — agent histories and working files
- Update farnsworth history and phase docs after Wave 3
- **squad:** Record farnsworth wave4 learnings
- Mark DDD refactoring spec as delivered
- **squad:** Update now.md for DDD refactoring delivery
- Mark DDD refactoring spec as done — all phases complete
- Archive completed planning specs to docs/planning/archive
- Mark bug-session-switching-ui spec as delivered
- **webui:** Remove legacy app.js — replaced by new WebUI
- **probe:** Build and run start-probe in Release configuration
- Set feature-subagent-ui-visibility status to in-progress
- **squad:** Update now.md — extension commands delivered
- **squad:** Update now.md — config inheritance delivered
- Mark feature-user-documentation as delivered
- Mark bug-exec-process-disconnect and bug-session-lifecycle-fragmentation as done
- Mark bug-steering-delivery-latency as done
- Mark feature-media-pipeline as delivered
- Mark improvement-subagent-completion-handling as delivered
- Mark improvement-heartbeat-service as delivered
- Mark bug-internal-channel-adapter-missing as delivered
- Mark bug-cross-agent-session-blocking as delivered
- Mark feature-config-management-api as delivered
- Post-delivery consistency fixes for feature-blazor-webui Phase 1
- Post-refactor consistency fixes — stale namespaces, docs, spec status
- Standardize extension manifests + add AGENTS.md rules
- Strip scroll debug logging — autoscroll confirmed working
- Add .txt and .log to gitignore with negation rules
- **webui:** Remove legacy static WebUI and all references
- Update .gitignore to include all files in the site directory
- **.squad:** Log bug-blazor-autoscroll session completion
- **consistency:** Complete design spec success criteria and clarify PID lifecycle comments
- **squad:** Update agent histories and deliver-spec prompt after gateway delivery
- **.squad:** Log gateway detached process delivery session
- **planning:** Add planning specs from worktree sessions
- **planning:** Begin delivery of feature-blazor-subagent-session-view
- **planning:** Mark feature-blazor-subagent-session-view as delivered
- **.squad:** Merge feature-blazor-subagent-session-view delivery artifacts
- **planning:** Mark feature-blazor-subagent-session-view as done
- **merge:** Resolve conflicts merging feature-blazor-subagent-session-view
- **squad:** Log wave 1 design review and reproducing tests
- Align Squad skills and templates with published 0.9.1 (#5)
- **squad:** Add botnexus-issue-workflow skill (#39)
- **scripts:** Revert CLI gateway start delegation, handle deploy directly (#40)

### 🔧 CI/Build

- **squad-heartbeat:** Change schedule from every 30 minutes to every hour (#38)
- **publish:** Switch NuGet push to OIDC-based authentication via NuGet/login@v1 (#104)

### 🛡️ Security

- Guard Swagger, add daily secret scanning workflow, fix gitignore (#2)

### Fix

- Populate model dropdown when opening existing sessions
- Add circuit breaker for repeated blocked tool calls
- Remove unused variable anyToolBlocked

### Scribe

- Full team sprint log — 2026-04-03T07:31:24Z
- Session logs and orchestration records
- Orchestration logs, session log, and decision merge
- Document multi-turn tool calling fix (2026-04-03T22:10:00Z)
- Post-spawn orchestration for Phase 3 design review
- Merge decision inbox → decisions.md
- Phase 3 Wave 1 orchestration logs + decision merge
- Log orchestration for Fry (WebUI fixes) and Bender (models endpoint)
- Log Wave 2 Session Model Orchestration

### Squad

- Session switching bug fully delivered with Playwright E2E

### WebUI

- Add markdown rendering and fix streaming appearance

### Audit

- Deep functional re-audit of AgentCore vs pi-mono agent-loop
- Deep functional re-audit of CodingAgent vs pi-mono
- Deep functional re-audit of providers vs pi-mono

### Build

- Centralize common properties and enable central package management
- **domain:** Upgrade BotNexus.Domain to net10.0

### Debug

- Add diagnostic logging for agent loop and copilot responses
- Add tool setup logging and /tools debug endpoint
- Fix tools endpoint to use supervisor GetHandle
- Extension loading warnings for discovery diagnostics
- Add file logging to bootstrap logger for startup diagnostics
- Log full tool list on handle creation
- Add console logging to scrollActiveToBottom + increase timeout

### Planning

- Add 7 specs from Blazor UI shakedown, archive 2 completed items

### Proposal

- 3-layer provider/model configuration filtering
- Unified config + agent directory architecture
- Cron infrastructure architecture (OpenClaw reference)

### Review

- **nibbler:** Phase 2 sprint consistency review — Good
- **nibbler:** Phase 3 consistency fixes
- **nibbler:** Phase 4 Wave 1 consistency findings — 0 P0, 2 P1 fixed, 5 P2
- Phase 11 design review — grade A-, 0 P0, 6 P1, 5 P2

### Scribe

- Log Hermes gateway validation

### Squad

- Orchestration and session logs for Leela pack.ps1 fix
- Session log for loop alignment & UI fix
- Log session-switching design review orchestration, decisions, and session metadata

[0.34.0]: https://github.com/sytone/botnexus/compare/v0.33.0...v0.34.0
[0.33.0]: https://github.com/sytone/botnexus/compare/v0.32.0...v0.33.0
[0.32.0]: https://github.com/sytone/botnexus/compare/v0.31.0...v0.32.0
[0.31.0]: https://github.com/sytone/botnexus/compare/v0.30.0...v0.31.0
[0.30.0]: https://github.com/sytone/botnexus/compare/v0.29.0...v0.30.0
[0.29.0]: https://github.com/sytone/botnexus/compare/v0.28.0...v0.29.0
[0.28.0]: https://github.com/sytone/botnexus/compare/v0.27.0...v0.28.0
[0.27.0]: https://github.com/sytone/botnexus/compare/v0.26.0...v0.27.0
[0.26.0]: https://github.com/sytone/botnexus/compare/v0.25.0...v0.26.0
[0.25.0]: https://github.com/sytone/botnexus/compare/v0.24.0...v0.25.0
[0.24.0]: https://github.com/sytone/botnexus/compare/v0.23.0...v0.24.0
[0.23.0]: https://github.com/sytone/botnexus/compare/v0.22.0...v0.23.0
[0.22.0]: https://github.com/sytone/botnexus/compare/v0.21.0...v0.22.0
[0.21.0]: https://github.com/sytone/botnexus/compare/v0.20.0...v0.21.0
[0.20.0]: https://github.com/sytone/botnexus/compare/v0.19.0...v0.20.0
[0.19.0]: https://github.com/sytone/botnexus/compare/v0.18.0...v0.19.0
[0.18.0]: https://github.com/sytone/botnexus/compare/v0.17.0...v0.18.0
[0.17.0]: https://github.com/sytone/botnexus/compare/v0.16.0...v0.17.0
[0.16.0]: https://github.com/sytone/botnexus/compare/v0.15.0...v0.16.0
[0.15.0]: https://github.com/sytone/botnexus/compare/v0.14.0...v0.15.0
[0.14.0]: https://github.com/sytone/botnexus/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/sytone/botnexus/compare/v0.12.1...v0.13.0
[0.12.1]: https://github.com/sytone/botnexus/compare/v0.12.0...v0.12.1
[0.12.0]: https://github.com/sytone/botnexus/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/sytone/botnexus/compare/v0.10.1...v0.11.0
[0.10.1]: https://github.com/sytone/botnexus/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/sytone/botnexus/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/sytone/botnexus/compare/v0.8.1...v0.9.0
[0.8.1]: https://github.com/sytone/botnexus/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/sytone/botnexus/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/sytone/botnexus/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/sytone/botnexus/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/sytone/botnexus/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/sytone/botnexus/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/sytone/botnexus/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/sytone/botnexus/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/sytone/botnexus/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/sytone/botnexus/compare/v0.1.15...v0.2.0
[0.1.14]: https://github.com/sytone/botnexus/compare/v0.1.13...v0.1.14
[0.1.13]: https://github.com/sytone/botnexus/compare/v0.1.12...v0.1.13
[0.1.12]: https://github.com/sytone/botnexus/compare/v0.1.11...v0.1.12
[0.1.11]: https://github.com/sytone/botnexus/compare/v0.1.10...v0.1.11
[0.1.10]: https://github.com/sytone/botnexus/compare/v0.1.9...v0.1.10
[0.1.9]: https://github.com/sytone/botnexus/compare/v0.1.8...v0.1.9
[0.1.8]: https://github.com/sytone/botnexus/compare/v0.1.7...v0.1.8
[0.1.7]: https://github.com/sytone/botnexus/compare/v0.1.6...v0.1.7
[0.1.6]: https://github.com/sytone/botnexus/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/sytone/botnexus/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/sytone/botnexus/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/sytone/botnexus/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/sytone/botnexus/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/sytone/botnexus/compare/v0.1.0...v0.1.1

