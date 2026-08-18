# Plugin architecture

A **plugin** is a directory that bundles BotNexus components - skills, agents, commands, hooks
and MCP server definitions - so they can be distributed and installed as one unit instead of
being copied file by file.

This document describes the **on-disk contract** and the **install / update / remove
lifecycle**. Trust decisions, cron-triggered updates, and wiring into skill discovery, agent
config sources or MCP are deliberately out of scope here and are covered by later slices of
the plugin epic (#2623).

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

