# Portal Plugins Page

The portal surfaces installed plugins at **`/plugins`**, with a selected plugin addressed by
**`/plugins/{PluginId}`**. This is slice 8 of the plugin epic; the on-disk contract and the
install/update/remove lifecycle are described in
[Plugin Architecture](../architecture/plugins.md).

## What the page shows

Each installed plugin renders one row with:

| Column | Meaning |
|---|---|
| **Name** | The plugin identifier, linking to `/plugins/{PluginId}`. |
| **Version** | The plugin manifest's advertised version, falling back to the short installed revision when the plugin is unversioned. |
| **Update** | Whether a newer revision is available at the source. |
| **Trust** | Whether the content on disk still matches what install recorded. |
| **Auto-update** | A toggle for the per-plugin update preference. |

Selecting a plugin adds a detail panel showing its source, requested reference, installed
revision, manifest version, install time, recorded file count and trust detail.

## Routing

Selection lives in the route, not in component state, so a plugin is linkable and survives a
reload. An id that is not installed falls back to the **unselected list with a non-fatal
notice** — never an error page. A stale bookmark, or a plugin removed since a link was shared, is
an ordinary thing to happen; blanking the page would hide the very list that answers it.

## Update state is not guessed

Update availability defaults to **`Not checked`**, not "up to date". Probing the source costs a
network round trip per plugin, so a list render does not pay it, and reporting currency without
having looked would be a claim rather than a finding — indistinguishable from a real answer.

A plugin whose updates are disabled reports **`Pinned - not checked`**. That is the complete
answer for a source that is deliberately never probed, not a placeholder for a check yet to run.

## Trust state is not overstated

This slice attests **presence** of the exact file set install recorded, not content. Every
recorded file being present therefore reports **`Unverified`**, with a detail line saying content
hashes are not yet catalogued — content hashing arrives with the install-time trust catalog.
A missing recorded file, or a missing plugin directory, reports **`Modified`** and names the
offending file, because a bare badge is not actionable.

Integrity is judged against the recorded file set and **never** a directory scan, for the same
reason removal is exact-set: a file the user dropped alongside plugin content is not a
modification of the plugin, and flagging it would cry wolf on exactly the content that removal is
careful to preserve.

## The only write

The page writes one thing — the per-plugin auto-update preference:

```
PUT /api/plugins/{name}/update-preference
{ "updatesEnabled": false }
```

The preference is persisted to the installed record so it survives a restart. A rejected write
leaves the row showing the **persisted** preference rather than the attempted one, and reports the
failure: a toggle that reads back as changed when the gateway never stored it is the one lie a
persistence control must not tell.

Installing a plugin from the portal is out of scope. Install remains a CLI operation, so the API
deliberately exposes no `POST`.

## API

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/plugins` | Every installed plugin, ordered by name. |
| `GET` | `/api/plugins/{name}` | One plugin; `404` when not installed. |
| `PUT` | `/api/plugins/{name}/update-preference` | Set the auto-update preference. |

The endpoints are registered by `PluginsEndpointContributor` in the
`BotNexus.Extensions.Plugins.Api` extension, not by a gateway controller: a gateway project may
not reference an extension project, which
`GatewayProjectDependencyBoundaryTests.GatewayProjects_DoNotReferenceExtensionsProjectsOrLibraries`
enforces. This follows the `SkillsEndpointContributor` precedent, which moved the skills file
browser out of `BotNexus.Gateway.Api` for the same reason.

