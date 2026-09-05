# Plugin architecture

A **plugin** is a directory that bundles BotNexus components - skills, agents, commands, hooks
and MCP server definitions - so they can be distributed and installed as one unit instead of
being copied file by file.

This document describes the **on-disk contract**, the **install / update / remove
lifecycle**, **plugin-granularity trust catalogs**, how a plugin's skills join skill discovery,
how plugin MCP servers register, and how plugin-shipped agents reach the agent registry behind a
privilege fence.

## Directory layout

```
my-plugin/
  .botnexus-plugin/
    plugin.json        # the manifest - the only required file
  skills/              # discovered by convention
  agents/
  commands/
  hooks/
  .mcp.json
```

The manifest lives at `.botnexus-plugin/plugin.json`. A directory without that file is not a
plugin and is rejected rather than treated as an empty one.

## The manifest

The manifest adopts the Claude Code field set so plugins authored for that ecosystem port with
no translation layer. Only `name` is required:

```json
{ "name": "hello-world" }
```

That single-field manifest is valid and complete. Components are **discovered by convention**
at the plugin root, so a well-laid-out plugin never needs to enumerate its own paths. The
explicit path fields (`skills`, `agents`, `commands`, `hooks`, `mcpServers`) exist only as an
override for plugins with a non-conventional layout.

This distinction is load-bearing in the parsed model: an omitted component field deserialises
to `null`, meaning *discover by convention*. It is never normalised to an empty collection,
which would instead mean *this plugin deliberately has none*.

## Marketplaces

A marketplace catalog lists plugins available from a publisher. It requires `name`, `owner`
and `plugins[]`; each entry requires a `name` and a `source`.

```json
{
  "name": "core-marketplace",
  "owner": { "name": "BotNexus Team" },
  "plugins": [
    { "name": "hello-world", "source": "https://example.com/hello.git" }
  ]
}
```

## Schema is the single source of truth

Two JSON Schema files define the contract:

- `src/extensions/BotNexus.Extensions.Plugins/Schemas/plugin-manifest.schema.json`
- `src/extensions/BotNexus.Extensions.Plugins/Schemas/marketplace.schema.json`

`PluginManifestParser` **loads** these schemas (embedded from the same checked-in files) and
validates against them. It contains no hand-written list of field names or required fields.
This mirrors how `skills/botnexus-maintenance/reference/issue-schema.json` is the single
source of truth for the issue linter, and it exists because every consumer that re-derives a
shape eventually drifts from it - which is precisely what happened to the label taxonomy and
the issue schema before they were centralised.

Tests assert that the parser's reported required-field set equals the `required` array read
directly out of the schema text, so the two cannot silently diverge. Adding a required field
to the schema is therefore sufficient - and necessary - to make the parser enforce it.

## Rejection, not coercion

A manifest that does not match the schema is **rejected**, and the resulting error names the
offending field. There is no best-effort coercion:

- a missing `name` is an error, not a defaulted empty string;
- `"name": 7` is an error, not the string `"7"`;
- an unknown field such as `"skilz"` is an error, not silently ignored.

Silently ignoring a misspelled field is the worst outcome available: the author believes their
plugin declares something it does not, and nothing ever tells them otherwise. Guessing at an
unknown shape means installing something the author did not write.

`ParseManifest` returns a `PluginParseResult<T>` carrying either the typed value or every
validation error found, rather than throwing on the first problem, so a scan across many
plugin directories can report all bad manifests in one pass.

## Lifecycle

`PluginLifecycleManager` owns install, update and remove over a **plugin root** - a single
directory holding one subdirectory per installed plugin, plus an `installed-plugins.json`
state document recording what is installed.

### Transport

The transport is `git clone`, a settled decision in #2623: it supplies private-repository
authentication and version pinning without inventing either. It sits behind
`IPluginSourceFetcher`, so the lifecycle can be exercised - including fault injection part way
through materialisation - with no network and no git binary. `GitPluginSourceFetcher` is the
production implementation and itself delegates process execution to `IGitCommandRunner`.

The fetcher reports the **resolved revision** (the commit SHA), never the requested reference.
Recording `main` would make the question "has the source moved since install?" unanswerable,
and that is precisely the question update exists to answer. The installed record therefore
carries both: `reference` is what update re-resolves, `resolvedVersion` is what is on disk.

### Install is all-or-nothing

Content is fetched and its manifest validated in a staging directory **outside the plugin
root**, and promoted into place only once it is known good. A clone that dies mid-transfer
therefore cannot leave a partial plugin directory behind - there is nothing to clean up at the
destination because nothing was ever written there.

This is structural rather than a cleanup handler, because a partially materialised plugin is
worse than a failed one: it looks installed to every later consumer, so a half-written skill
set would be discovered and used as if it were complete.

Installing over an already-installed plugin is refused rather than silently overwriting it: the
existing record is the only thing that knows which files the previous install wrote.

### Update preference: pinning is opt-in

Every installed plugin carries `updatesEnabled`, defaulting to `true`. Update re-resolves the
source and replaces content only when it is enabled; a pinned plugin is left completely
untouched and the source is not even fetched, since cloning a pinned plugin costs a network
round trip to reach a foregone conclusion. `SetUpdatePreference` lets a plugin be pinned after
the fact without reinstalling it.

When the source resolves to the revision already on disk, nothing is replaced. That keeps a
scheduled update across many plugins from rewriting every directory on every run.

### Removal is exact-set, never pattern-matched

Install records **every file it wrote**, as forward-slash relative paths, and removal deletes
exactly that set. Directories are pruned only when they are empty afterwards.

Deleting the plugin directory wholesale would be simpler and wrong. A user who drops a local
override, a note or a log file next to plugin content would silently lose it - including a file
nested inside a directory the install created. Pattern-matching the directory at removal time
fails the same way, and worse, it fails on content that merely *looks* like plugin content.
The recorded set is the only description of ownership that is actually true.

The same recorded set drives update: the previous content is retired file by file, so anything
the user placed alongside a plugin survives an update just as it survives a removal. Git's own
`.git` metadata is never promoted - it is an artefact of the transport, not plugin content, and
copying it would make every plugin directory a nested repository.

## The update trigger is a cron job, not a startup check

Claude Code and Copilot check their plugins when the framework starts, because their host is a
short-lived CLI process that starts many times a day. **BotNexus is an always-up gateway.** On a
box that stays up for weeks, "check on start" means "never check" - and it would pass every test,
because the test harness restarts the process on every run. Copying that trigger would ship a
plugin system that silently stops updating and looks healthy while doing it.

The extension therefore contributes an agentless **`plugin-update` cron action** and provisions one
platform-wide job for it (#2683). The relevant properties:

| Property | Value | Why |
|---|---|---|
| Action type | `plugin-update` | Dispatched by `CronScheduler` through the same `ICronAction` enumerable as every built-in action. There is no second dispatch path. |
| `agentId` | `null` | Plugins are installed into the gateway, not an agent. `CronJob.AgentId` is already nullable and the scheduler's session-rebonding pass skips agentless jobs, so no "system job" concept was needed. |
| Cost | none | No session is bonded and no model is resolved. Cost and tool-count fields stay `null` - the platform's "not measured" reading - never zero. |
| Job id | `plugin-update` | A fixed constant, not derived from an agent: the id *is* the guarantee that exactly one such job exists. |
| Default schedule | `0 3 * * *` | Off-hours, staggered before the 04:00 skill-review pass. |

### Provisioned on install, and then left alone

The job is created by the act of installing a plugin - the moment it first becomes meaningful -
and only when the install fully succeeded. A failed install materialised nothing, so a job
provisioned for it would run forever over content that does not exist.

Once the job exists it is **never modified**. The provisioner reads the store and returns early;
it does not upsert a canonical definition. A schedule the user edits, or a job the user disables,
survives every subsequent install. This mirrors `SkillReviewCronProvisioner` and is the opposite
of the heartbeat provisioner, which deliberately force-resyncs. To turn the loop off, **disable**
the job rather than delete it: a deleted job is recreated by the next install.

Provisioning failure is logged, not propagated. The plugin is already on disk with its record
written, and reporting that as a failed install would be a lie the caller could act on
destructively.

### One plugin's failure is not the run's failure

Each plugin is updated inside its own error boundary and the run continues past a fault. Aborting
on the first failure would let a single unreachable source silently freeze every other plugin on
the gateway at its installed revision - visible for the broken plugin, invisible for all the
healthy ones. The run is recorded as an error only when *every* enabled plugin failed, because at
that point there is no partial success left to protect.

The update preference is read before any work, so a pinned plugin's source is never fetched. A
filter applied after the fetch would cost a clone per run to reach a foregone conclusion, and would
make "pinned" observationally identical to "already current".

## MCP servers

A plugin may declare MCP servers. They are registered with the **existing** `McpServerManager`
in `BotNexus.Extensions.Mcp`, never with a plugin-only registry: plugin servers are ordinary MCP
servers, and a second registry would mean a second lifecycle, a second warmup path and a second
place for a leaked server process to hide.

`PluginMcpServerRegistrar` owns the policy; `IMcpServerHost` is the narrow seam it talks through
so the policy is testable without spawning real servers. `McpServerManagerHost` is the only
production implementation and does nothing but delegate.

### Collision is impossible by construction

Every declared name is scoped **at registration time** into `plugin:<plugin>:<server>`. Two
plugins that both declare `github` register as `plugin:alpha:github` and `plugin:beta:github`
and both resolve. This is deliberately not detect-and-warn: a collision that has to be detected
is a collision that already happened, and which plugin won would depend on discovery order.

The separator is a character a plugin identifier cannot contain - plugin names are lowercase
kebab-case - so a scoped id parses unambiguously back into its two parts. An unscoped id, such
as a user-configured server, is never claimed by any plugin.

### Declaration file

The manifest key `mcpServers` names the declaration file. As with every other component, `null`
means *discover by convention* rather than *has none*: `.botnexus-plugin/mcp.json` then
`.mcp.json` are probed in order. Both the wrapper form (`{ "mcpServers": { ... } }`) and a bare
root map are accepted, the latter because that is what the equivalent files in the wider MCP
ecosystem look like.

An explicit manifest path is still confined to the plugin directory. A manifest is authored by
whoever wrote the plugin, so treating its path as trusted would let a plugin point the loader at
any file on the host and have the contents parsed as server configuration.

### Removal unregisters exactly that plugin's servers

Selection is by the plugin scope encoded in the server id, so removing a plugin can never take
down a server another plugin - or the user's own configuration - registered, even when the two
declared the same name.

### Trust is decided before anything starts

Under `Enforce`, an untrusted plugin's servers are **never handed to the manager** - not started
and then stopped. An MCP server start is a process spawn or an outbound credentialled
connection, so "start it and reconsider" would already have done the damage. Under `Warn` the
failure is logged and registration proceeds; under `Disabled` no verification happens.

`PluginTrustMode` mirrors `SkillTrustMode` member for member, pinned by an architecture fence.
Plugins reuse the skills trust model (#2682) rather than introducing a second vocabulary,
because two vocabularies is how the enforced set and the reported set drift apart.

## Skill discovery

A plugin's `skills/` directory participates in the existing skill merge at the **global/shared
tier** (#2684). `SkillSource` gained a `Plugin` member and `SkillDiscovery` scans plugin skill
roots immediately *before* the global directory, which is how the merge dictionary expresses
"lower priority".

The resulting precedence is `Plugin` < `Global` < `Agent` < `Workspace`. Plugin and global sit
at the same conceptual tier - both are visible to every agent - but a name collision resolves
in favour of the global directory. A plugin may add capability; it may never silently displace
a skill the operator wrote themselves, because installing a plugin should not change the
meaning of a name that already worked.

### The installed record decides what contributes

`PluginSkillRootResolver` derives skill roots from `installed-plugins.json`, never by
enumerating subdirectories of the plugin root. This is the same reasoning that makes install
refuse an existing-but-unrecorded directory: such a directory has no known provenance and no
removal manifest. Discovering skills out of it would be a trivial way to smuggle content into
every agent's prompt by dropping a folder next to real plugins.

The resolver returns plain directory paths and nothing else. Parsing, validation, the security
scan and trust verification all already live in `SkillDiscovery`, and a second discovery
implementation for plugins is precisely how the enforced set and the surfaced set drift apart.

### Trust

Plugin skills are verified by the existing per-skill `SkillTrustVerifier` on the shared scan
path, so `Disabled` / `Warn` / `Enforce` behave identically to any other skill: under `Enforce`
a skill whose `trust.json` catalog does not match its content is skipped and the refusal is
logged; under `Warn` it is loaded and the violation logged.

Note that this is trust at **skill** granularity, applied to skills that happen to come from a
plugin. Trust at **plugin** granularity - a catalog generated over the whole plugin at install
time - is described in [Plugin trust catalogs](#plugin-trust-catalogs) below.

## Plugin-shipped agents

A plugin may ship agent descriptors under its own `agents/` directory, one JSON document per
agent. `PluginAgentConfigurationSource` surfaces them as a **second
`IAgentConfigurationSource`**, which `AgentConfigurationHostedService` already knows how to
reconcile - it loads every registered source at startup and merges the results. Plugin agents
therefore need no reconciliation logic of their own, and none was added: a second reconciliation
path is how two sets of agents drift into disagreeing about which descriptor won.

As with skills, **the installed record is the authority**. Descriptors are read only from
plugins recorded in `installed-plugins.json`; a directory dropped next to real plugins has no
provenance and contributes nothing.

The source does not watch. `Watch` returns `null`, which the hosted service already treats as
"this source does not notify". Plugin content changes only through install, update or remove -
all explicit operations - so a filesystem watcher would be a second, racier notification path
for events the lifecycle manager already knows about.

### What a plugin agent may declare

A plugin arrives from a marketplace, so its agent descriptor is untrusted input. BotNexus adopts
the Claude Code constraint directly: **a plugin-shipped agent may not declare hooks, MCP servers,
isolation escalation, or file access beyond the installing user's own ceiling.**

| Category | Members | Outcome |
|---|---|---|
| Identity and presentation | `displayName`, `emoji`, `description`, `order` | Declarable |
| Model selection | `model`, `provider`, `allowedModels`, `thinking`, `contextWindow`, `cacheRetention` | Declarable |
| Prompt content | `systemPrompt`, `systemPromptFiles` | Declarable |
| Tool ids | `toolIds` | Declarable |
| Behavioural config | `memory`, `soul`, `heartbeat`, `dateTimeInjection`, `conversationRetention`, `maxConcurrentSessions`, `metadata` | Declarable |
| File access | `fileAccess` | **Narrowed** to the installing user's ceiling |
| Everything else | `isolationStrategy`, `isolationOptions`, `kind`, `shellCommand`, `extensionConfig` (hooks, MCP servers), `subAgents`, `subAgentRoles`, session and conversation access | **Rejected at load** |

`toolIds` is declarable because a tool id names a tool the *host* has registered; an id the host
does not know resolves to nothing. Declaring one cannot conjure a capability the user has not
already installed.

A rejected descriptor is **skipped entirely**, with an error naming the offending field, and the
agent does not load. It is never loaded at reduced privilege: "load it anyway but ignore the
dangerous bit" is indistinguishable at the call site from "the author's intent was honoured".

### File access is narrowed, not refused

`fileAccess` is the one member with a coherent reduced form, so it is clamped rather than
rejected. Grants are intersected **by containment** - a declared path survives when it is at or
beneath a path the ceiling already allows, so a plugin may legitimately ask for a subdirectory of
a granted tree. Denials go the other way and are **unioned**: the ceiling's denied paths always
apply, because a plugin must not be able to un-deny a path by omitting it. Narrowing is logged,
so an author whose declaration did not fully take effect can find out.

A plugin that declares no `fileAccess` is **not** handed the ceiling as a grant; it keeps the
host's default, workspace-only behaviour. And when the installing user has no path grants of
their own, a plugin-declared policy narrows to nothing - a plugin can never be granted more than
the user who installed it.

### The fence is structural, not a list of forbidden names

The obvious implementation is a deny-list of dangerous member names. That list is correct only on
the day it is written: the next property added to `AgentDescriptor` is not on it, so it becomes
plugin-declarable the instant it exists - silently, with no error and no log.

So `PluginAgentDescriptorFence` inverts the default. It reflects over the descriptor's settable
members and classifies them as *declarable* or *narrowed*; the **fenced set is the computed
complement**, never a literal list. A member added tomorrow is fenced tomorrow, and the failure
mode of forgetting is "the new member is rejected", not "the new member is granted".

`PluginAgentPrivilegeFenceArchitectureTests` pins that structure, in the shape of the #2588
fingerprint fence: widening the declarable set fails the architecture test until the same
decision is mirrored in it. Growing the plugin privilege surface therefore cannot happen as a
quiet one-line edit - it requires editing a test whose whole purpose is to be read.

The on-disk `PluginAgentDefinition` shape is a second, cheaper layer: it declares only the
permitted fields, so an `isolationStrategy` in a plugin's JSON has nowhere to bind and is
discarded at parse time. That is a convenience, not the authority - it is a hand-maintained list,
and the structural fence is what stops it drifting.

Runtime sandboxing of the resulting agent is out of scope: the fence governs what the descriptor
may **declare**.

## Plugin trust catalogs

A plugin ships executable content, so it is a supply-chain surface. Install therefore records a
SHA-256 **trust catalog** at `<plugin>/trust.json`, generated over the content install actually
materialised (#2682).

```json
{
  "version": 1,
  "generatedAt": "2026-09-04T12:00:00+00:00",
  "entries": [
    { "path": ".botnexus-plugin/plugin.json", "sha256": "…", "updatedAt": "…" },
    { "path": "skills/demo/scripts/run.ps1", "sha256": "…", "updatedAt": "…" }
  ]
}
```

This is the **same catalog format and the same hasher** the skills trust model uses. There is one
implementation in the platform, `ContentTrustCatalog`; `SkillTrustVerifier` is a documented
forwarding shim over it. A second hashing mechanism is how the enforced set and the reported set
drift apart, which is the defect #2682 exists to prevent.

### Modes

`PluginTrustGate` applies one of three postures, matching `SkillTrustMode` member for member:

| Mode | Verifies | Modified content |
|---|---|---|
| `Disabled` | no | permitted, not logged |
| `Warn` | yes | **permitted**, logged as a warning |
| `Enforce` | yes | **refused**, logged as an error |

`Warn` deliberately still logs. A Warn that permitted silently would be indistinguishable from
`Disabled`, and the whole point of the mode is to make a tamper visible on a fleet that cannot yet
afford to fail closed.

### What the catalog covers

Every file install materialised - including the plugin's own `.botnexus-plugin/plugin.json`. The
manifest is the most security-relevant file in the tree, so skipping dot-directories (as the skills
policy does, since a skill's dot-directories are editor metadata) would leave a plugin's declared
identity freely editable after install.

Because the catalog is exhaustive, plugin verification also reports **unlisted** files: content
present on disk and absent from the catalog is a violation, not something to ignore. A file dropped
into an installed plugin was not installed by the platform, and calling such a plugin trusted would
make the catalog a claim about an unnamed subset.

The two postures differ deliberately:

| | Skills | Plugins |
|---|---|---|
| Files catalogued | scannable script files only | every materialised file |
| Dot-directories | skipped | walked |
| Unlisted file on disk | tolerated | violation |

A skill legitimately sits beside documentation and assets that were never script content, so
hashing them would turn a README edit into a trust violation. A plugin's content set is defined by
what install wrote, so it has no such ambiguity.

### Update regenerates the catalog

Update replaces content, so it rewrites `trust.json` for the new content. A stale catalog would
describe files that no longer exist and would make every successfully updated plugin fail
verification - an availability bug wearing a security hat.

If the catalog cannot be written, the install does **not** fail: the content is already on disk, and
reporting a failed install the caller could act on destructively is worse than an unverifiable
plugin. Failing open here hands out no trust, because a missing catalog is itself a refusal under
`Enforce`.
