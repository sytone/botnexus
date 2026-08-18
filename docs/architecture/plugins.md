# Plugin architecture

A **plugin** is a directory that bundles BotNexus components - skills, agents, commands, hooks
and MCP server definitions - so they can be distributed and installed as one unit instead of
being copied file by file.

This document describes the **on-disk contract**, the **install / update / remove
lifecycle**, and how a plugin's skills join skill discovery. Plugin-granularity trust
catalogs (#2682), cron-triggered updates, and wiring into agent config sources or MCP are
deliberately out of scope here and are covered by later slices of the plugin epic (#2623).

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
plugin. A catalog generated over the whole plugin at install time - so that a plugin shipping
no `trust.json` is itself covered - is sibling issue #2682 and is not yet implemented.
