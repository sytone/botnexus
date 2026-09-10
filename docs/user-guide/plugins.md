# Plugins & the marketplace

A **plugin** is a git repository you install into BotNexus from the portal. It can carry skills,
and it can carry a prebuilt gateway extension — code and UI that becomes part of the portal.

The Plugins page has two URL boxes. They look alike and do different things:

| Box | What it does | Use it for |
|---|---|---|
| **Install one plugin directly** | Installs immediately | A repository that *is* a plugin, when you already know you want it |
| **Repositories to browse** | Reads and lists; **installs nothing** | A plugin repository you want to keep an eye on, or a catalog listing several |

Adding a repository never installs anything. It records a URL, reads what is there, and lists it;
you then press **Install** on the entry you want. Everything below about consent and restarts
applies whichever route you take, because both end in the same install.

A **catalog** only works in the second box. It is a repository of pointers, not a plugin, so
installing it directly is refused - with an error saying exactly that, and where it belongs.

## Table of contents

- [Plugin, extension or skill?](#plugin-extension-or-skill)
- [Repositories](#repositories)
- [Installing a plugin](#installing-a-plugin)
- [Managing installed plugins](#managing-installed-plugins)
- [Building a plugin](#building-a-plugin)
- [Carrying an extension](#carrying-an-extension)
- [Contributing a menu entry](#contributing-a-menu-entry)
- [What a plugin needs to work](#what-a-plugin-needs-to-work)
- [When an install is refused](#when-an-install-is-refused)

## Plugin, extension or skill?

These three words are easy to confuse, and the difference decides how you ship something.

| | What it is | How it arrives | Needs a restart? |
|---|---|---|---|
| **Skill** | Markdown that teaches an agent something | Inside a plugin, or dropped in a skills folder | No |
| **Extension** | A .NET assembly the gateway loads — tools, channels, UI | Built from source, or carried by a plugin | Yes |
| **Plugin** | A git repo bundling skills and optionally one extension | Installed from the portal | Only if it carries an extension |

A plugin is the *delivery mechanism*. What it delivers is skills, an extension, or both.

## Repositories

**Plugins → Repositories to browse.** A repository is a place BotNexus looks for plugins. Adding
one is not installing anything: it records a URL, reads what is there, and lists it. Nothing runs
until you press **Install** on an entry.

Paste the URL and press **Add repository**. It is read straight away, so its plugins appear in the
same breath:

```
https://github.com/owner/my-plugins.git      reference: main
```

Repository URLs must be `http://` or `https://`. That address is handed to git, so a local path or
a `file://` URL is refused — otherwise anyone able to reach the portal could make the gateway read
a directory. To install from a local path, use **Install one plugin directly** instead, which is
you naming a path you already chose.

### What a repository can be

BotNexus works out which it is by looking, not by asking you:

| Badge | What it found |
|---|---|
| **Single plugin** | The repository *is* a plugin — it has `.botnexus-plugin/plugin.json` |
| **Catalog** | It has a `marketplace.json` listing plugins that live in other repositories |
| **Unread** | Neither, or it could not be reached — the reason is shown on the row |

A repository can be both, in which case the catalog wins: it is the broader claim.

### What the listing tells you

Each entry shows its name, version and description, and two badges that matter:

- **Carries an extension** — installing this deploys code that runs inside the gateway process at
  full trust. You are told here, *before* you press Install, not only at the consent prompt
  afterwards. It is read from the plugin's own manifest, so a catalog cannot list a plugin as
  harmless when it is not.
- **Installed** — you already have a plugin of that name, so there is nothing to install. Matched
  on name, because the same plugin can legitimately be listed by more than one catalog.

An entry the catalog lists but BotNexus could not read shows its reason and cannot be installed.
The rest of the listing is unaffected — one bad entry does not hide the others.

### Refreshing

**Refresh** re-reads one repository; **Refresh all** re-reads every one. Nothing is re-read on its
own, so what you see is what was there at the last read.

If a repository cannot be reached, its error appears on the row and **its previous listing stays**.
That is deliberate: a repository that is briefly down can still be installed from, and blanking the
list would take away something that still works. The "last read" time is not moved forward either —
a failed read read nothing, and pretending otherwise would make stale entries look fresh.

### Removing a repository

**Remove** takes two clicks and only stops the listing:

> Stop listing 'x'? Plugins already installed from it stay installed and keep working.

Forgetting where you found something is not uninstalling it. To remove the plugin itself, use
**Remove** on its row in the installed table below.

### Publishing a catalog

To offer several plugins from one address, put a `marketplace.json` at the repository root. Each
entry points at the repository the plugin actually lives in.

```json
{
  "name": "acme-plugins",
  "owner": { "name": "Acme", "url": "https://acme.example" },
  "description": "Plugins maintained by Acme.",
  "plugins": [
    {
      "name": "acme-dashboard",
      "source": "https://github.com/acme/dashboard.git",
      "version": "v3.2.0",
      "description": "Ops dashboard for the portal.",
      "keywords": ["ops", "dashboard"]
    }
  ]
}
```

| Field | Required | Notes |
|---|---|---|
| `name` | yes | Lowercase kebab-case, unique |
| `owner.name` | yes | Who is accountable for the listing; `email` and `url` optional |
| `description` | no | Summary of the catalog |
| `plugins[].name` | yes | Must match the plugin's own manifest name |
| `plugins[].source` | yes | Repository URL the plugin lives in |
| `plugins[].version` | no | Reference to read; omit to track the default branch |
| `plugins[].description` | no | Used when the plugin's own manifest carries no description |
| `plugins[].keywords` | no | Discovery terms |

Unknown fields are rejected rather than ignored, so a typo is reported instead of silently doing
nothing.

**A catalog is a signpost, not a source of truth.** BotNexus fetches each entry and reads that
plugin's own manifest for its name, version, description and whether it carries an extension.
Nothing you write in a catalog can overstate or understate what a plugin does — so listing someone
else's plugin is safe for whoever installs it, and the catalog cannot hide code.

Your `description` is the one exception, and only ever as a **fallback**: it is shown when the
plugin's own manifest carries none, and is ignored the moment the plugin describes itself. So a
catalog can introduce a plugin whose author never wrote a summary, but cannot restate one who did.

The version and the extension badge are never yours to supply — they decide what installing *does*.
Only when an entry cannot be read at all does the row fall back to your `version` and
`description`, so a broken entry still says something useful instead of vanishing.

## Installing a plugin

**Plugins → Install one plugin directly.** Paste the repository URL, optionally give a branch, tag
or commit, and press **Install**. This box goes straight to installing, without listing anything -
use **Repositories to browse** if you would rather see what a repository offers first.

Pressing **Install** on a listed entry does exactly the same thing with that entry's URL, so the
consent and restart rules below apply to both routes.

```
https://github.com/owner/my-plugin.git      reference: v1.0.0
```

Any git source works, including a local path and a private repository the gateway's git can
already authenticate to.

**Pin to a tag or commit where you can.** The reference is what an update re-resolves later, and
the exact revision installed is recorded separately, so BotNexus can always answer "has the source
moved since I installed this?".

### If the plugin carries code

A plugin that carries an extension is not the same kind of thing as one carrying skills. A skill is
prompt text; a carried extension is an assembly that **runs inside the gateway process at full
trust, with no sandbox**.

So the install stops and asks:

> Plugin 'x' carries a gateway extension, which runs code in the gateway process at full trust.

Choose **Install anyway** to proceed, or **Cancel**. Your entered URL is kept either way.

You are asked *after* the fetch rather than before, because whether a repository carries code is
only knowable once its manifest has been read. Nothing is written to disk until you answer.

### Restart to activate

A carried extension is placed on disk immediately, but the gateway maps extension endpoints once at
startup. The portal says so:

> 'x' deployed a gateway extension. Restart the gateway to activate it.

Skills need no restart — they are picked up the next time an agent builds a prompt.

## Managing installed plugins

Each row on the Plugins page offers:

- **Auto-update** — whether a scheduled update may replace this plugin's content. Turn it off to
  pin the plugin at the revision you installed.
- **In menu** — whether this plugin's contributed menu entries appear in the sidebar. Use it to
  keep a long sidebar readable. This is presentation only: the plugin stays installed, its
  extension stays loaded, and its pages stay reachable by URL. Skills-only plugins show a dash,
  because they have no menu entry to control.
- **Update** — appears when the source has moved. A plugin carrying a deployed extension cannot be
  updated in place; remove it, restart, then install the new version. The gateway holds those
  assemblies open, so replacing them underneath itself would fail.
- **Remove** — takes two clicks. Removal deletes exactly the files the install recorded, plus any
  extension the plugin deployed. Content you added alongside a plugin is never touched.

Removing a plugin that deployed an extension deletes the extension directory at once, but its code
keeps running until the gateway restarts.

## Building a plugin

One plugin per repository, at the repository root. The installer clones the whole repository — there
is no subdirectory or monorepo option — and copies everything except `.git/` into
`~/.botnexus/plugins/<name>/`. Keep the repository lean; nothing is filtered for you.

### The manifest

Required, at this exact path:

```
.botnexus-plugin/plugin.json
```

Only `name` is required, but a marketplace listing with no description is not much of a listing.

```json
{
  "name": "my-plugin",
  "description": "One line describing what this gives an agent.",
  "version": "1.0.0",
  "author": { "name": "Your Name" },
  "homepage": "https://github.com/owner/my-plugin",
  "repository": "https://github.com/owner/my-plugin.git",
  "license": "MIT",
  "keywords": ["example"]
}
```

| Field | Type | Notes |
|---|---|---|
| `name` | string | **Required.** Lowercase kebab-case, 1–64 chars. Becomes the install directory. |
| `description` | string | Shown in the listing. |
| `version` | string | Semantic version. |
| `author` | object | `{ name, email?, url? }` — `name` required. |
| `homepage`, `repository`, `license` | string | Project URL, source URL, SPDX identifier. |
| `keywords` | string[] | Search terms. |
| `skills`, `agents`, `commands` | string[] | Explicit paths. Omit to discover by convention. |
| `hooks`, `mcpServers` | string | A single path, not an array. |
| `extension` | object | A carried gateway extension — see below. |

**The schema rejects unknown fields.** A field you invent fails validation rather than being
ignored, so a typo is caught at install instead of silently doing nothing.

**The manifest's `name` wins.** It becomes the install directory and the record key.

### Skills

```
skills/
  my-skill/
    SKILL.md          <- required
    references/       <- optional supporting folders
    templates/
    scripts/
    assets/
```

- **Exactly one level deep.** `skills/a/b/SKILL.md` is not found.
- A folder without `SKILL.md` is skipped **silently** — the most common "why is nothing showing up".
- `SKILL.md` opens with `---` YAML frontmatter carrying a non-empty `description` (≤ 1024 chars),
  and must be under 512 KB.
- Skill names must not contain a double hyphen.
- Plugin skills lose a name collision with a global, agent or workspace skill, so namespace yours.

See the [Skills guide](../skills.md) for the full `SKILL.md` format.

## Carrying an extension

Add one object to the manifest:

```json
"extension": { "manifest": "botnexus-extension.json" }
```

It points at an ordinary extension manifest in the same repository. Recommended layout:

```
.botnexus-plugin/plugin.json      # plugin metadata + the "extension" object
botnexus-extension.json           # the extension manifest
BotNexus.Extensions.MyThing.dll   # the prebuilt entry assembly and its private dependencies
wwwroot/                          # built static assets, if it serves UI
skills/                           # optional; skills work alongside
```

**The extension must be prebuilt and committed.** Plugins are copied verbatim — there is no build
step at install, and no guaranteed SDK on the host.

On install, the extension's folder is copied to `~/.botnexus/extensions/<id>/`, where the gateway's
normal loader finds it. `.botnexus-plugin/`, `skills/` and `.git/` are excluded, so plugin metadata
never leaks into the extensions tree. The plugin record remembers which extension it deployed, so
removal cleans up both.

An extension whose directory already exists is **not** overwritten — the running gateway has those
assemblies loaded. Remove and restart first.

## Contributing a menu entry

An extension that serves a UI can put itself in the portal sidebar, with no change to the portal.
Declare it in `botnexus-extension.json`:

```json
"nav": [
  {
    "id": "my-thing",
    "label": "My Thing",
    "path": "/my-thing",
    "icon": "tools",
    "order": 65,
    "external": true
  }
]
```

| Field | Notes |
|---|---|
| `id` | Lowercase kebab-case, unique across all contributions. |
| `label` | Sidebar text, trimmed to 40 characters. |
| `path` | Must be site-relative, beginning with `/`. Anything else is dropped. |
| `icon` | A portal icon **name**, never markup. An unknown name falls back to a default. |
| `order` | Sorts among the built-ins, which sit 10 apart: Agents 60, Cron 70. So 65 lands between them. |
| `external` | `true` when the path is served by your extension rather than the Blazor router. |
| `fullPage` | Optional. `true` replaces the whole window; omit to embed. |

### Views are embedded by default

A contributed view opens **inside** the portal, framed in the main canvas with the sidebar and
header still around it, at `/extension/<id>`. Taking over the window is opt-in via `fullPage`,
because leaving the portal entirely is the more disruptive behaviour and should be asked for.

**Your view follows the portal's light/dark toggle automatically** if it styles itself from
`prefers-color-scheme`, which is how a standalone web app normally does it. The portal drives the
frame's colour scheme; you need no code and no knowledge that the portal exists.

Note the portal URL does not track navigation *inside* a framed view, so deep links into your
view's internal state are not currently shareable.

### Claiming your path

Extensions register their middleware before the portal's, so a UI extension's route is served by
that extension rather than being swallowed by the portal's catch-all. You do not need to register
your path anywhere. If your extension registers *endpoints* rather than middleware, those are
recognised automatically too.

## What a plugin needs to work

- [ ] `.botnexus-plugin/plugin.json` at the repository root
- [ ] `name` lowercase kebab-case, no fields outside the table above
- [ ] `description` and `keywords` filled in — they are the listing
- [ ] Skills at `skills/<name>/SKILL.md`, exactly one level deep, each with frontmatter and a
      non-empty `description`
- [ ] A carried extension prebuilt and committed, with its `botnexus-extension.json`
- [ ] Shared framework assemblies trimmed from the repository where possible — the gateway supplies
      its own, and a mismatched copy is how a binary plugin breaks on a different gateway
- [ ] No build output or large assets you do not need — everything but `.git/` is copied
- [ ] A tag or release commit to install as a pinned reference

## When an install is refused

Failures name the field at fault rather than failing generically.

| Message | Meaning |
|---|---|
| Plugin manifest not found | No `.botnexus-plugin/plugin.json` at the repository root |
| is a marketplace catalog, not a plugin | A catalog URL went into the install box; add it under **Repositories to browse** instead |
| Field '…' is invalid | A field failed the schema — often an unknown field, or a name that is not kebab-case |
| carries a gateway extension | Consent needed; re-issue with **Install anyway** |
| resolves outside the plugin directory | The `extension.manifest` path tried to escape the plugin |
| must be prebuilt and committed | The manifest names an entry assembly that is not in the repository |
| is already deployed | An extension of that id already exists; remove it and restart first |
| is already installed | A plugin of that name is installed; remove or update it |
| git clone … failed | Bad URL, bad reference, private repo the gateway cannot authenticate to |
| must be an absolute http:// or https:// | A repository was added with a local path or another scheme |
| neither marketplace.json nor …plugin.json | The repository is not a plugin and does not list any |
| is already configured | A repository of that name is already added; **Refresh** it instead |

Nothing is written unless the whole fetch **and** validation succeed, so a refused install leaves
no partial state behind.

## Next steps

- [Skills](../skills.md) — the `SKILL.md` format in full
- [Extensions](extensions.md) — writing the code a plugin can carry
- [Troubleshooting](troubleshooting.md)
- [Plugins REST API](../api/plugins.md) — the same operations, for scripting them
