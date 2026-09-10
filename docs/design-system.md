# BotNexus Design System

**Status:** Foundation implemented · **Applies to:** the Blazor WebAssembly portal
(`BotNexus.Extensions.Channels.SignalR.BlazorClient`)

---

## Purpose

The concrete implementation spec for the portal's look and feel. Every token
lives in one block at the top of `wwwroot/css/app.css`; this document explains
what each is *for* and, more usefully, which decisions are already made so they
don't get re-litigated per component.

The goal is not more decoration. It is fewer, more deliberate decisions applied
consistently — which is what actually reads as considered in mature software.

---

## Why this exists

The portal worked but read as a prototype:

| | Before |
|---|---|
| Colour | 104 distinct hardcoded hex values |
| Undeclared tokens | **34 names, 74 references**, each silently resolving to a hex fallback baked into the `var()` call |
| Corner radius | 15 distinct values across 62 hardcoded declarations |
| Elevation | 7 ad-hoc shadows, including two on flat content |
| Typography | no defined scale |
| Themes | dark only |

The undeclared-token problem was the most consequential and the least visible.
A declaration like `var(--surface, #1e1e1e)` looks tokenised but is not: the
name resolves to nothing, the hardcoded fallback wins, and the value can never
respond to a theme. Seventy-four places behaved this way. Any attempt to add a
light theme would have rendered them all in dark-theme colours.

---

## Research basis

Drawn from **Apple's Human Interface Guidelines** and **Microsoft's Fluent 2**,
taking what the two agree on rather than blending their visual languages:

- **Deference (HIG).** Chrome recedes so content leads. Depth comes from the
  surface ladder, not from decorating every card with a shadow.
- **Hit targets.** HIG asks 44px for touch; Fluent's desktop pointer minimum is
  32px. Both are tokenised. The portal's compact density was **28px** — under
  both.
- **Elevation ramp (Fluent).** A small set of named tiers, rather than one
  shadow or a bespoke shadow per component.
- **Materials (both).** Floating chrome is translucent and blurred so the layer
  beneath reads as context — Fluent's acrylic, HIG's vibrancy.
- **Motion (Fluent).** Named durations, and *different easing for entering vs.
  exiting*. Motion explains a change; it is never decorative.
- **Focus (both, emphatically).** One visible keyboard focus indicator, defined
  once and applied globally rather than left to each control.

---

## Tokens

### Colour

A four-step surface ladder on a near-neutral slate — each step a lightness
increment, never a different hue.

```
--color-canvas      page background
--color-surface     cards, panels
--color-surface-2   nested / hovered surfaces
--color-hairline    1px borders

--color-ink / -muted / -faint      text roles
```

The previous palette used a **saturated blue** (`#0f3460`) as its surface. That
is the single biggest reason the shell read as dated: surfaces competed with the
accent instead of receding behind content.

**One accent.** `--color-accent` is BotNexus's cyan, used *only* for primary
actions, focus rings and the active nav item. Never a section heading, never
decoration. ProjectOS's violet was deliberately not adopted — the rule is the
framework, the hex is that product's identity.

`--color-on-accent` exists because text on a filled accent surface must not use
`--color-ink`: ink flips between themes and won't reliably contrast against a
mid-tone fill.

**Status: three tokens each, not one.** `--color-success` / `-bg` / `-text`.
Dot indicators use the solid value; pills use the bg/text pair, so they stay
legible on any surface instead of depending on opacity tinting.

### Category palette

`--cat-live`, `--cat-pinned`, `--cat-agent`, `--cat-subagent`, `--cat-a2a`,
`--cat-webhook`, `--cat-blue`, `--cat-purple`, `--cat-muted`.

These identify a *kind* of thing, not a status and not an action. They are
deliberately separate from the accent: the accent means "act here", and reusing
it for classification dilutes that. Kept desaturated so a screen showing several
at once stays calm. This is the hook for per-plugin colour-coding later.

### Typography

Seven roles. Reference these, never a raw font-size.

| Role | Size / Weight | Use |
|---|---|---|
| `t-display` | 28px / 600 | hero numbers (rare) |
| `t-title` | 20px / 600 | page titles |
| `t-heading` | 16px / 600 | section and card headings |
| `t-body` | 14px / 400 | default |
| `t-label` | 13px / 500 | form labels, small UI text |
| `t-caption` | 12px / 400 | metadata, timestamps |
| `t-mono` | 13px / 400 | IDs, hashes, ports |

Line-heights sit on a 4px grid. Sizes are in `rem` so an OS text-size preference
still scales them — the practical web equivalent of HIG's Dynamic Type.

**The scale is applied in CSS, not by adding `.t-*` classes to markup.** The
portal had 250 raw `font-size` declarations across **37 distinct values**;
adding classes to 14,830 lines of `.razor` would have left all 250 in place to
fight them on specificity — two competing systems instead of one — for no gain,
since every component already carries a class. Mapping the declarations onto the
role tokens collapsed 37 values to 7 in a single reviewable diff.

Rules that also set a monospace family take `--text-mono` rather than the
size-matched role, so intent survives even though mono and label are both 13px.

Seven `em`-based sizes are left alone: they are relative to their parent by
design, and pinning them to a fixed rem would break the nesting they exist for.

`font-size` only. The roles pair a size with a leading, but line-height is still
per-component: this portal's chrome is dense and density-tuned, and changing both
dimensions at once would make any resulting layout break impossible to attribute.
Leading is a follow-up.

### Typeface

**Inter**, self-hosted, weights 400–700 from a single variable file per subset.

The portal previously used the platform stack (San Francisco on macOS, Segoe UI
on Windows). That is what HIG and Fluent each prescribe for their own platform,
and it costs nothing — but it also means the product looks materially different
depending on where it is opened, and different again from its sibling projects.
Inter renders identically everywhere, which was judged the more valuable
property here.

Self-hosted rather than loaded from the Google Fonts CDN: the portal is reachable
on a LAN and cached offline by the service worker, so text must not depend on an
internet round-trip to render.

Three details that matter:

- **`font-display: swap`.** Text paints immediately in the fallback and reflows
  when Inter arrives. The alternative (`block`) hides text for up to three
  seconds, which is a worse failure than a brief metric shift.
- **`unicode-range` subsetting.** latin (48 KB) covers the common path;
  latin-ext (85 KB) is fetched only if a page actually renders a glyph from it.
- **The latin subset is preloaded** in `index.html`. It is needed for the first
  paint, and discovering it only after `app.css` parses would guarantee a
  visible swap. latin-ext is deliberately *not* preloaded.

The platform stack remains behind Inter in `--font-sans`, so text stays legible
during the swap and if the woff2 fails entirely. `--font-mono` is unchanged.

Inter is licensed under the SIL Open Font License 1.1.

### Shape

Two radii. `--radius-sm` **6px** for buttons, inputs, badges, small controls;
`--radius-lg` **12px** for cards, dialogs, panels. `--radius-pill` is a shape,
not a third size.

HIG's continuous "squircle" curvature has no portable CSS equivalent yet;
plain `border-radius` is the honest approximation.

### Icons

`--icon-sm` **16px** for glyphs that sit beside a label and read as a row marker ·
`--icon-md` **18px** for icon-only action controls.

Deliberately separate from the type roles: those describe running text, and an
emoji rendered at body size is not legible *as a symbol*. An icon is recognised
by shape at a glance, which needs more size than a letterform does to be read
inside a word.

Size and hit target move together. A bigger glyph in a 20px box is no easier to
hit, and a bigger box around a 12px glyph is no easier to read — the portal had
both problems at once, with 12–14px glyphs in boxes as small as 20×19.

**Every icon-only control carries a `title`.** Where the control also has a
visible label, the tooltip says what the destination is *for* rather than
repeating the word already on screen: a tooltip that restates its own label is
noise.

### Hit targets

`--hit-pointer` **32px** (Fluent desktop minimum) · `--hit-touch` **44px** (HIG).
Both exist so a control picks the one matching its input modality rather than
splitting the difference and satisfying neither. Compact density now uses the
former, comfortable the latter.

### Elevation

Two tiers, and **flat content gets neither**:

- `--shadow-raised` — popovers, dropdowns, menus, tooltips
- `--shadow-overlay` — dialogs, command palette, toasts

Cards, tables and lists carry **no shadow** and are separated by the surface
ladder alone. Adding shadows to every card is the fastest way to make a redesign
look busier rather than more considered.

### Material

`.material-raised` / `.material-overlay` apply an acrylic-style translucent
blurred background. Confined to floating chrome, because blur is expensive.
Degrades to `--material-fallback` where `backdrop-filter` is unsupported and
under `prefers-reduced-transparency`.

### Motion

`--motion-fast` 100ms (hover/press) · `--motion-base` 160ms (open/close) ·
`--motion-slow` 240ms (drawers).

Entering decelerates (`--ease-enter`), exiting accelerates (`--ease-exit`).
Anything past ~200ms on UI chrome reads as sluggish, not smooth.
`prefers-reduced-motion` is honoured globally.

### Contrast

**Both themes are verified against WCAG AA (4.5:1 for body text, 3:1 for large).**
This is checked by measurement, not by eye — picking values by eye is exactly how
the first cut of the light theme shipped five failures.

Four token corrections came out of that audit:

| Token | Was | Now | Why |
|---|---|---|---|
| `--color-ink-faint` (dark) | `#6b7885` | `#818c97` | failed on all three dark surfaces (3.51–4.19) |
| `--color-ink-faint` (light) | `#6e7c8a` | `#626e7b` | failed on all three light surfaces (3.80–4.27) |
| `--color-accent` (light) | `#0b7f99` | `#0a7790` | failed as text on canvas (4.37) and surface-2 (4.14) |
| `--color-danger-fill` | *(new)* | `#ce433d` dark | white on `#f85149` was only 3.35:1 |

`--color-danger-fill` exists because a **solid danger button** carrying
`--color-on-solid` needs a darker red than the same colour used as an 8px dot or
a 1px border, where text contrast does not apply. `--color-danger` stays bright
for those.

When auditing, composite translucent backgrounds over what sits behind them. A
naive walk that returns `rgba(…, 0.05)` as the background compares a colour
against a 5% tint of itself and reports 1:1 — a false failure that will send you
chasing a bug that is not there.

### Focus

`:focus-visible` (not `:focus`) draws a 2px accent ring with 2px offset, so a
pointer click leaves nothing behind while keyboard traversal always shows one.

---

## Icon library

44 inline SVG icons. The source of truth is `assets/icons/svg/`; the Blazor library is
**generated**, never hand-edited:

```bash
python3 scripts/generate-icons.py
```

That writes `IconLibrary.g.cs` and the per-icon tone rules in `app.css`. To change an
icon, change its SVG and re-run.

Drawing conventions: 24 x 24 viewBox, `fill="none"`, 2px strokes, round caps and
joins, transparent background, legible at 16, 20, 24 and 32px.

### Usage

```razor
<Icon Name="home" />                                     @* 20px default *@
<Icon Name="delete" Size="16" Class="bn-icon-inherit" /> @* takes its control colour *@
<Icon Name="agents" Class="bn-icon-flat" />              @* gradient collapsed to flat *@
<Icon Name="refresh" Class="bn-icon-spin" />             @* in progress *@
<Icon Name="warning" Title="Config invalid" />           @* announced; default aria-hidden *@
```

### The palette

Colour here is restrained and semantic, in the same spirit as the category palette:
blue for communication and actions, green for activity and completion, amber for
tools, scheduling and temporary state, red for destructive and blocking, and gradients
for AI, extension and creative identities.

| Hue | Icons |
|---|---|
| Green `#22C55E` | `activity` `check` `todo` `usage` |
| Blue `#3B82F6` | `add` `agents` `chat` `conversation` `guide` `help` `send` |
| Amber `#F59E0B` | `cron-jobs` `folder` `light-mode` `pause` `pin` `tools` `warning` |
| Red `#EF4444` | `avoid` `delete` `error` `stop` |
| Violet `#8B5CF6` | `assistant` `canvas` `skills` `thinking` |
| Teal `#14B8A6` | `plugins` `reports` |
| Indigo `#6366F1` | `dark-mode` `workspace` |
| Cyan `#06B6D4` | `bot` |

Seven carry a gradient rather than a flat tone - `agents` `assistant` `bot` `canvas`
`plugins` `skills` `usage` - and the hue above is their first stop, used when the icon
is forced flat.

Thirteen carry no tone at all and inherit their context: `attach` `back` `close`
`configuration` `copy` `edit` `file` `home` `move` `refresh` `running` `search`
`visibility`.

### Colour policy

No icon hardcodes its stroke. Every root carries `stroke="currentColor"` (or a
gradient reference) and the artwork tone lives in a generated `.bn-icon-<name>` rule.
Rendering is identical; the difference is that any context can override it - a
disabled control, a selected row, a button whose label sets the colour.

| Class | Effect |
|---|---|
| `bn-icon-inherit` | Take the colour of the surrounding control |
| `bn-icon-flat` | Drop a gradient for the icon's flat tone |
| `bn-icon-spin` | Rotate; respects `prefers-reduced-motion` |

These are emitted **after** the per-icon tones on purpose. Both are single-class
selectors, so source order alone decides which wins - written above the tones they
silently lose, and an archive icon stays red instead of inheriting its button.

### SVG ids are document-global

The set originally declared every gradient as `id="g"`. Once two of those icons
rendered together, every `url(#g)` resolved to whichever landed in the DOM first and
the icons silently took each other's colours - three of them share the sidebar.

The generator now makes each id unique per icon and **refuses to emit a set where two
icons declare the same one**. `Icon.razor` additionally suffixes each id per instance,
so the same icon rendered three times does not produce three duplicate ids.

`Icon.razor` emits the whole element - root included - as a single `MarkupString`
rather than an `<svg>` root with a `MarkupString` body. Raw markup injected into an
existing SVG parent is namespace-sensitive and can end up in the HTML namespace, where
it renders as nothing; starting the fragment at `<svg>` leaves the namespace decision
to the parser, which handles it the same way it handles any inline SVG in a document.

---

## Form patterns

Two layouts, chosen by the shape of the content rather than by page.

**Two-column rows** - configuration and the agent editor. A label column, then a
control column carrying the control, its description and its error. Collapses to one
column under 720px, the width the mobile Settings page renders the same component at.

```css
grid-template-columns: minmax(9rem, 12rem) minmax(0, 1fr);
align-items: start;
```

`align-items: start`, not `center`: a centred label floats halfway down a tall control
and against a ten-row checkbox list ends up level with nothing. Single-line controls
cap at `26rem` - letting an input run the full row width pushes its label a long way
from its value.

**Stacked rows** - the cron editor. Labels above controls in a responsive grid, which
suits a form of many short fields. The controls still use the shared input style; only
the arrangement differs.

### The generic renderer

`SchemaForm` draws every configuration screen, desktop and mobile, from the UI-schema
envelope. Style the classes it emits; do not fork it:

`schema-form` `schema-group` `schema-group-title` `schema-object` `schema-subgroup`
`schema-subgroup-title` `schema-field` `schema-field-label` `schema-field-control`
`schema-field-description` `schema-field-error` `schema-array` `schema-dict`

The schema already carries `x-ui-label`, `x-ui-description`, `x-ui-group` and
`x-ui-order`. Render all four. Descriptions and grouping were computed and thrown away
for a long time, which is why those screens read as a wall of bare labels - 31 of the
gateway section's 34 fields carry a description that was never shown.

Every input needs a `for`/`id` pairing. Before this was fixed, not one input on the
configuration page had an accessible name.

---

## Rules

1. No raw hex in a component rule. Add a token instead.
2. Two radii. `--radius-pill` and `50%` are shapes, not extra sizes.
3. Shadows only on floating chrome, only via the two tiers.
4. One accent. Category colours classify; status colours signal; the accent
   invites action.
5. Type roles, never raw font-sizes.
6. Every colour must come from a token, or the light theme will not follow it.
7. Derive spacing from the size token; never restate it as a literal. Three
   conversation-row buttons were pinned at `right: 2.95/1.6/0.25rem` and later given
   the 32px `--hit-pointer` minimum without the offsets being revisited. 32 - 21.6
   leaves each button overlapping its neighbour by 10.4px.
8. No icon hardcodes its stroke. `currentColor` or its own gradient, so a hover,
   disabled or selected state can reach it.

### Enforced by tests

Conventions nothing checks have already drifted, so these fail loudly on the things
that otherwise fail silently: no two icons declaring the same id (with both names in
the message), every `url(#...)` resolving inside its own icon, every stroke being
overridable, the tone overrides being declared after the tones, and every toned icon
having a rule. When you add to the system, add the fence with it.

### Deliberate exceptions

Four non-token values remain, each defensible:

- `50%` on circular avatars and status dots — a shape.
- `3px` on scrollbar thumbs — not a control.
- `3px` on inline `<code>` — 6px on a tight inline span reads bubbly.
- `2px` on the burger-menu lines — a line cap, not a corner.

---

## Themes

Dark lives on `:root`; light is opt-in via `[data-theme="light"]` on `<html>`.

This **inverts the usual convention** (light as default) on purpose. Dark was
the only theme this portal ever had, and an existing user must not be repainted
by upgrading. Only tokens are redefined — no component rule is duplicated, and
no `dark:`-style variant classes exist anywhere.

### Switching

A toggle sits in the top bar (☀ / ☾), and `Settings → Colour theme` offers the
same choice explicitly. Both go through `IPortalPreferencesService.SetThemeAsync`,
which mirrors the existing density preference rather than introducing a second
mechanism — same `PortalTheme.Normalize` guard, same localStorage blob
(`botnexus.portal.prefs`), same `OnChanged` notification.

**Dark is the absence of the attribute, not `data-theme="dark"`.** Dark lives on
`:root`, so expressing it as an attribute would mean the pre-paint script had to
write something on the default path. Light sets the attribute; dark removes it.

A synchronous inline script in `index.html` applies the stored theme **before
first paint**, reading the same localStorage blob. Without it a light-theme user
would stare at a fully dark portal for the seconds the WASM runtime takes to
boot, then watch it flip. The script is deliberately placed before Blazor's own
and guarded by try/catch: unparseable or unavailable storage falls through to
the dark default.

`SetThemeAsync` applies the DOM attribute *before* persisting, so the swap is
instant even where localStorage is unavailable (private browsing, quota) — the
theme still changes for the session rather than appearing to do nothing.

---

## Deploying a CSS change

Two facts make this less obvious than it looks, and getting either wrong
produces the same symptom: the change is live on the server and invisible in
the browser.

**1. The portal is served from the installed extension, not the build tree.**
Rebuilding updates `src/extensions/…SignalR/bin/Release/net10.0/blazor/` but
not `~/.botnexus/extensions/botnexus-signalr/blazor/`.

**2. A service worker sits in front of it.** `service-worker.js` is
network-first for the shell and cache-first for fingerprinted `/_framework/`
assets, and `service-worker-assets.js` carries an integrity hash for every
file it caches — `css/app.css` included.

So **never hand-copy an individual file into the extension directory.** Doing
that leaves the asset manifest holding the hash of the *previous* file and its
`Manifest version` unchanged, which is precisely how the worker decides whether
a new build exists. It concludes nothing changed and keeps serving its cache —
through a hard reload, a fresh tab, `cache: no-store`, and query-string
cache-busting alike, because a service worker intercepts all of them. The
worker's own source comment puts it well: *"The cache was not merely stale, it
was stale FOREVER."*

Rebuild, then deploy the whole output:

```bash
dotnet build src/extensions/BotNexus.Extensions.Channels.SignalR -c Release
rsync -a --delete \
  src/extensions/BotNexus.Extensions.Channels.SignalR/bin/Release/net10.0/blazor/ \
  ~/.botnexus/extensions/botnexus-signalr/blazor/
```

Verify all three moved together — a mismatch between them is the bug:

```bash
curl -s http://<host>:5005/ | grep -o 'app.css[^">]*'                  # versioned href
curl -s http://<host>:5005/service-worker.js | grep -o 'Manifest version: [^ ]*'
curl -s 'http://<host>:5005/css/app.css?v=ds1' | grep -c color-canvas
```

A changed `Manifest version` is the signal that reaches the browser. Expect the
client to need two reloads: one to install the new worker, one for it to take
control. If a client is genuinely wedged, DevTools → Application → Service
Workers → Unregister.

Because `index.html` references the stylesheet at a stable path, the href also
carries a `?v=` token — bump it whenever `app.css` changes materially. Blazor
fingerprints its own `_framework` assets but has no equivalent for
`index.html` in standalone WASM.

---

## Status

**Done**

- Full token layer: colour, category, typography, shape, hit targets,
  elevation, material, motion, focus
- Light theme token set
- All 34 previously-undeclared tokens now declared and theming correctly
- **Every colour in every component rule flows through a token** — 133 literal
  values migrated, 0 remain
- **144 dead `var(--x, #hex)` fallbacks removed.** Now that every token is
  declared, an inline fallback only hides a future typo: it paints a hardcoded
  colour instead of failing visibly. Removing them is what makes the rule "no
  raw hex in a component rule" enforceable rather than aspirational.
- White-on-accent contrast fixed: `#fff` on the cyan accent measured ~2:1,
  under the 4.5:1 minimum. Those 13 call sites now use `--color-on-accent`
  (~8.5:1). White is retained via `--color-on-solid` where the fill is a
  mid-to-dark status or category colour and white is correct.
- Hardcoded radii 62 → 18 (and 15 distinct → 8, of which 4 are deliberate)
- Ad-hoc shadows 7 → 0; two shadows removed from flat content
- Global `:focus-visible`, `prefers-reduced-motion`,
  `prefers-reduced-transparency`
- Light/dark toggle in the top bar and in Settings, persisted per browser and
  applied before first paint
- Inter self-hosted as the UI typeface, subset and preloaded
- Type scale applied: 250 raw font-sizes across 37 values collapsed onto the
  seven roles (7 relative `em` values left by design)
- Both themes verified WCAG AA by measurement; four token corrections applied

**Verified:** `src/dirs.proj` builds with 0 warnings / 0 errors; the gateway
boots with 0 errors and serves the tokenised stylesheet.

**Next**

1. Migrate the 75 remaining `var(--radius)` call sites onto `--radius-sm` /
   `--radius-lg` explicitly, then retire the legacy alias.
3. Line-height: pair each role's leading with its size, once the size change has
   been shaken out in daily use.
4. Retire the legacy colour aliases once no rule references them.
5. Real `Dialog` / `Toast` components using `.material-overlay`.
6. Command palette (`Ctrl/Cmd+K`).
