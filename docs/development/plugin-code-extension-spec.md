# Spec: extending the plugin format to carry code/UI extensions

**Status:** items 1-3 delivered; item 4 outstanding · **Reference consumer:** the Agent Builder extension, a
working `IEndpointContributor` serving a SPA at `/agent-builder` · **Supersedes:** the
proposal draft of the same name.

Every requirement is tied to how the gateway behaves today. Source files are listed in §10 so
this can be verified rather than trusted. Sections marked **[correction]** changed from the
draft after the claims were checked against the code; the reasons are stated inline rather
than silently applied.

---

## 1. Purpose

A **plugin** (git repo, marketplace-installed, `.botnexus-plugin/plugin.json`) can carry
**skills only**. `additionalProperties: false` on the schema means the author literally cannot
declare code, and nothing in the gateway loads code from a plugin directory. Runnable code and
UI ship only as a **gateway extension** (`botnexus-extension.json` + an assembly loaded by
`AssemblyLoadContextExtensionLoader`), which has **no marketplace distribution path** — it is
built from source and deployed by `serve gateway`.

Agent Builder is squarely a code/UI extension and cannot be expressed as a skill. This spec
defines what the plugin format and gateway must add so a marketplace plugin can carry, install
and integrate a code/UI extension as a drop-in, with no hand-patching of the portal.

## 2. Current state — two disjoint mechanisms

| | Plugin | Extension |
|---|---|---|
| Manifest | `.botnexus-plugin/plugin.json` (schema `additionalProperties: false`) | `botnexus-extension.json` |
| Install | `git clone` → validate → promote to `~/.botnexus/plugins/<name>/` | built from `src/extensions/**`, deployed to `~/.botnexus/extensions/<id>/` |
| Distributes via marketplace? | **Yes** | **No** |
| Can ship code/UI? | **No** | **Yes** (`IEndpointContributor`, `IAgentTool`, …) |
| Discovered content | skills only (`PluginSkillRootResolver`) | all contracts in the loader's discovery list |

The gap this spec closes: let a plugin carry an extension, deploy it through the existing
extension loader, and give a UI extension the integration points it needs without editing
gateway or portal source.

## 3. The consumer's concrete needs

Agent Builder is the reference consumer. Shipping it required **three** things, only one of
which is "carry a DLL". The other two are currently edits to the portal's own source:

1. **Carry and load the extension** — the `IEndpointContributor` assembly plus its `wwwroot/`
   SPA build.
2. **Claim a served path** — an addition to the portal's passthrough allowlist, because the
   portal's catch-all middleware serves the Blazor app for any path not explicitly excluded.
3. **Contribute a nav entry** — an edit to `MainLayout.razor`, because the sidebar is a
   hardcoded key list; the NavOrder API only *reorders* known keys, it cannot *add* one.

A plugin cannot patch another extension's source, so a code-carrying format that only does (1)
leaves (2) and (3) as manual portal patches. §5 and §6 generalise them into gateway features.

## 4. Carrying code

### 4.1 Plugin manifest — the `extension` field

Plugins are copied verbatim: `GitPluginSourceFetcher` clones and `PluginLifecycleManager.Promote`
copies everything except `.git/`. There is no build step at install and no guaranteed SDK on the
host, so **a carried extension must be prebuilt and committed to the plugin repo**.

One optional object is added to `plugin-manifest.schema.json`. It must be added explicitly —
the schema is `additionalProperties: false`:

```jsonc
"extension": {
  "manifest": "botnexus-extension.json"   // repo-relative path to the extension manifest
}
```

**[correction] `abi` moves off the plugin manifest.** The draft put an `abi` range here. The
contract that actually breaks is `BotNexus.Gateway.Abstractions`, and source-built extensions
are exposed to exactly the same mismatch — so the version gate belongs on `ExtensionManifest`,
which today has **no compatibility field of any kind** (`Id`, `Name`, `Version`,
`EntryAssembly`, `ExtensionTypes`, `Dependencies`, `Enabled`, `ConfigSchema`). Putting it there
covers both distribution paths with one gate. See §7.

The referenced `botnexus-extension.json` is the existing extension manifest, unchanged in
shape. The plugin repo ships the entry assembly, its private dependencies, and any static
assets.

Recommended plugin repo layout:

```
.botnexus-plugin/plugin.json      # name/description/keywords + the "extension" object
botnexus-extension.json           # the extension manifest
lib/                              # prebuilt entry DLL (+ private deps; NOT shared contracts)
wwwroot/                          # built static assets, if the extension serves UI
skills/                           # optional; skills still work alongside
```

> Trim shared and contract assemblies (`BotNexus.Gateway.Abstractions`, `BotNexus.Domain`, …)
> out of `lib/` — the ALC resolves those from the host. Shipping mismatched copies is how a
> binary plugin breaks on a slightly different gateway.

### 4.2 Install, deploy, lifecycle

Reuse the existing extension loader; do not write a second one.

- **Deploy step:** on promote, after copying the plugin into `~/.botnexus/plugins/<name>/`,
  copy the carried extension subtree into `~/.botnexus/extensions/<id>/`, mirroring
  `ServeCommand`'s deploy, which already copies a tree recursively and prunes stale files.
  `AssemblyLoadContextExtensionLoader` then picks it up unchanged.
- **Provenance:** record `plugin <name> → extension <id>` and the exact files written, so
  uninstall removes the deployed extension too, not just the plugin directory.
- **Activation requires a restart.** `MapExtensionEndpoints` is called once during startup;
  there is no hot-map path. Installing or removing a code plugin needs a gateway restart to
  take effect. Skills stay hot.

#### [correction] Update and uninstall cannot overwrite files the gateway has loaded

The draft assumed the deploy step could simply overwrite. It cannot, when the deploying process
is the gateway itself — which it is, since install is an API call. The running gateway has the
extension's assemblies loaded, and overwriting them in place fails with
`IOException: ... because it is being used by another process`. This exact failure has been hit
twice on the reference host during ordinary redeploys. "The ALC is collectible" is true but does
not help: the loader holds its contexts for process lifetime.

The distinction that matters:

| Operation | Target directory | Safe from inside the running gateway? |
|---|---|---|
| Install a **new** extension id | does not exist yet | **Yes** — nothing is loaded from it |
| Update an installed code plugin | holds loaded assemblies | **No** |
| Uninstall a code plugin | holds loaded assemblies | **No** |

**Required design:** update and uninstall stage their result rather than applying it.

- Write the new content to `~/.botnexus/extensions/<id>.pending/` and record the intent.
- On next startup, before discovery, the loader swaps `<id>.pending` into `<id>` (and for
  uninstall, deletes `<id>`) while nothing is loaded.
- The API reports "staged; restart to apply" rather than claiming success.

Install of a new id may apply immediately, because nothing is mapped from a directory that did
not exist. It still needs a restart to *activate*, but the copy itself cannot conflict.

### 4.3 Fingerprinted assets

Blazor and Vite fingerprint their output, so filenames change every build and old generations
accumulate. The deploy must prune files not in the new set, using the recorded file list rather
than a directory scan — the same rule the plugin remover already follows.

## 5. Path claiming

**Problem (verified):** `SignalREndpointContributor` registers an `app.Use` catch-all that
serves the Blazor portal for any path not in a hardcoded passthrough list — `/api/`, `/hub/`,
`/swagger`, `/health`, `/mobile`. Contributors run in `GetServices<IEndpointContributor>()`
order, which is registration order, which is extension load order, derived from a topological
sort whose tie-break is filesystem directory order — nondeterministic. A UI extension's route
may or may not win.

### [correction] Ordering alone does not fix this

The draft proposed giving `IEndpointContributor` an explicit order and making the portal the
last-resort. That is necessary but **not sufficient**, and it would have passed its own
acceptance test while leaving a hole:

- Ordering only governs contributors that register `app.Use` **middleware**. Agent Builder does,
  so the fix would appear to work.
- Contributors that register **endpoints** (`MapGet`, route groups) — as `Plugins.Api` does —
  are not ordered relative to that middleware at all. `WebApplication` inserts routing at the
  head of the pipeline, so by the time the portal's catch-all runs, `context.GetEndpoint()` is
  already populated for those routes. The catch-all simply never looks. That is precisely what
  the hardcoded `/api/` entry is compensating for.

**Required fix, both parts:**

1. **Endpoint-aware fallback.** The portal's catch-all passes through when
   `context.GetEndpoint() is not null`. This covers every endpoint-routed extension
   deterministically, independent of load order, and lets the hardcoded allowlist shrink to
   the genuinely non-endpoint cases.
2. **Explicit contributor ordering.** An ordering signal on `IEndpointContributor` (an
   `int Order` defaulting to 0, or a fallback marker interface), with the portal declaring
   itself last. This covers middleware-style contributors, which routing cannot help.

Together these remove the per-path patch for all future UI extensions. No registry and no new
manifest surface is needed.

## 6. Nav contribution

**Problem (verified):** the portal sidebar is `NavOrderKeys.DefaultOrder`, a hardcoded static
list in `MainLayout.razor`. `OrderedNavKeys()` merges server-provided *order* over that list,
and `NavOrderController` persists ordering of known keys. There is no way to add a key without
editing the Blazor client.

**Fix — contributed nav items rendered at runtime:**

```jsonc
"nav": [
  { "id": "agent-builder", "label": "Agent Builder", "path": "/agent-builder",
    "icon": "tools", "order": 65, "external": true }
]
```

### [correction] `nav` lives on the extension manifest, not the plugin manifest

The draft's §4.1 proposed only an `extension` field, but its worked example put a top-level
`nav` array on `plugin.json`. Under `additionalProperties: false` that example would fail
validation — it is a second schema change the draft never proposed.

It also belongs elsewhere. Nav is a property of the thing that serves the path, so it goes on
`ExtensionManifest` alongside `ExtensionTypes` and `ConfigSchema`. Extensions built from source
then get declarative nav too, instead of the capability existing only for marketplace-carried
ones.

- Expose contributed entries via `GET /api/nav/contributions` (or an extension of the NavOrder
  surface). The Blazor client fetches them on load and merges into `OrderedNavKeys()`.
- An entry with `external: true` — a path served by an extension rather than a Blazor `@page` —
  must render as a forceLoad navigation. This is what the hand-written Agent Builder nav
  fragment already does; productise it. Internal Blazor routes keep using `<NavLink>`.
- **Icon model: a fixed enum**, constrained to the portal's existing icon set. Inline SVG or
  data URIs from a marketplace plugin would inject third-party markup into the portal DOM.

## 7. ABI and version compatibility

A prebuilt extension assembly is bound to the `BotNexus.Gateway.Abstractions` it compiled
against. There is no compatibility field today, so a mismatched binary fails at startup, or
worse loads against a subtly different contract.

- Declare a supported range on **`ExtensionManifest`** (see the §4.1 correction), expressed
  against the **Abstractions assembly version** rather than a gateway product version — that is
  the contract that actually breaks.
- The loader checks it **before** loading the assembly and refuses with a clear message —
  "built for Abstractions ^1.4.0; this gateway provides 1.6.0" — instead of crashing host
  startup. This sits alongside the existing `ValidateDependencies` and the prune pass.
- Surface compatibility on the marketplace listing so an operator sees it before installing.

## 8. Security and trust

A skill is a prompt. A code plugin **runs a .NET assembly in the gateway process** — collectible
ALC, but full trust, no sandbox. Code plugins must be treated differently from skills-only ones.

### [correction] Carried extensions must not land ahead of authentication

The draft covered consent and disclosure but not pipeline placement. `Program.cs` calls
`MapExtensionEndpoints(app)` **before** `UseCors`, `GatewayAuthMiddleware`,
`RateLimitingMiddleware` and `MapControllers`. Every contributor's middleware therefore sits at
the front of the pipeline, ahead of authentication.

That is benign while every contributor is first-party. The moment a marketplace plugin can
register middleware there, third-party code serves paths that bypass `GatewayAuthMiddleware` by
construction — and with it the `GatewayDevOriginEnforcement` DNS-rebind guard, which lives in
the auth handler.

**Required:** a carried extension maps **after** authentication by default. Pre-auth placement
is opt-in, declared in the manifest, and must be named explicitly in the install consent step.

Also required:

- **Explicit consent.** DELIVERED ahead of the rest of item 4. Installing a code plugin requires an
  operator confirmation distinct from skills — "this plugin runs code in your gateway" — refused
  under its own `extension.consent` field so a caller can tell it from a broken plugin without
  parsing prose. Skills-only installs stay low-friction. The ABI guard and post-auth placement
  remain outstanding.
- **Capability disclosure.** The install UI shows what the extension contributes —
  `extensionTypes`, served paths, nav entries, tools — derived from the manifest, before the
  operator commits.
- **Provenance and pinning.** The install commit SHA is already recorded; for code, prefer a
  pinned tag or commit.
- **Isolation reality.** Document that an `IAgentTool` running bash and a UI
  `IEndpointContributor` are both in-process and full-trust. There is no per-extension sandbox.

## 9. Build order

Items 1-3 are built, deployed and verified end to end: Agent Builder now installs as a plugin,
declares its own nav entry, claims its own path, and both of its former portal patches are deleted.
Item 4 is the gate on opening this to third-party plugins.

1. [done] **Carry and deploy a prebuilt extension** from a plugin — schema `extension` field, deploy
   step, provenance, staged update/uninstall (§4.2), restart to activate. Makes code shippable
   at all.
2. [done] **Endpoint-aware fallback + contributor ordering** (§5). Removes the passthrough patch.
3. [done] **Nav contribution API** (§6). Removes the `MainLayout` patch.
4. [todo] **ABI guard (§7) and code-plugin consent + post-auth placement (§8).** Required before this
   is opened to third-party plugins.

Agent Builder can ship as a marketplace plugin once 1–3 exist. Item 4 gates opening the
marketplace to anyone else's code. Items 2 and 3 have a concrete acceptance test: land them,
delete the two portal patches, and confirm Agent Builder still serves and still appears in nav.

### Upstream divergence

Both schema files are upstream `sytone` files, and ten fork PRs are in flight with collaborator
access newly granted. Keep the change purely additive — one optional `extension` object — and
propose it upstream early. Jon built the marketplace scaffolding and will have a view.

## 10. Source files (verify, don't trust)

Plugin format and lifecycle:
- `src/extensions/BotNexus.Extensions.Plugins/Schemas/plugin-manifest.schema.json`
- `src/extensions/BotNexus.Extensions.Plugins/PluginManifestParser.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/GitPluginSourceFetcher.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginLifecycleManager.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginSkillRootResolver.cs`

Extension loading and deploy:
- `src/gateway/BotNexus.Gateway.Contracts/ExtensionModels.cs` (no compat field today)
- `src/gateway/BotNexus.Gateway/Extensions/AssemblyLoadContextExtensionLoader.cs`
- `src/gateway/BotNexus.Gateway/Extensions/ServiceCollectionExtensions.cs`
- `src/gateway/BotNexus.Cli/Commands/ServeCommand.cs`
- `src/gateway/BotNexus.Gateway.Api/Program.cs` (`MapExtensionEndpoints` at line 627, ahead of
  `UseCors` at 630 and `GatewayAuthMiddleware`)

Portal integration points:
- `src/extensions/BotNexus.Extensions.Channels.SignalR/SignalREndpointContributor.cs`
  (catch-all at line 47, passthrough list at 66-70)
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Layout/MainLayout.razor`
  (`NavOrderKeys.DefaultOrder` at line 256)
- `src/gateway/BotNexus.Gateway.Api/Controllers/NavOrderController.cs`
