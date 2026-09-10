# Plugins API Reference

Reference for the **plugins** endpoints: what is installed (`api/plugins`) and the
repositories the gateway looks in to find more (`api/plugins/sources`).

> **Registered by an extension, not a controller.** These routes come from
> `PluginsEndpointContributor` in `src/extensions/BotNexus.Extensions.Plugins.Api/`
> rather than from a controller under `src/gateway/`, because a gateway project may
> not reference an extension project. They are endpoint-routed, so they run *after*
> `GatewayAuthMiddleware` and are authenticated like every other `/api` route.

Two ideas are kept apart throughout, and the routes follow the split:

- A **source** is a place to look. Adding one records a URL, reads it once, and runs
  nothing.
- An **install** is the step that can put third-party code in the gateway. It stays
  behind its own call and its own consent gate.

---

## Data types

### PluginPortalRow

One installed plugin. Returned by the read routes and carried on a successful
install or update.

| Field | Type | Notes |
|-------|------|-------|
| `name` | string | Plugin identifier, and the route parameter that addresses it. |
| `source` | string | Marketplace source the content was fetched from. |
| `reference` | string? | Branch or tag requested at install time; `null` for the source's default branch. |
| `resolvedVersion` | string | Exact revision on disk — a commit SHA for a git source. |
| `manifestVersion` | string? | Version the plugin's own manifest advertises; `null` when unversioned. |
| `updatesEnabled` | bool | Whether a scheduled update may replace this plugin's content. |
| `installedAtUtc` | timestamp | When the content on disk was materialised. |
| `fileCount` | int | Files the install recorded, so a modified count is interpretable. |
| `trustState` | int | Integrity of the content on disk. See below. |
| `trustDetail` | string? | What diverged, when `trustState` is `2`. |
| `updateState` | int | Update availability at the source. See below. |
| `availableVersion` | string? | Revision the source currently resolves to, when it was probed. |
| `updateProbeError` | string? | Why the probe failed, when `updateState` is `4`. |
| `deployedExtensionId` | string? | The gateway extension this plugin deployed, when it carried one. |
| `navHidden` | bool | Whether this plugin's contributed nav entries are hidden from the sidebar. |

### `trustState`

Serialised as a **number**. Three states rather than a boolean, because "we did not
look" and "we looked and it is fine" are different answers, and collapsing them
would present an unverifiable plugin as a trusted one.

| Value | Name | Meaning |
|-------|------|---------|
| `0` | Unverified | No content hash catalog exists, so integrity cannot be attested. |
| `1` | Verified | Every recorded file is present and every hashed file matches. |
| `2` | Modified | Content diverges from the installed record. Reported, never repaired — silently re-materialising would destroy the evidence. |

### `updateState`

Also a **number**. `0` is the default and is deliberately *not* collapsed into "up to
date": probing costs a round trip against the source, so the list does not pay it
unless asked, and reporting "current" without looking would be a claim rather than a
finding.

| Value | Name | Meaning |
|-------|------|---------|
| `0` | Unknown | The source was not probed. |
| `1` | Current | The source resolves to the revision already on disk. |
| `2` | UpdateAvailable | The source resolves to a different revision. |
| `3` | Pinned | Updates are disabled, so the source is not probed at all. |
| `4` | ProbeFailed | The source could not be probed; `updateProbeError` says why. |

### PluginOperationResponse

Returned by install, update and remove.

| Field | Type | Notes |
|-------|------|-------|
| `outcome` | string | Lifecycle outcome name. |
| `name` | string | Plugin identifier. |
| `previousVersion` | string? | Revision installed before, when there was one. |
| `resolvedVersion` | string? | Revision now on disk. |
| `restartRequired` | bool | See below. |
| `plugin` | PluginPortalRow? | The plugin's row, when it is still installed. `null` after a remove. |

`restartRequired` is `true` when the plugin deployed a carried extension. Extension
endpoints are mapped once at startup, so a carried extension is on disk but **inert
until the gateway restarts** — the difference between "installed" and "installed and
working".

### MarketplaceSource

| Field | Type | Notes |
|-------|------|-------|
| `name` | string | Stable identifier, lowercase kebab-case, derived from the URL when not supplied. |
| `url` | string | Git URL of the repository to look in. |
| `reference` | string? | Branch, tag or commit to read; `null` for the default branch. |
| `addedAtUtc` | timestamp | When the operator added it. |
| `lastRefreshedAtUtc` | timestamp? | When its contents were last read *successfully*; `null` if never. |
| `lastError` | string? | Why the last refresh failed; `null` when it succeeded. |
| `kind` | string? | What the source turned out to be — `catalog` for a repository carrying a catalogue of other repositories. |
| `offerings` | MarketplaceOffering[] | What it offers, as of the last successful refresh. |

`lastRefreshedAtUtc` and `lastError` are separate on purpose: a source that refreshed
yesterday and failed today still has offerings worth listing, and one field cannot say
both things.

### MarketplaceOffering

| Field | Type | Notes |
|-------|------|-------|
| `name` | string | Plugin name, as the installer will record it. |
| `url` | string | Git URL to install from. For a catalogue, the entry's own repository. |
| `version` | string? | Version from the plugin manifest, when it could be read. |
| `description` | string? | One-line summary for the listing. |
| `reference` | string? | Reference to install: a tag or commit when the source pins one. |
| `carriesExtension` | bool | Whether installing this runs third-party code in the gateway. |
| `error` | string? | Why this entry could not be read, when it could not. |
| `versionWarning` | string? | A problem with the catalogue's pinned `version` itself. |

`error` and `versionWarning` are not the same thing. An entry with a `versionWarning`
**was** read, so it still lists and still installs — the warning says the pin does not
match any tag the repository has.

---

## Installed plugins

### `GET /api/plugins`

Lists every installed plugin, ordered by name. Returns `200 OK` with an array of
`PluginPortalRow`.

### `GET /api/plugins/{name}`

Returns `200 OK` with the row, `400 Bad Request` when `name` is blank, or
`404 Not Found` when the plugin is not installed.

### `PUT /api/plugins/{name}/update-preference`

Sets whether scheduled updates may replace this plugin's content.

```json
{ "updatesEnabled": false }
```

Returns `200 OK` with the updated row. `400` when `name` is blank or the body is
missing; `404` when the plugin is not installed.

Written back to the installed record rather than held in memory: a toggle that did not
survive a restart would assert something the gateway never stored.

### `PUT /api/plugins/{name}/nav-visibility`

Shows or hides the plugin's contributed nav entries.

```json
{ "navHidden": true }
```

Same responses as `update-preference`.

### `POST /api/plugins/install`

```json
{
  "source": "https://github.com/owner/repo",
  "name": null,
  "reference": null,
  "updatesEnabled": true,
  "acknowledgeCarriedExtension": false
}
```

| Field | Notes |
|-------|-------|
| `source` | Repository URL to install from. Required. |
| `name` | Expected plugin name, or `null` to accept whatever the manifest declares. |
| `reference` | Branch, tag or commit, or `null` for the default branch. |
| `updatesEnabled` | Whether scheduled updates may replace the content. Defaults to `true`. |
| `acknowledgeCarriedExtension` | Whether the caller accepts a plugin that carries a gateway extension. Defaults to `false`. |

Returns `200 OK` with a `PluginOperationResponse`, or `400 Bad Request` when `source`
is missing or the install failed:

```json
{
  "error": "…",
  "errors": [ { "field": "…", "message": "…" } ]
}
```

**A plugin that carries an extension is code loaded in-process at full trust.** With
`acknowledgeCarriedExtension` left `false`, install refuses one rather than proceeding,
so the caller re-issues the request knowing what it is agreeing to. A caller can see
this coming: the offering's `carriesExtension` says so before anyone clicks.

### `POST /api/plugins/{name}/update`

Re-resolves the plugin's source and replaces its content if the source moved. Returns
`200 OK` with a `PluginOperationResponse`, or `400` when `name` is blank or the update
failed.

### `DELETE /api/plugins/{name}`

Removes the plugin and any extension it deployed. Returns `200 OK` with a
`PluginOperationResponse` whose `plugin` is `null`, `400` when `name` is blank, or
`404` when the plugin is not installed — "not installed" is the only way remove fails,
and a `404` says that more precisely than a `400` would.

---

## Marketplace sources

### `GET /api/plugins/sources`

Lists every configured source with whatever the last read found, ordered by name.
Returns `200 OK` with an array of `MarketplaceSource`.

### `POST /api/plugins/sources`

```json
{ "url": "https://github.com/owner/repo", "name": null, "reference": null }
```

`name` is optional — a derived name carries owner and repository, so two publishers'
similarly named repositories do not collide. `reference` is optional and means the
default branch.

Returns `201 Created` with a `Location` of `/api/plugins/sources/{name}` and the
probed source in the body. The source is read **before** the response returns, so the
caller sees what it offers without a second call — finding out what is in it is the
point of adding one.

| Status | When |
|--------|------|
| `400` | `url` missing, or not an absolute `http://` / `https://` address. |
| `409` | A source of that name is already configured. |

Adding the same source twice is a conflict rather than a silent overwrite: the stored
record carries the last read, and quietly replacing it would discard offerings the
portal may be listing. `refresh` is the route that deliberately re-reads.

**A probe that fails still stores the source**, carrying its error. A first read can
fail for reasons that have nothing to do with the URL — the network, a rate limit, a
repository briefly unavailable — and discarding the entry would make the operator
retype it. A bad URL is visible on the row and one `DELETE` away; a good source lost to
a flaky network is not recoverable by the operator at all.

### `POST /api/plugins/sources/refresh`

Re-reads every configured source. Returns `200 OK` with the full array.

One unreadable source does not stop the others being refreshed — each carries its own
error, which is the whole reason the error lives on the source rather than on the
response.

### `POST /api/plugins/sources/{name}/refresh`

Re-reads one source and stores what it now offers. Returns `200 OK` with the probed
source, `400` when `name` is blank, or `404` when it is not configured.

**A refresh that cannot read the source is still a `200`** carrying the source with its
error, not a `5xx`. The request succeeded — the gateway asked and recorded the answer —
and an error status would leave a caller unable to tell "this source is unreachable"
from "the refresh call itself broke".

### `DELETE /api/plugins/sources/{name}`

Returns `200 OK` with `{ "removed": "<name>" }`, `400` when `name` is blank, or `404`
when the source is not configured.

Removing a source removes **only the listing**. Plugins installed from it stay
installed and keep working: an installed plugin owns its own files and its own source
URL, and nothing about it is read back through the source it was found in. Forgetting
where you found something is not the same as uninstalling it.

---

## See also

- [Plugins architecture](../architecture/plugins.md)
- [Plugin repository requirements](../development/plugin-repository-requirements.md)
- [Managing plugins in the portal](../user-guide/plugins.md)
