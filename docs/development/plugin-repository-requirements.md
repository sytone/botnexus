# Plugin repository requirements

What a GitHub repository must contain for the BotNexus marketplace to fetch, validate and
install it. Every rule below is taken from the code that will actually read the repo, with the
source file named so it can be re-checked rather than trusted.

Read this before authoring a plugin repo. A repo that does not meet the **Required** section
will be rejected at install time with a message naming the offending field.

## 1. How the installer reads the repo

`GitPluginSourceFetcher` runs, literally:

```
git clone --quiet [--branch <reference>] -- <source> .
git rev-parse HEAD
```

Consequences that constrain the repo layout:

- **The whole repository becomes the plugin.** There is no sparse checkout, no subdirectory
  option, no monorepo path. One plugin per repository, at the repository root.
- **`.git/` is excluded when the staged clone is promoted** to `~/.botnexus/plugins/<name>/`
  (`PluginLifecycleManager.Promote`). Everything else in the repo is copied verbatim — build
  output, `node_modules`, screenshots and all. Keep the repo lean; nothing is filtered for you.
- **The recorded version is the commit SHA, never the branch name.** `reference` may be a
  branch, tag or commit; what gets stored is what `rev-parse HEAD` returned, so "has the source
  moved since install?" stays answerable.
- The source is passed after `--`, so a URL beginning with a dash cannot be reinterpreted as a
  git option. Private repos work via git's own authentication.

## 2. Required: the manifest

**Exact path, at the repository root:**

```
.botnexus-plugin/plugin.json
```

A directory without this file is not a plugin and is rejected — `PluginManifestParser`
returns `Plugin manifest not found` rather than treating it as an empty success.

The manifest is validated against `plugin-manifest.schema.json`, which is
**`"additionalProperties": false`**. Any field not listed below fails validation. There is no
best-effort coercion: an unknown shape is an error, not a warning.

Only **`name`** is required. Everything else is optional but recommended.

| Field | Type | Rules |
|---|---|---|
| `name` | string | **Required.** `^[a-z0-9]+(-[a-z0-9]+)*$`, 1–64 chars. Lowercase kebab-case. |
| `description` | string | Shown in marketplace listings. Write this — it is the card copy. |
| `version` | string | Semver: `^\d+\.\d+\.\d+(-prerelease)?(+build)?$` |
| `author` | object | `{ "name": <required>, "email"?, "url"? }` — no other keys allowed. |
| `homepage` | string | Project or docs URL. |
| `repository` | string | Source repository URL. |
| `license` | string | SPDX identifier, e.g. `MIT`. |
| `keywords` | string[] | Used for marketplace search. |
| `skills` | string[] | Explicit skill paths. **Omit** to use convention discovery. |
| `agents` | string[] | Explicit agent paths. Omit for convention discovery. |
| `commands` | string[] | Explicit command paths. Omit for convention discovery. |
| `hooks` | string | Single path (not an array). |
| `mcpServers` | string | Single path (not an array). |

**The `name` in the manifest is authoritative.** It becomes the install directory
(`~/.botnexus/plugins/<name>/`) and the key in the installed-plugin record. If the marketplace
requested a specific name and the manifest disagrees, the install is rejected:
`declares name 'x', which does not match the requested 'y'`.

Minimal valid manifest:

```json
{
  "name": "my-plugin",
  "description": "One line describing what this plugin gives an agent.",
  "version": "1.0.0",
  "author": { "name": "Your Name" },
  "license": "MIT",
  "keywords": ["example"]
}
```

## 3. Skills

Skills reach agents from a plugin exactly as they do from the global folder. Verified on a live
gateway: an agent's own `skills_list` returns plugin-carried skills alongside global ones.

Note the portal's **Skills page lists only `~/.botnexus/skills`** — it is a file browser over that
one folder. Plugin skills work while not appearing there; do not read their absence as a failure.

Layout, relative to the repository root:

```
skills/
  <skill-name>/
    SKILL.md          <- required; a directory without it is silently skipped
    references/       <- optional supporting dirs, surfaced as load-on-demand linked files
    templates/
    scripts/
    assets/
```

- **Exactly one level of nesting.** `SkillDiscovery.ScanDirectory` enumerates the immediate
  subdirectories of `skills/` and looks for `SKILL.md` in each. `skills/a/b/SKILL.md` is not found.
- Only `references/`, `templates/`, `scripts/`, `assets/` are treated as supporting directories.
- `SKILL.md` must be **≤ 512 KB** or the skill is skipped with a warning.

### SKILL.md format

YAML frontmatter delimited by `---`, which must be the **first non-empty line** of the file:

```markdown
---
name: my-skill
description: When to use this skill and what it does. This is what the model reads to decide.
license: MIT
compatibility: Requires git on PATH
allowed-tools: read, write, bash
disable-model-invocation: false
metadata:
  category: example
---

# My skill

Body content: trigger conditions, numbered steps, exact commands, pitfalls, verification steps.
```

Recognised frontmatter keys (`SkillParser`) — anything else is ignored, not an error:

| Key | Notes |
|---|---|
| `name` | Must **not** contain a double hyphen (`--`). |
| `description` | Must be non-empty. **≤ 1024 characters.** This is the trigger text. |
| `license` | Free text. |
| `compatibility` | **≤ 500 characters.** |
| `allowed-tools` | Free text list. |
| `disable-model-invocation` | Boolean. |
| `metadata` | Nested map of string values. |

### Name collisions

Plugin skills are scanned **first**, which in this merge means **lowest priority**. A
same-named global, agent or workspace skill wins. Namespace your skill names to avoid being
silently shadowed.

## 4. Optional: a marketplace catalog

Only needed if a repo publishes a *list* of plugins rather than being one. Validated against
`marketplace.schema.json`, also `additionalProperties: false`.

```json
{
  "name": "my-catalog",
  "owner": { "name": "Your Name" },
  "description": "What this catalog offers.",
  "plugins": [
    {
      "name": "my-plugin",
      "source": "https://github.com/you/my-plugin.git",
      "description": "Card copy.",
      "version": "1.0.0",
      "keywords": ["example"]
    }
  ]
}
```

Required: `name`, `owner.name`, `plugins`. Each entry requires `name` and `source`.

## 5. Carrying code or UI

**Corrected 2026-08-30. This section previously said a plugin could not ship code or UI. That is
no longer true and had become actively misleading** — the reference plugin
`botnexus-agent-builder` ships a carried extension with a web app and serves it in-portal.

A plugin carries code by pointing at an extension manifest:

```json
{
  "name": "botnexus-agent-builder",
  "version": "1.0.0",
  "extension": { "manifest": "botnexus-extension.json" }
}
```

`extension.manifest` is a plugin-relative path to a normal `botnexus-extension.json`. Everything
beside it in the repo is deployed as the extension, so the layout is just an extension repo with a
plugin manifest added.

**The manifest's directory is the deploy unit**, and that decides where to put it:

- **A payload-only repo** — nothing in it but the built extension — keeps the manifest at the
  root. `.botnexus-plugin/`, `skills/` and `.git/` are excluded, so the root is already clean.
- **A repo that is also the source** of the extension must put the manifest in a
  **subdirectory** (`extension/`, say). Those three exclusions apply *only* when the manifest sits
  at the plugin root; a root manifest in a source repo deploys `src/`, `package.json`,
  `node_modules/` and the rest into `~/.botnexus/extensions/<id>/`.

Both are valid. The choice follows from whether the repository is a payload or a project.

Three rules the installer enforces, each of which refuses the install rather than warning:

- **`"endpointPhase": "after-authentication"` is required** in the extension manifest. Plugin code
  is third-party; routes mapping ahead of authentication would be unauthenticated surface on the
  gateway. A plugin declaring anything else is refused, naming the field.
- **The operator must consent.** Installing a plugin that carries an extension needs an explicit
  acknowledgement — the portal shows what is being installed and asks; the API takes
  `"acknowledgeCarriedExtension": true`.
- **Compatibility is checked.** A prebuilt extension declaring an incompatible gateway contract
  range is refused rather than loaded, so a stale binary cannot crash the host it lands on.
  Declare the range on the extension manifest, against the `BotNexus.Gateway.Abstractions`
  **assembly version** — that is the contract that actually breaks:

  ```json
  "compatibility": {
    "minAbstractionsVersion": "0.45.0",
    "maxAbstractionsVersion": "0.46.0"
  }
  ```

  The upper bound is **exclusive**, because a ceiling is nearly always "up to the next breaking
  release". Both bounds are optional, and an absent or unparseable one means no constraint rather
  than a failure — so an extension written before the field existed keeps loading.

Two consequences worth designing around:

- **A restart is required** before a carried extension serves anything. Extension endpoints map
  once at gateway startup, so the install result returns `"restartRequired": true` and the plugin
  is on disk but inert until then.
- **Updating one is remove → restart → install**, not update-in-place. The running gateway has the
  assemblies loaded and cannot replace them underneath itself; attempting an update is refused with
  that instruction.

Ship the entry assembly, its `.deps.json`, and its **private** dependencies. Leave shared
contracts (`BotNexus.Gateway.Abstractions`, `BotNexus.Domain`, …) out — but not for the reason
the older design notes give. `ExtensionAssemblyLoadContext.Load` unifies with the host
**categorically**: any assembly the host has loaded, ships in its base directory, or owns
resolves from the host's default context, and the extension's private copy is never consulted.
So a shipped contract assembly is **dead weight rather than a live hazard** — it inflates the
plugin and misleads the next reader into thinking the version in it matters. It does not.

Ship any web assets in `wwwroot/` beside the extension manifest. They travel with the plugin and
are **not** in the BotNexus repo, so deploying the extension from a BotNexus build gives the shell
without its UI — the plugin is the only thing that carries them.

### Contributing a portal nav entry

A carried extension that serves a UI declares its left-nav entry on the **extension** manifest,
not the plugin manifest — nav is a property of the thing that serves the path, so an extension
built from source gets it on the same terms as one delivered by a plugin:

```json
"nav": [
  { "id": "agent-builder", "label": "Agent Builder", "path": "/agent-builder/",
    "icon": "agents", "order": 50, "external": true, "fullPage": false }
]
```

- `path` must be site-relative and begin with `/`, or the entry is dropped rather than rendered.
- `icon` must be a key in the portal's `IconLibrary`; an unknown name falls back to a default
  rather than failing, and arbitrary markup is never accepted.
- `external` marks a path served outside the Blazor router, so client-side routing cannot reach it.
- `fullPage: false` frames the view inside the portal, keeping sidebar, header and theme;
  `true` replaces the whole window, which reads as leaving the app and is therefore opt-in.

### Still not wired

The manifest accepts these and the schema documents them, but nothing in the running gateway
consumes them. Declaring them is harmless; relying on them will not work.

- `agents`, `commands`, `hooks`, `mcpServers` — parsed, never discovered.

## 6. Install-time failure modes

Worth knowing so the repo can avoid them:

| Condition | Result |
|---|---|
| No `.botnexus-plugin/plugin.json` at root | Rejected: manifest not found |
| Unknown field in the manifest | Rejected, naming the field |
| `name` not lowercase kebab-case | Rejected on pattern |
| Plugin of that name already installed | Rejected — remove or update first |
| `~/.botnexus/plugins/<name>/` exists but is unrecorded | Rejected, **not** overwritten |
| `git clone` fails (private, bad ref, network) | Rejected with git's exit code and stderr |

Nothing is written to the plugin directory unless the entire fetch **and** validation
succeeded — installs are staged, then promoted.

## 7. Checklist for the plugin author

- [ ] `.botnexus-plugin/plugin.json` exists at the repository root
- [ ] `name` is lowercase kebab-case and matches the intended install directory
- [ ] `description` and `keywords` are filled in — they are the marketplace card
- [ ] No fields beyond the table in §2
- [ ] Skills live at `skills/<name>/SKILL.md`, exactly one level deep
- [ ] Every `SKILL.md` opens with `---` frontmatter carrying a non-empty `description` ≤ 1024 chars
- [ ] No skill `name` contains `--`
- [ ] Repo contains no build output or large assets — everything except `.git/` is copied on install
- [ ] A tag or release commit exists to install as a pinned `reference`

If the plugin carries an extension (§5), also:

- [ ] `extension.manifest` points at the `botnexus-extension.json`, plugin-relative
- [ ] That manifest declares `"endpointPhase": "after-authentication"` — the install is refused
      without it
- [ ] Any web assets are in `wwwroot/` beside it, since nothing else carries them
- [ ] The extension's compatibility range admits the gateways you expect it to install on
- [ ] Bump the tag when you change it: updating a carried extension is remove -> restart ->
      install, so an operator installs a *new reference* rather than refreshing the old one

## Sources

- `src/extensions/BotNexus.Extensions.Plugins/Schemas/plugin-manifest.schema.json`
- `src/extensions/BotNexus.Extensions.Plugins/Schemas/marketplace.schema.json`
- `src/extensions/BotNexus.Extensions.Plugins/PluginManifestParser.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/GitPluginSourceFetcher.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginLifecycleManager.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginSkillRootResolver.cs`
- `src/extensions/BotNexus.Extensions.Skills/SkillDiscovery.cs`
- `src/extensions/BotNexus.Extensions.Skills/SkillParser.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginExtensionDeployer.cs` — the
  install-time rules for a carried extension
- `src/gateway/BotNexus.Gateway.Contracts/ExtensionModels.cs` — `ExtensionManifest`,
  `ExtensionCompatibility`, `ExtensionNavEntry`
- `src/gateway/BotNexus.Gateway/Extensions/AssemblyLoadContextExtensionLoader.cs` — the ABI gate
