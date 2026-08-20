# Portal Surface Parity

Inventory of shared logic, deliberate-difference register, and alignment plan for the two portal
surfaces — the desktop `BlazorClient` and the `BlazorClient.Mobile` PWA — measured against the
shared `BlazorClient.Core` library.

Produced for issue [#2452](https://github.com/Sytone/botnexus/issues/2452).

::: info Measurement provenance
Every count and line reference in this document was re-measured against `main` at commit
`4ce9ad37` by scanning 1,441 `src` `.cs`/`.razor` files (128 of them in the three portal projects),
excluding `bin`, `obj` and `node_modules`. The originating issue's evidence dated from 2026-07 and
several of its numbers had moved; where a claim no longer held it is marked
**superseded** below rather than repeated.
:::

## The rule this document exists to enforce

> When a second surface needs a rule the first surface already has, the rule moves to `.Core`.
> It is never reimplemented.

`ConversationOrigin` in `.Core` is the canonical worked example. Its remarks state the reasoning
directly: a client-side re-declaration of a server contract "is a duplicated contract that can
silently drift — adding a value server-side fails no client build, it just degrades to the
tolerant-parse fallback and renders wrong." That is issue #2305's finding, and it generalises to
every wire value the portal compares against a literal.

Two properties are required together, and they are complementary rather than alternatives:

1. **Single declaration.** The vocabulary is declared once, server-side, and referenced — not
   mirrored.
2. **Tolerant parsing.** A deployed client can still be older than the server it talks to, so
   parsing must be total: unknown, empty and absent values fall back to a documented default rather
   than throwing.

## 1. Surface shapes

| | Desktop `BlazorClient` | `BlazorClient.Core` | `BlazorClient.Mobile` |
|---|---|---|---|
| `.razor` files | 39 | 3 | 8 |
| `.cs` files | 4 | 68 | 6 |
| Components | 25 | 2 | 3 |
| Routed pages | 22 routes across 11 files | — | 6 routes across 2 files |
| Largest file | `Components/ChatPanel.razor` — 1,513 lines | `Services/AgentInteractionService.cs` — 1,347 lines | `Pages/Chat.razor` — 1,126 lines |
| Unit test files | 135 | (shared) | 29 |

**Superseded numbers.** Issue #2452 recorded desktop at "~40 component/page files" and mobile at 13,
with `ChatPanel.razor` at 1,306 lines and mobile `Chat.razor` at 977. The component/page counts still
hold approximately, but both large files have grown materially: `ChatPanel.razor` by 207 lines and
mobile `Chat.razor` by 149. The monolith is accreting, not stable.

### Routes

Desktop serves 22 routes: `/`, `/home`, `/chat`, `/chat/{AgentId}`, `/chat/{AgentId}/{ConversationId}`,
`/agent/{AgentId}`, `/agent/{AgentId}/conversation/{ConversationId}`, `/agents`, `/agents/new`,
`/agents/{AgentId}`, `/activity`, `/activity/{Section}`, `/configuration`, `/configuration/{Section}`,
`/cron`, `/platform`, `/plugins`, `/plugins/{PluginId}`, `/skills`, `/skills/{Section}`, `/tools`,
`/tools/{Id}`.

Mobile serves 6: `/`, `/agent/{AgentId}`, `/agent/{AgentId}/conversation/{ConversationId}`,
`/chat/{AgentId}`, `/chat/{AgentId}/{ConversationId}`, `/settings`.

The five chat-family routes are identical between the surfaces, which is the important part: a deep
link works on both. Everything mobile lacks is an *administration* surface, and that is a deliberate
difference (see §3).

## 2. Commonality inventory — wire-value literals

Acceptance clause 2 requires **every** inline wire-value string literal in either surface to be
identified with a proposed `.Core` home or an explicit justification for staying local.

A note on method: a naive scan for `"active"` returns 41 hits across the portal, of which 28 are
CSS class toggles (`class="... @(cond ? "active" : "")"`). Those are **not** wire values — they are
presentation tokens paired with a stylesheet, and they stay local. The table below is the filtered
set.

### 2.1 Conversation status — **drift, extract**

| Surface | File | Line | Expression |
|---|---|---|---|
| Desktop | `Components/AgentDashboard.razor` | 65 | `.Where(c => c.Status == "Active")` |
| Desktop | `Components/AgentDashboard.razor` | 76 | `.Count(c => c.Status == "Active")` |
| Mobile | `Pages/Chat.razor` | 54 | `.Where(c => c.Status == "Active")` |
| Mobile | `Pages/Chat.razor` | 646 | `.Where(c => c.Status == "Active")` |

The issue's four-site claim **still holds exactly**, though the mobile line numbers have moved from
40/599 to 54/646. Three further `.Core` sites participate in the same vocabulary as producers or
consumers: `Services/Abstractions/IClientStateStore.cs:327` (`= "Active"` default),
`Services/AgentInteractionService.cs:1146` (`Status = "Active"`), and
`Services/ActivityDashboard.cs:960-961`, the only portal site that handles `"Archived"` at all.

The four surface filters use ordinal `==`; the `.Core` filter uses `OrdinalIgnoreCase`. That
inconsistency is live today and invisible only because the server happens to emit exactly `"Active"`.

**Superseded absence claim.** The issue stated that "a repo-wide search for a status type in `.Core`
returns nothing — no `ConversationStatus` enum, no status constants, no parser." The `.Core` half is
still true. The *repo-wide* half is not: `ConversationStatus` is declared in
`src/domain/BotNexus.Domain.Wire/ConversationEnums.cs:6` and has 51 references across 25 gateway and
domain files. So this is not a missing enum — it is a **portal that never adopted an enum that
already exists**, which is a strictly easier fix than the issue assumed. There is no new type to
design; there is a reference to add and a tolerant parser to write.

**Proposed `.Core` home** — `Services/ConversationStatusParsing.cs`, a direct sibling of
`ConversationOrigin.cs`, referencing `BotNexus.Gateway.Abstractions.Models.ConversationStatus`
rather than re-declaring it, with a total case-insensitive `Parse` and an `IsActive` predicate.

The fallback must fail **open** (`Active`), for the reason `ConversationOrigin.ParseVisibility`
documents: a client older than its server that mapped an unknown status to `Archived` would silently
empty the user's conversation list, which reads to the user as data loss. An unexpectedly visible row
is cosmetic.

→ [#3454](https://github.com/Sytone/botnexus/issues/3454)

### 2.2 Todo item status — **drift, extract**

Desktop-only, in `Components/TodoPanel.razor`: the literals `"done"`, `"in_progress"`, `"cancelled"`
and `"pending"` appear in four separate blocks — a normalisation switch (126-129, with a
`_ => "pending"` fallback), two direct comparisons (137, 148), a glyph switch (164-166), a label
switch (172-175), and a property default (205).

These are wire values. The server declares them in
`src/gateway/BotNexus.Gateway/Tools/TodoTool.cs` as a JSON schema enum at lines 58 and 73, normalises
them at line 374 and maps them to prompt glyphs at line 389;
`src/gateway/BotNexus.Gateway.Prompts/TodoPromptFormatter.cs` carries a third copy of the glyph
mapping.

A scan of all 1,441 `src` files finds **zero** occurrences of `TodoStatus` or `TodoItemStatus`. There
is no shared type anywhere — server-side or client-side.

The portal comparisons are case-sensitive while `TodoTool.cs:374` normalises case before storing, so
the portal relies on a server invariant it does not state.

**Proposed `.Core` home** — `Services/TodoItemStatusProjection.cs` owning the enum, a total
case-insensitive parser defaulting to `Pending`, and the glyph and label tables.

→ [#3455](https://github.com/Sytone/botnexus/issues/3455)

### 2.3 Message role — **drift, extract**

Four near-identical normalisation switches, all inside `.Core`:

| File | Line | Handles `""`? |
|---|---|---|
| `Services/AgentInteractionService.cs` | 1275-1281 | no |
| `Services/PortalLoadService.cs` | 270-276 | no |
| `Services/GatewayEventHandler.cs` | 345-350 | yes |
| `Services/ClientStateStore.cs` | 433-435 | yes |

`GatewayEventHandler.cs:340` states the duplication in a comment: *"Mirrors
AgentInteractionService.MapRole so…"*. A comment acknowledging a mirror is the clearest available
signal that the seam is in the wrong place. Two of the four copies handle `""` and two do not — the
divergence is already real.

Downstream, each surface re-derives the assistant/user question independently: desktop
`ChatPanel.razor:616-618` and `:721-722`, mobile `Chat.razor:352-353` and `:1018`. Desktop
additionally compares `msg.Role == "Assistant"` ordinally at lines 361, 372, 382, 394 and
`== "System"` at 287, relying on the canonical casing the four mappers happen to produce.

**Proposed `.Core` home** — `Services/MessageRole.cs` with canonical constants, a total `Normalize`,
`IsAssistant`/`IsUser` predicates, and a `CssRole` down-mapping for the lower-case token mobile needs.

→ [#3456](https://github.com/Sytone/botnexus/issues/3456)

### 2.4 Wire values that correctly **stay local**

| Value(s) | Site | Why it stays |
|---|---|---|
| `"active"` as a CSS class | 28 sites across `ActivityDashboard.razor`, `AgentPanel.razor`, `SessionDebugPanel.razor`, `MainLayout.razor`, `Activity.razor`, `Configuration.razor`, `Home.razor`, `ActivityCostView.razor` | Presentation token paired 1:1 with `app.css`, not a server value. Moving it to `.Core` would couple the shared library to a stylesheet. |
| `"agent-prompt"`, `"command"` | `Pages/CronJobs.razor:155, 189, 311` | Already owned by `.Core` — `Services/CronApiClient.cs:183` declares the default. The razor comparisons are case-insensitive and read from that contract. Desktop-only surface; no second consumer exists, so extraction would be speculative. |
| `"ok"`, `"error"`, `"failed"`, `"running"`, `"completed"` (cron run status) | `Pages/CronJobs.razor:476-479` | Maps run status to a CSS class. `.Core`'s `Services/ActivityDashboard.cs:585-586` owns the richer *semantic* normalisation (`"succeeded"`, `"timeout"`, `"aborted"`, `"no_tool_calls"`); the razor switch is the CSS half. Worth folding in when a second surface gains a cron view — not before. |
| `"error"` as a status-CSS argument | 20 `SetStatus(..., "error")` calls across `AgentDetailPanel`, `ToolsManage`, `Agents`, `Configuration.razor.cs`, `CronJobs`, `Plugins` | Presentation token for the shared status-line pattern. Local to desktop admin pages. |
| `"overview"`, `"metadata"`, `"system-prompt"`, `"history"`, `"sub-agents"` | `Components/SessionDebugPanel.razor:25-37` | Private tab identifiers within one component. Never crosses a wire. |
| `"cron"` | `Layout/MainLayout.razor:227` (`public const string Cron = "cron"`), `Pages/Activity.razor:51`, `Activity.razor.cs:37` | Already a named constant, and `.Core`'s `ConversationRenderProjection.cs:132` and `ActivityDashboard.cs:900` own the semantic mapping. The remaining literals are route-segment identifiers. |
| `"heartbeat"` | `Components/AgentDetailPanel.razor:1026`, `.Core/Services/AgentConfigContracts.cs:327` | Config JSON property name, already declared once in `.Core`. |
| `"Running"`, `"Completed"`, `"Failed"` (sub-agent status) | `Layout/MainLayout.razor:626, 965-966`, `.Core/Services/ClientStateModels.cs:39`, `GatewayEventHandler.cs:561, 590, 613` | Producer and consumer are both in `.Core`; desktop reads them for a glyph. Single-surface today. Flagged as a **watch item**: if mobile ever renders sub-agents, this becomes the next §2.1. |

## 3. Deliberate-difference register

Classified per the categories in #2452. **Display**, **platform** and **interaction** entries are
decisions — they are recorded so no future slice re-litigates them. **Drift** entries are defects
with a filed issue.

### 3.1 Display constraint

| # | Difference | Evidence | Reason |
|---|---|---|---|
| D-1 | Mobile has no collapsible sidebar with filter bars | Desktop `MainLayout.razor:549-561` renders three `ConversationActivityFilter` buttons; mobile has none | A phone viewport cannot carry a persistent sidebar plus filter chrome alongside the message list. Mobile uses the native picker instead. |
| D-2 | Mobile has no multi-panel layout | Desktop `AgentPanel.razor:29-61` hosts five tab panes (Conversation, Workspace, Reports, Canvas, Todo); mobile hosts chat plus a canvas overlay | Tab panes assume a viewport wide enough to keep the chat visible beside them. |
| D-3 | Mobile has no admin pages | Desktop routes `/agents`, `/activity`, `/cron`, `/plugins`, `/skills`, `/tools`, `/platform`; mobile has none | These are dense tabular management surfaces. Mobile keeps `/settings`, which is the one an operator needs on a phone. |
| D-4 | Mobile has no session debug panel | `Components/SessionDebugPanel.razor` is desktop-only; 0 `SessionDebug` hits across mobile | A five-tab diagnostic inspector on a phone is unusable, and its audience is at a desk. |
| D-5 | Mobile has no workspace file tree or viewer | `WorkspaceFileTree.razor`, `WorkspaceFileViewer.razor`, `WorkspacePanel.razor` desktop-only; 0 `Workspace` hits across mobile | File browsing needs a two-pane tree/preview layout. |

### 3.2 Platform constraint

| # | Difference | Evidence | Reason |
|---|---|---|---|
| P-1 | Mobile uses the OS `<optgroup>` picker, not a custom bottom sheet | Mobile `Chat.razor:53` calls `PortalConversationGrouping.ForPicker`; the desktop equivalent renders a custom list at `MainLayout.razor:1101-1180` | Decided in **#2360** and explicitly good. The native picker gives correct touch scrolling, accessibility and OS-consistent behaviour for free. The *grouping rule* is shared; only the rendering differs. This is the template for a correct difference. |
| P-2 | Separate reconnect implementations | `.Core/Services/DesktopReconnectLoop.cs` + `DesktopReconnectBackoff.cs` vs `Mobile/Services/MobileReconnectLoop.cs` (264 lines), `MobileReconnectBackoff.cs`, `MobileReconnectRetryPolicy.cs`, `MobileHubTuningOptions.cs` | Mobile connections are backgrounded by the OS, suspended on screen lock and resumed on a different network. A desktop SignalR circuit is not. Different failure model, so a genuinely different policy — not duplication. |
| P-3 | Mobile has a PWA lifecycle desktop lacks | `MobilePwaConfigurationTests.cs`, `MobilePwaLifecycleTests.cs` | Install prompts, service worker and manifest are inherently mobile-only. |
| P-4 | Mobile has a `GlobalErrorBoundary` + `ReconnectOverlay` shell | `Mobile/Components/GlobalErrorBoundary.razor`, `ReconnectOverlay.razor` | A phone user cannot open dev tools. A full-screen recoverable error state is the only viable diagnostic. Desktop has its own boundary; the two are not the same control. |
| P-5 | No `IConversationMruService` on mobile | 2 desktop hits (`Home.razor`, `Program.cs`), 0 mobile | The MRU exists (#3064) to let an agent-only route resolve a conversation and redirect, a problem created by desktop's multi-panel ambient identity resolution. Mobile has one route family and always carries explicit identity, so the problem does not arise. |

### 3.3 Interaction model

| # | Difference | Evidence | Reason |
|---|---|---|---|
| I-1 | No hover affordances on mobile | Desktop `MainLayout.razor:1141` puts the archive action behind a `title=` tooltip on a hover row; mobile surfaces it as an explicit menu action at `Chat.razor:118` | Touch has no hover state. A hover-revealed control is undiscoverable on a phone. |
| I-2 | Mobile archive confirm is a full-screen overlay | Mobile `Chat.razor:255-273` | Larger hit targets and an unambiguous confirm/cancel pair, versus desktop's inline row control. |
| I-3 | Mobile has a manual refresh button | `Chat.razor:63, 87` (`ManualRefreshAsync`) | A backgrounded mobile circuit may have missed events. Desktop's circuit is continuous and needs no user-invoked resync. |
| I-4 | Composer geometry preferences are desktop-only | `PortalPreferences.ExpandingInput` / `ExpandingInputMaxLines` consumed by desktop only | An 8-line expanding textarea is a large-viewport affordance. **Conditional**: this entry is provisional until #3459 either honours these on mobile or confirms the decision — see §3.4 F-6. |

### 3.4 Drift — not yet implemented

Every row here is a defect with a filed issue.

| # | Drift | Evidence | Issue |
|---|---|---|---|
| F-1 | Conversation status compared against a literal on both surfaces | 4 sites, §2.1 | [#3454](https://github.com/Sytone/botnexus/issues/3454) |
| F-2 | Todo status vocabulary declared nowhere shared | `TodoPanel.razor` × 4 blocks, §2.2 | [#3455](https://github.com/Sytone/botnexus/issues/3455) |
| F-3 | Four duplicate message-role mappers, one self-declared mirror | §2.3 | [#3456](https://github.com/Sytone/botnexus/issues/3456) |
| F-4 | Timestamp format implemented 3–4 times, byte-identical across surfaces | `ChatPanel.razor:1003-1004` ≡ mobile `Chat.razor:1029-1030`; partial copy `MainLayout.razor:1311`; variant `CronJobs.razor:461` | [#3457](https://github.com/Sytone/botnexus/issues/3457) |
| F-5 | Mobile ignores `ConversationRenderProjection`'s `IsReadOnly` / `ShowComposer` / `Badge` / `Group` | Mobile uses only `IsUnattended` (`Chat.razor:940`); its composer gates on `_isSending` alone (lines 221, 223). Desktop uses `IsReadOnly` at 19 sites in `ChatPanel.razor`. A read-only conversation renders a live composer on mobile. | [#3458](https://github.com/Sytone/botnexus/issues/3458) |
| F-6 | Config-form orchestration mirrored by comment; mobile ignores `IPortalPreferencesService` entirely | Mobile `Settings.razor.cs` says "mirrors the desktop `Configuration` code-behind"; 4 desktop `IPortalPreferencesService` hits, 0 mobile | [#3459](https://github.com/Sytone/botnexus/issues/3459) |
| F-7 | Mobile `Chat.razor` is an undecomposed 1,126-line monolith, growing | +149 lines since the #2452 measurement; 3 mobile components total vs 25 desktop | [#3460](https://github.com/Sytone/botnexus/issues/3460) |

F-5 is the only entry with a **user-visible correctness** consequence: mobile offers a send
affordance on a conversation the server will reject.

## 4. Alignment plan and merge order

Sequenced so shared-core extraction lands before the surfaces consume it, and so the file-boundary
change lands last where it cannot conflict with everything else.

| Order | Issue | Slice | Why here |
|---|---|---|---|
| 1 | [#3457](https://github.com/Sytone/botnexus/issues/3457) | Timestamp formatting → `.Core` | Smallest, self-contained, touches both surfaces. Establishes the pattern and the deterministic-`now` seam cheaply. |
| 2 | [#3454](https://github.com/Sytone/botnexus/issues/3454) | Conversation status parsing → `.Core` | The headline finding. No new type to design — the enum already exists in `Domain.Wire`. |
| 3 | [#3455](https://github.com/Sytone/botnexus/issues/3455) | Todo status vocabulary → `.Core` | Independent of 1–2, desktop-only edit surface, so it can run in parallel with 2 if worktrees are disjoint. |
| 4 | [#3456](https://github.com/Sytone/botnexus/issues/3456) | Message-role mappers → one owner | Larger blast radius: 4 `.Core` files plus both surfaces. Wants 1–2 merged first so the extraction pattern is settled. |
| 5 | [#3458](https://github.com/Sytone/botnexus/issues/3458) | Mobile consumes the render projection | The first *consumption* slice. Depends on nothing above strictly, but edits mobile `Chat.razor` heavily, so it must precede the decomposition. |
| 6 | [#3459](https://github.com/Sytone/botnexus/issues/3459) | Config orchestration + preferences → `.Core` | Independent file set (`Settings.razor.cs` / `Configuration.razor.cs`), so it may run in parallel with 5. |
| 7 | [#3460](https://github.com/Sytone/botnexus/issues/3460) | Decompose mobile `Chat.razor` | **Last, unconditionally.** Every slice above edits this file; doing the split first guarantees conflicts for all of them. |

Slices 1–4 are pure `.Core` extractions with no behaviour change beyond documented case-sensitivity
deltas. Slices 5–6 change behaviour and need their deltas asserted. Slice 7 changes no behaviour at
all and is gated on zero test-source edits.

## 5. Where a `.Core` extraction collapses two test files into one

Acceptance clause 6. Today the surfaces carry 135 and 29 unit test files respectively. Eight mobile
files are name-level near-duplicates of a desktop file:

| Mobile test | Desktop counterpart |
|---|---|
| `MobileAskUserPromptTests` | `AskUserPromptTests` |
| `MobileBootFailureDiagnosticsTests` | `BootFailureDiagnosticsTests` |
| `MobileCanvasBridgeRoundTripTests` | `CanvasBridgeRoundTripTests` |
| `MobileCanvasPanelTests` | `CanvasPanelTests` |
| `MobileGlobalErrorBoundaryTests` | `GlobalErrorBoundaryTests` |
| `MobileMarkdownRendererFailClosedTests` | `MarkdownRendererFailClosedTests` |
| `MobilePwaConfigurationTests` | `PwaConfigurationTests` |
| `MobileReconnectLoopTests` | `DesktopReconnectLoopTests` |

Not all of these should collapse. The distinction is whether the test asserts a **rule** or a
**rendering**.

**Genuine collapse candidates — one `.Core` test replaces two:**

- **`MobileTimestampFormatTests.cs`** is the clearest case. Its entire content is the
  today-versus-not-today format rule, which desktop implements identically at
  `ChatPanel.razor:1003-1004` and which no desktop test pins. A mobile-only test cannot catch the
  desktop copy drifting — the coverage is *asymmetric*, which is worse than absent because it looks
  like the rule is covered. One `.Core` table test over a fixed `now` replaces it. (#3457)
- **Role normalisation** is currently asserted through rendering on both surfaces. A `.Core` table
  test over the role cross-product covers it once; both surface suites keep only their DOM
  assertions. (#3456)
- **Conversation status filtering** would gain its parse table in `.Core` rather than acquiring a
  filter assertion in each of the two suites. (#3454)
- **`MobileListOrderingTests.cs`** already demonstrates the *correct* shape and is worth copying:
  its own doc comment splits it into "pure `PortalListOrdering` contract tests" plus a mobile DOM
  ordering test. The contract half belongs in the `.Core` suite; the DOM half is legitimately
  mobile. That split is the pattern every extraction above should follow.
- **`MobileSystemSectionParityTests.cs`** and **`MobileScheduledCronMapGroupingTests.cs`** assert
  grouping rules already owned by `.Core`'s `PortalConversationGrouping`. Their rule assertions
  duplicate `.Core` coverage; their rendering assertions do not.

**Correctly separate — do not collapse:**

- `MobileReconnectLoopTests` vs `DesktopReconnectLoopTests` assert genuinely different policies
  (register entry P-2). Merging them would fabricate a shared contract that does not exist.
- `MobilePwaConfigurationTests` and `MobilePwaLifecycleTests` have no desktop analogue in substance
  (P-3); the name similarity is coincidental.
- `MobileGlobalErrorBoundaryTests` covers a different control with a different recovery model (P-4).
- Every `...PageTests` file asserting rendered DOM stays per-surface. Rendering is exactly where the
  surfaces are *supposed* to differ.

The general rule this yields, and the one future portal work should apply:

> A test that asserts a **rule** belongs in the `.Core` suite and runs once.
> A test that asserts a **rendering** belongs in the surface suite and runs per surface.
> A rule test that exists on only one surface is worse than no test, because it certifies a
> contract it cannot actually enforce.

## 6. Maintaining this document

- A new deliberate difference is added to §3 in the same PR that creates it, with its category and
  reason. A difference introduced without a register entry is drift by default.
- A drift row is deleted from §3.4 when its issue closes, not when its PR opens.
- The measured counts in §1 are re-taken whenever this document is materially revised. They are
  provenance, not decoration — the growth of mobile `Chat.razor` between the #2452 measurement and
  this one is precisely the signal that made F-7 worth filing.
