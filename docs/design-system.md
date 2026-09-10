# BotNexus Design System

**Applies to:** the Blazor WebAssembly portal
(`src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient`)
**Source of truth:** the token block at the top of `wwwroot/css/app.css`
**Audience:** anyone designing or building portal UI — this document is written so
a designer who has never opened the codebase can specify work from it.

---

## How to use this document

Three ways in, depending on what you are doing:

| You are… | Start at |
|---|---|
| Designing a new screen | [Quick reference](#quick-reference), then [Component patterns](#component-patterns) |
| Picking a colour | [Colour](#colour) — and read [the colour policy](#colour-policy) first |
| Adding an icon | [Icon library](#icon-library) |
| Wondering why something is the way it is | [Why this exists](#why-this-exists) and [Research basis](#research-basis) |
| About to break a rule | [Rules](#rules) and [Known gaps](#known-gaps) |

**The one thing to internalise:** every visual value in the portal comes from a
named token. If you find yourself specifying a hex code, a pixel radius or a font
size that is not in this document, either you have found a gap worth adding a
token for, or the design should use an existing token. Both are fine outcomes.
Inventing a one-off value is not.

---

## Quick reference

The entire system, for pinning next to a canvas.

**Surfaces** — four steps, dark first. Depth comes from this ladder, not shadows.

| Token | Dark | Light | Use for |
|---|---|---|---|
| `--color-canvas` | `#0e1116` | `#f6f8fa` | The page behind everything |
| `--color-surface` | `#151a21` | `#ffffff` | Cards, panels, inputs, the top bar |
| `--color-surface-2` | `#1c232c` | `#eef2f6` | A raised or selected step above a surface |
| `--color-hairline` | `#273039` | `#d5dde5` | Every border and divider |

**Ink** — three weights of text, and that is all.

| Token | Dark | Light | Use for |
|---|---|---|---|
| `--color-ink` | `#e6edf3` | `#10161d` | Body text, headings, control values |
| `--color-ink-muted` | `#9aa7b4` | `#4d5a67` | Labels, secondary text, descriptions |
| `--color-ink-faint` | `#818c97` | `#626e7b` | Timestamps, placeholders, disabled |

**Accent** — one saturated colour in the whole product.

| Token | Dark | Light | Use for |
|---|---|---|---|
| `--color-accent` | `#00b4d8` | `#0a7790` | Primary action, active nav, focus ring |
| `--color-accent-hover` | `#2bc9e8` | `#086274` | Its hover state |
| `--color-on-accent` | `#04222a` | `#ffffff` | Text/icons **on** a filled accent |

**Type** — seven roles. Sizes in `rem` so OS text scaling works.

| Role | Size | Line height | Weight | Use for |
|---|---|---|---|---|
| `--text-display` | 1.75rem / 28px | 2.25rem | 600 | One per page at most |
| `--text-title` | 1.25rem / 20px | 1.75rem | 600 | Page and dialog titles |
| `--text-heading` | 1rem / 16px | 1.5rem | 600 | Section headings |
| `--text-body` | 0.875rem / 14px | 1.25rem | 400 | Default for everything |
| `--text-label` | 0.8125rem / 13px | 1.25rem | 500 | Field labels, table headers |
| `--text-caption` | 0.75rem / 12px | 1rem | 400 | Timestamps, hints, metadata |
| `--text-mono` | 0.8125rem / 13px | 1.25rem | 400 | Ids, paths, JSON, code |

**Shape** — two radii and a pill. Nothing else.

| Token | Value | Use for |
|---|---|---|
| `--radius-sm` | `6px` | Buttons, inputs, chips, small controls |
| `--radius-lg` | `12px` | Cards, dialogs, panels |
| `--radius-pill` | `999px` | Status pills, avatars, badges |

**Motion** — three durations, three curves.

| Token | Value | Use for |
|---|---|---|
| `--motion-fast` | `100ms` | Hover and press feedback |
| `--motion-base` | `160ms` | Opening and closing |
| `--motion-slow` | `240ms` | Drawers and large panels |
| `--ease-enter` | `cubic-bezier(0.1, 0.9, 0.2, 1)` | Arriving — decelerates and settles |
| `--ease-exit` | `cubic-bezier(0.7, 0, 0.84, 0)` | Leaving — accelerates away |
| `--ease-standard` | `cubic-bezier(0.33, 0, 0.67, 1)` | Symmetric state changes |

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

The undeclared-token problem was the most consequential and the least visible. A
declaration like `var(--surface, #1e1e1e)` *looks* tokenised but is not: the name
resolves to nothing, the hardcoded fallback wins, and the value can never respond
to a theme. Seventy-four places behaved this way. Any attempt to add a light theme
would have rendered all seventy-four in dark-theme colours.

That failure mode is worth holding onto, because it is the shape of most design
system decay: **something that looks systematic but is not.** A token that is
referenced but never declared, a class that exists but is used twice, a scale
that is documented but not followed. Each looks like a system from the outside.

The goal here is not more decoration. It is fewer, more deliberate decisions,
applied consistently — which is what actually reads as considered.

---

## Research basis

Drawn from **Apple's Human Interface Guidelines** and **Microsoft's Fluent 2**,
taking what the two agree on rather than blending their visual languages:

- **Deference (HIG).** Chrome recedes so content leads. Depth comes from the
  surface ladder, not from decorating every card with a shadow.
- **Hit targets.** HIG asks 44px for touch; Fluent's desktop pointer minimum is
  32px. Both are tokenised. The portal's compact density was **28px** — under both.
- **Elevation ramp (Fluent).** A small set of named tiers, rather than one shadow
  or a bespoke shadow per component.
- **Materials (both).** Floating chrome is translucent and blurred so the layer
  beneath reads as context — Fluent's acrylic, HIG's vibrancy.
- **Motion (Fluent).** Named durations, and *different easing for entering vs
  exiting*. Motion explains a change; it is never decorative.
- **Focus (both, emphatically).** One visible keyboard focus indicator, defined
  once and applied globally rather than left to each control.

---

## Colour

### The surface ladder

Four steps on a near-neutral slate. Each step is a **lightness increment, not a
different hue** — that is what keeps the shell calm.

The pre-redesign palette used a saturated blue (`#0f3460`) as its surface, which
competed with the accent for attention. That, not the accent, is why the shell
read as dated. Surfaces recede; content carries the colour.

```
--color-canvas      the page                    darkest
--color-surface     cards, panels, inputs
--color-surface-2   raised / selected step
--color-hairline    borders and dividers        lightest
```

**Designer rule:** if you need a third visible level of depth on one screen, you
are probably designing a hierarchy that needs solving with spacing or grouping
instead. Two surface steps above the canvas is the working budget.

### Ink

Three weights. `--color-ink-faint` was lightened from `#6b7885` because that
failed WCAG AA on all three dark surfaces (3.51–4.19:1). It now measures ≥4.6:1
against canvas, surface and surface-2. The light theme's equivalent was darkened
from `#6e7c8a` for the same reason.

**Designer rule:** never introduce a fourth ink. If text needs to be quieter than
`--color-ink-faint`, it is probably not needed on that screen.

### The accent

**One saturated colour in the entire product.** `--color-accent` means *act here*:
primary actions, the focus ring, the active nav item. It is never decoration and
never a section heading.

Text sitting **on** a filled accent takes `--color-on-accent`, never
`--color-ink`. This matters: ink flips between themes and will not reliably
contrast against a mid-tone fill. White on the dark theme's cyan measures ~2:1,
well under the 4.5:1 minimum; `--color-on-accent` measures ~8.5:1.

### Status

Three tokens per status, not one — a solid value for dots and borders, plus a
background/foreground pair for pills. Pills use the pair rather than opacity
tinting so they stay legible on any surface.

| Status | Solid | Background | Text |
|---|---|---|---|
| Success | `--color-success` | `--color-success-bg` | `--color-success-text` |
| Warning | `--color-warning` | `--color-warning-bg` | `--color-warning-text` |
| Danger | `--color-danger` | `--color-danger-bg` | `--color-danger-text` |
| Info | `--color-info` | `--color-info-bg` | `--color-info-text` |

`--color-danger-fill` exists separately: a **solid** danger button carrying white
text needs a darker red than the dot/border value, because white on `#f85149` is
only 3.35:1. Use `--color-danger` for a 1px border or an 8px dot, where text
contrast does not apply; use `--color-danger-fill` when it is a filled button.

### Category palette

Distinct hues that identify a **kind of thing** — a live run, a pinned item, a
webhook — rather than signalling status or inviting an action.

| Token | Dark | Light | Identifies |
|---|---|---|---|
| `--cat-live` | `#2ea36a` | `#1a7f52` | A run happening now |
| `--cat-pinned` | `#a3823c` | `#8a6a24` | A pinned conversation |
| `--cat-agent` | `#6c6c80` | `#5b6472` | An agent-originated item |
| `--cat-subagent` | `#4f8570` | `#3d6b58` | A sub-agent |
| `--cat-a2a` | `#96714a` | `#7a5a34` | Agent-to-agent traffic |
| `--cat-webhook` | `#8272a8` | `#6a5a90` | A webhook trigger |
| `--cat-blue` / `-bg` | `#60a5fa` / `#16304f` | `#1f6feb` / `#ddeaff` | General classification |
| `--cat-purple` / `-bg` | `#a78bfa` / `#281a45` | `#7c4ddc` / `#ede4ff` | General classification |
| `--cat-muted` | `#5b6b80` | `#64748b` | Unclassified / inactive |

They are **deliberately not the accent**: the accent means "act here", and reusing
it for classification would dilute that. They are kept desaturated so a screen
showing several at once stays calm.

### Washes and scrims

| Token | Use for |
|---|---|
| `--color-wash` | Neutral hover / selected — must not read as status |
| `--color-wash-soft` | A barely-there row tint |
| `--color-accent-wash` / `-soft` / `-strong` | A row or panel that carries accent meaning |
| `--color-success-wash` etc. | A row carrying a status meaning |
| `--color-scrim` | Behind a modal, dimming the page |
| `--color-scrim-soft` | A lighter dim, or a sunken code block |

Neutral washes are translucent white on dark and translucent black on light, so
they compose correctly over any surface step.

### Two colours that never theme

| Token | Value | Why |
|---|---|---|
| `--color-on-solid` | `#ffffff` | Text on a saturated status or category fill. Those fills are mid-to-dark in both themes, so white is correct in both. |
| `--color-embed-surface` | `#ffffff` | Iframe canvases hosting third-party HTML. The content assumes a white page; darkening the frame would leave black-on-black text. |

### Colour policy

1. **No raw hex in a component rule.** Every colour comes from a token. The one
   sanctioned exception is the generated icon tone block — see [Icons](#icon-library).
2. **One accent.** If you want a second saturated colour to distinguish something,
   you want the category palette.
3. **Status colour is for status.** Green means succeeded, not "primary".
4. **Text on a fill uses the matching `on-` token.** `--color-on-accent` for the
   accent, `--color-on-solid` for status and category fills.
5. **No inline `var()` fallbacks.** `var(--x, #hex)` paints a hardcoded colour
   instead of failing visibly, which is how 74 undeclared tokens hid for months.
   If a token might not exist, that is a bug to fix, not to paper over.

---

## Typography

### The scale

Seven roles. Components reference these; nothing references a raw font size.
Line heights sit on a 4px grid so vertical rhythm survives mixing roles.

Sizes are in `rem` so an OS text-size preference scales them — the practical web
equivalent of HIG's Dynamic Type. **Never specify type in `px`.**

| Role | Size | Line height | Default weight |
|---|---|---|---|
| `--text-display` | 28px | 36px | `--weight-semibold` (600) |
| `--text-title` | 20px | 28px | 600 |
| `--text-heading` | 16px | 24px | 600 |
| `--text-body` | 14px | 20px | `--weight-regular` (400) |
| `--text-label` | 13px | 20px | `--weight-medium` (500) |
| `--text-caption` | 12px | 16px | 400 |
| `--text-mono` | 13px | 20px | 400, `--font-mono` |

Weight is declared inside each role, so a role can be reused at a different weight
without redefining its size. Three weights exist: 400, 500, 600. There is no bold
(700) in the UI.

### Choosing a role

| If the text is… | Use |
|---|---|
| The name of the page | `--text-title` |
| The name of a section within a page | `--text-heading` |
| Anything a user reads as content | `--text-body` |
| Naming a control or a column | `--text-label` |
| Metadata *about* content — when, how many, by whom | `--text-caption` |
| An id, path, model name, or anything compared character by character | `--text-mono` |
| The single most important thing on a dashboard | `--text-display`, at most once |

### Applying it in markup

Role classes exist and can be used directly:

```html
<h2 class="t-title">Agents</h2>
<span class="t-caption">6 min ago</span>
<code class="t-mono">claude-haiku-4-5</code>
```

`.t-display` `.t-title` `.t-heading` `.t-body` `.t-label` `.t-caption` `.t-mono`

Most components instead set `font-size: var(--text-body)` in their own CSS rule.
Both are correct and resolve to the same token. Prefer the CSS token when the
element already has a component class; use the utility class for one-off text
that has no rule of its own.

### Typeface

**Inter**, self-hosted rather than loaded from a CDN — the portal is reachable on
a LAN and offline via the service worker, so text must not depend on an internet
round-trip.

One variable file per subset covers weights 400–700, so all three weights cost a
single download. A `unicode-range` split means the 85 KB latin-ext file is fetched
only if a page actually renders a glyph from it; the common path is the 48 KB
latin file alone. `font-display: swap` — text paints immediately in the fallback
and reflows when Inter arrives.

```
--font-sans   'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif
--font-mono   'Cascadia Code', 'Fira Code', 'Consolas', monospace
```

Inter is licensed under the SIL Open Font License 1.1.

### The one exception

Rendered markdown (`.msg-content` — chat messages and guide pages) sizes its
headings and inline code in `em`, relative to the container. That is deliberate:
markdown appears inside a chat bubble and inside a full-width guide page, and it
should scale with whichever it is in. These are the only font sizes in the portal
that are not a named role.

---

## Shape

Two radii and a pill. HIG's continuous "squircle" curvature has no portable CSS
equivalent; plain `border-radius` is the honest approximation.

| Token | Value | Applies to |
|---|---|---|
| `--radius-sm` | 6px | Buttons, inputs, selects, chips, tags, small controls |
| `--radius-lg` | 12px | Cards, dialogs, panels, sheets |
| `--radius-pill` | 999px | Status pills, badges, avatars, toggle knobs |

`--radius` is a legacy alias resolving to `--radius-sm`. New work should name the
size explicitly.

**Deliberate exceptions:** chat bubbles carry a 3px "tail" corner on the side
nearest their author. That is a shape decision, not a container radius.

---

## Hit targets and icon sizing

| Token | Value | From |
|---|---|---|
| `--hit-touch` | 44px | HIG's touch minimum |
| `--hit-pointer` | 32px | Fluent's desktop pointer minimum |

Both are provided so a control picks the one matching its input modality rather
than splitting the difference and satisfying neither.

Glyph sizes are separate from the type roles on purpose: those describe running
text, and an icon has to be recognised by **shape** at a glance, which needs more
size than a letterform does to be read inside a word.

| Token | Value | Intended for |
|---|---|---|
| `--icon-sm` | 1rem / 16px | Secondary glyphs inside dense rows |
| `--icon-md` | 1.125rem / 18px | Primary action glyphs |

> **Known gap.** `<Icon Size="…">` takes a raw pixel number, and call sites
> currently pass **12, 13, 14, 15, 16, 18 and 24** — seven values against two
> tokens. See [Known gaps](#known-gaps).

---

## Elevation

Flat content — cards, tables, lists — carries **no shadow**. It is separated by
the surface ladder alone. Only floating chrome may use these, and only the tier
matching how far it sits from the page.

| Token | Value (dark) | For |
|---|---|---|
| `--shadow-raised` | `0 4px 12px rgba(0,0,0,0.32)` | Popovers, dropdowns, menus, tooltips |
| `--shadow-overlay` | `0 16px 40px rgba(0,0,0,0.48)` | Dialogs, command palette, toasts |

Two tiers, not Fluent's full ramp: the portal has exactly two classes of floating
thing, and unused tiers invite arbitrary picking.

**If you are reaching for a shadow on a card, the answer is a surface step.**

---

## Material

Floating chrome is translucent and blurred so the layer beneath reads as context
rather than being replaced — Fluent's acrylic, HIG's vibrancy.

```
--material-tint      rgba(21, 26, 33, 0.82)    the translucent fill
--material-blur      20px                       backdrop blur radius
--material-fallback  #171d25                    opaque stand-in
```

Applied via `.material-raised` and `.material-overlay`, never ad hoc — blur is
expensive and must stay confined to chrome.

The fallback is used automatically in two cases: browsers without
`backdrop-filter`, and readers who have asked for
`prefers-reduced-transparency: reduce`. **Translucency is an aesthetic;
legibility is not.**

---

## Motion

Fluent's insight worth keeping: entering and exiting are not the same gesture.
Entering decelerates — it arrives and settles. Exiting accelerates — it leaves
decisively.

| Token | Value | Use |
|---|---|---|
| `--motion-fast` | 100ms | Hover, press, colour and opacity feedback |
| `--motion-base` | 160ms | Open/close, transform, size change |
| `--motion-slow` | 240ms | Drawers and large panels |

Durations stay short: anything past ~200ms on UI chrome reads as sluggish rather
than smooth.

`prefers-reduced-motion: reduce` collapses every animation and transition to
0.01ms globally. **Anything conveyed by animation alone is a bug** — motion may
explain a change, never carry it. If a state is only distinguishable while it is
moving, the design needs a second signal.

---

## Focus

One treatment, defined once, applied globally:

```
--focus-ring-width   2px
--focus-ring-offset  2px
--focus-ring-color   var(--color-accent)
```

Applied on `:focus-visible`, not `:focus`, so a pointer click does not leave a
ring behind while keyboard traversal always shows one. The offset keeps the ring
clear of the control's own border so it stays visible on any surface.

> **Never write `outline: none`.** Both HIG and Fluent treat a visible keyboard
> indicator as non-negotiable, and suppressing it per-control is how a portal ends
> up with controls that cannot be reached by keyboard at all. If a control needs
> different focus feedback, add it *alongside* the ring, not instead of it.

A border-colour change on focus is a good **additional** signal — it says "this is
the active field" to everyone. It is not a substitute for the ring, which says
"your keyboard is here".

---

## Density

Two presets, switched by `data-density` on the app shell. Every piece of chrome
spacing flows through these, so the whole shell retunes from one place.

| Token | Compact (default) | Comfortable |
|---|---|---|
| `--density-row-pad-y` | 0.2rem | 0.5rem |
| `--density-row-pad-x` | 0.7rem | 1rem |
| `--density-gap` | 0.4rem | 0.7rem |
| `--density-control-h` | 32px (`--hit-pointer`) | 44px (`--hit-touch`) |
| `--density-bar-h` | 32px | 46px |
| `--density-font-sm` | 0.8rem | 0.88rem |
| `--density-font-xs` | 0.68rem | 0.75rem |
| `--density-nav-pad-y` | 0.22rem | 0.45rem |
| `--density-nav-pad-x` | 0.5rem | 0.65rem |
| `--density-subnav-indent` | 1rem | 1.25rem |
| `--density-group-pad-y` | 0.15rem | 0.35rem |

**When designing chrome, specify in density tokens, not fixed spacing.** A design
that only works at one density will break for the other half of the users.

Comfortable is not merely "bigger" — it moves controls onto the 44px touch target,
so it is also the accessible preset.

---

## Icon library

**45 icons.** Inline SVG, generated from `assets/icons/svg` into
`IconLibrary.g.cs` by `scripts/generate-icons.py`.

### Drawing conventions

Every icon in the set follows these, without exception:

- **24×24 viewBox**, drawn on a 24px grid
- **2px stroke**, `stroke-linecap="round"`, `stroke-linejoin="round"`
- `fill="none"` — the set is stroked, not filled
- Optical balance over mathematical centring

### The set

| | | | |
|---|---|---|---|
| activity | add | agents | assistant |
| attach | avoid | back | bot |
| canvas | chat | check | close |
| configuration | conversation | copy | cron-jobs |
| dark-mode | delete | edit | error |
| file | folder | guide | help |
| home | light-mode | move | notifications |
| pause | pin | plugins | refresh |
| reports | running | search | send |
| skills | stop | thinking | todo |
| tools | usage | visibility | warning |
| workspace |  |  |  |

### Usage

```razor
<Icon Name="agents" />                      <!-- default size -->
<Icon Name="delete" Size="16" />            <!-- explicit px -->
<Icon Name="pin" Class="bn-icon-inherit" /> <!-- take the parent's colour -->
```

| Parameter | Meaning |
|---|---|
| `Name` | Icon name as it appears in `assets/icons/svg`. Case-insensitive. |
| `Size` | Edge length in px. |
| `Class` | Extra classes — see modifiers below. |
| `Title` | Accessible name. **Leave null when the icon sits beside its own label** — the default is `aria-hidden`, so a decorative icon is not announced twice. |

| Modifier | Effect |
|---|---|
| `.bn-icon-inherit` | Icon takes its parent's colour. Use when the icon is part of a label rather than an object in its own right. |
| `.bn-icon-flat` | Drops a gradient for the icon's flat tone, for a context with its own colour. |

### Tones

33 of the 45 carry a tone; the remaining 12 inherit. The tone lives in a generated
CSS rule rather than on the element, so any context — a disabled control, a
selected nav row, a button that needs the icon to match its label — can override
it with an ordinary rule.

| Hue | Icons |
|---|---|
| Green `#22C55E` | activity, check, notifications, todo, usage |
| Blue `#3B82F6` | add, agents, chat, conversation, guide, help, send |
| Purple `#8B5CF6` | assistant, canvas, skills, thinking |
| Red `#EF4444` | avoid, delete, error, stop |
| Amber `#F59E0B` | cron-jobs, folder, light-mode, pause, pin, tools, warning |
| Teal `#14B8A6` | plugins, reports |
| Indigo `#6366F1` | dark-mode, workspace |
| Cyan `#06B6D4` | bot |

> **These are the one sanctioned exception to "no raw hex".** They are generated
> from the artwork, so the SVG stays the design source. They do not currently
> respond to the theme — see [Known gaps](#known-gaps).

### Adding an icon

1. Draw it to the conventions above and save as
   `assets/icons/svg/<name>.svg`.
2. Run `python3 scripts/generate-icons.py`.
3. Use it as `<Icon Name="<name>" />`.

The generator normalises two things that do not survive being inlined into a
shared document, and it fixes them in code rather than in the artwork:

- **Gradient ids are document-global.** Several icons declare their gradient as
  `id="g"`. Once two of them render together, every `url(#g)` resolves to whichever
  landed first and the icons silently take each other's colours. Ids are rewritten
  per icon.
- **Hardcoded strokes cannot respond to state.** The stroke becomes
  `currentColor` and the artwork's tone moves to a generated CSS rule, which
  renders identically but can be overridden by any ordinary rule.

### What stays type, not iconography

Disclosure triangles, ellipses, bullets, chevrons and the streaming block cursor
are **type**. Turning them into SVG would be a downgrade — they sit in a text run,
inherit its colour and size for free, and align on the baseline.

---

## Component patterns

### Form fields

Two layouts, and which one a form uses is decided by its **fields**, not by taste:

- **Two-column** (label beside control) — a single column of fields whose values
  are long and varied: paths, prompts, ids, free text. The label needs to sit
  beside its control or the eye loses the pairing down a tall form.
- **Stacked** (label above control) — fields short and uniform enough to pack into
  a responsive multi-column grid, such as the cron editor's schedule parts. A label
  column there would spend horizontal space the grid needs.

Below the mobile breakpoint every form stacks: a label column and a usable control
do not both fit.

**Control anatomy**

| Part | Token |
|---|---|
| Background | `--color-surface` |
| Border | 1px `--color-hairline` |
| Radius | `--radius-sm` |
| Text | `--color-ink` at `--text-body` |
| Padding | `0.35rem 0.6rem` |
| Focus | Border → `--color-accent`, **plus** the global focus ring |
| Disabled | `opacity: 0.6` |
| Placeholder | `--color-ink-faint` |

**Field measure:** a text control is capped at `26rem`. Config text is read, not
scanned; past that width a value drifts too far from the label naming it.

### Buttons

| Intent | Fill | Text | Use for |
|---|---|---|---|
| Primary | `--color-accent` | `--color-on-accent` | The one action a view exists for |
| Danger (filled) | `--color-danger-fill` | `--color-on-solid` | The confirm step of a destructive action |
| Danger (tonal) | `--color-danger-wash` + `--color-danger` border | `--color-danger-text` | A Delete that *opens* a confirmation |
| Secondary | transparent or `--color-surface-2`, hairline border | `--color-ink` | Everything else |

**The distinction between the two danger treatments matters.** A filled red button
says "this is the irreversible act". A tonal one says "this leads somewhere
destructive". Using filled red for every Delete entry point makes a list of rows
shout at the user and devalues the real confirmation.

### Cards, dialogs, empty states

- **Card** — `--color-surface`, 1px `--color-hairline`, `--radius-lg`, **no shadow**.
- **Dialog** — same, plus `--shadow-overlay`, over a `--color-scrim` backdrop.
- **Empty state** — centred, `--color-ink-muted`, generous padding. Say what would
  be here and how to get it, not just "no items".

### Flex rows that must not overflow

A flex item defaults to `min-width: auto`, which resolves to the item's *intrinsic*
minimum — for a `<select>`, the width of its widest `<option>`. So `flex: 1` alone
does **not** guarantee an item can shrink, and a long option can push its siblings
straight off the viewport.

This is exactly how the mobile top bar came to measure 609px against a 375px
screen, with the refresh and overflow buttons entirely off-screen and unreachable.
The `text-overflow: ellipsis` already on those selects could never fire for the
same reason.

**Any flex child that carries text you are willing to truncate needs
`min-width: 0`.** And give the controls that must survive — a menu that is the only
route to Settings or Archive — an explicit `flex-shrink: 0`, so the squeeze lands
on the text, never on the affordance.

### Capability chips

A chip marks a **capability an agent has** — not a state it is in, and not a
warning. It takes the accent wash (`--color-accent-wash` ground,
`--color-accent-ring` hairline, `--accent` text, `--radius-pill`,
`--text-caption`), deliberately away from the status hues, because status colour
is reserved for things that change on their own.

The one chip in the system today is **Can delegate**, on the agent roster. It
appears when an agent's tool policy admits either `spawn_subagent` (create an
ephemeral child) or `agent_converse` (call an agent that already exists). Most
agents on a typical install carry neither, so most cards stay unmarked — which is
the point. A marker that appears on everything distinguishes nothing.

**What a capability chip must not claim.** `AgentDescriptor.CanDelegate` derives
from the agent's own `toolIds` and says nothing about *whom* it may call.
`subAgents` carries that, and is enforced only when the gateway's agent-exchange
policy is `whitelist` — and never on the spawn path at all. So the chip cannot
honestly name targets, and does not try to.

Runtime conditions are excluded for the same reason: the spawn tool additionally
needs a sub-agent manager, `SubAgentOptions.MaxDepth` above zero, and a session
that is not itself a sub-agent session. If delegation is ever disabled
platform-wide, the caller must suppress the chip — the property will not know.

**The lesson worth keeping.** This chip shipped reading `spawn_subagent` alone and
was wrong the first time it met a real configuration: an agent wired for
delegation via `agent_converse` rendered unmarked. A badge derived from a
capability must enumerate *every* mechanism that grants it, or it lies by
omission.

### Writing UI copy

Words are design material. Name things by what a person recognises, not how the
system is built. Active voice. A control says exactly what happens — a button
labelled **Publish** produces a toast saying **Published**. Errors explain what
went wrong *and* how to fix it: no apologies, no vagueness.

---

## Themes

Two themes, implemented as a **pure token swap**. The light block redefines only
tokens; not one component rule is duplicated. That is what makes a third theme
cheap — it would be one more block of the same 52 names.

Dark lives on `:root` and light on `[data-theme="light"]`. Dark is the default
because it was the portal's only theme historically, and upgrading must not
repaint an existing user.

Light is **tuned, not inverted.** The accent darkens (`#00b4d8` → `#0a7790`) so it
still passes contrast against white, and `--color-on-accent` flips from near-black
to white to suit it.

### Switching

Two entry points: the moon/sun button in the top bar, and a select in Settings.

The choice is stored per browser in `localStorage` under
`botnexus.portal.prefs`. An inline script applies it **before first paint and
before Blazor boots** — the WASM runtime takes seconds to start, and without this a
light-theme user would stare at a dark portal until it did, then watch it flip.

Dark is expressed by the *absence* of the attribute, so the default path writes
nothing.

**There is no "follow system" option.** The toggle is strictly two-state; a user
whose OS is set to light still opens BotNexus in dark until they choose otherwise.

### Designing for both

Every screen must be checked in both themes. The failure mode to watch for is a
colour that only works in one — most often white text on a fill that is light in
the light theme. If you specify a colour, specify it as a token and the theme
handles it. If you find yourself wanting "white here but dark there", you want an
`on-` token.

---

## Rules

1. **No raw hex in a component rule.** Icon tones are the one exception.
2. **No raw font size.** Use a type role.
3. **No bare pixel radius.** Use `--radius-sm`, `--radius-lg` or `--radius-pill`.
4. **No `outline: none`.** Ever, on anything focusable.
5. **No ad-hoc shadow.** Two tiers exist; flat content gets none.
6. **No inline `var()` fallback.** `var(--x, #hex)` hides a missing token.
7. **One accent.** Classification uses the category palette.
8. **Motion never carries meaning alone.**
9. **Chrome spacing uses density tokens**, not fixed values.
10. **Check both themes before calling a screen done.**

### Enforced by tests

Conventions nothing checks have already drifted, so these fail loudly on the things
that otherwise fail silently: no two icons declaring the same id (with both names in
the message), every `url(#...)` resolving inside its own icon, every stroke being
overridable, the tone overrides being declared after the tones, and every toned icon
having a rule. When you add to the system, add the fence with it.


### Deliberate exceptions

| Exception | Why |
|---|---|
| Icon tone hex values | Generated from artwork; the SVG is the design source |
| `.msg-content` `em` sizes | Rendered markdown must scale with its container |
| Chat bubble 3px tail radius | A shape decision, not a container radius |
| `--color-embed-surface` never themes | Third-party HTML assumes a white page |
| `--color-on-solid` never themes | Status fills are mid-to-dark in both themes |

---

## Known gaps

Documented honestly, so a designer knows where the system does not yet hold.

> Counting emoji: a sweep of `[\x{1F300}-\x{1FAFF}\x{2600}-\x{27BF}]` misses
> `⏹` (U+23F9), `✏️` (U+270F), `⏳` (U+23F3) and the arrows and geometric shapes.
> An "emoji: 0" result from too narrow a range is how this document came to claim
> the migration was finished when it was not. Sweep U+2190–U+2BFF as well as the
> emoji planes.

| Gap | Impact |
|---|---|
| **Icon sizing is not tokenised.** `<Icon Size>` takes a raw px number; call sites pass 12, 13, 14, 15, 16, 18 and 24 while only `--icon-sm` (16) and `--icon-md` (18) exist. | Icons are visually inconsistent between dense rows. Specify sizes from the two tokens and treat the others as legacy. |
| **Icon tones do not theme.** The 33 generated tone rules are raw hex, chosen against a dark surface. | Icons keep dark-theme hues in light mode. Acceptable but not ideal; avoid relying on icon colour to carry meaning. |
| **~29 legacy token aliases remain** (`--bg-primary`, `--border`, `--text-muted`, …) mapping onto the semantic names. | Two names for the same value. Prefer the `--color-*` names in new work. |
| **`--radius` legacy alias** still used at many call sites. | Prefer `--radius-sm` / `--radius-lg` explicitly. |
| **The mobile client does not share these tokens.** `BlazorClient.Mobile` has its own stylesheet and palette. | Anything designed for `/mobile` is a separate visual system today. Do not assume a portal token exists there. Its cache-busting is now in place (`mobile.css?v=mN`), but its palette is still raw hex. |
| **No "follow system" theme option.** | A light-mode OS user gets dark until they toggle. |

---

## Changing something

**A token value.** Edit the token block at the top of `app.css`. Nothing else
should need touching — that is the point of the layer. Check both themes.

**Adding a token.** Add it to `:root` and, if it is a colour, add the light-theme
override in the `[data-theme="light"]` block. A colour with no light value will
render its dark value on a white page.

**Adding an icon.** See [Adding an icon](#adding-an-icon).

**Deploying a CSS change.** The portal is served from a *deployed* copy of the
publish output, not from the build tree — building the gateway does not refresh
it. Publish the client, then deploy the whole output directory, never individual
files:

```
dotnet publish src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient -c Release
rsync -a --delete \
  src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/bin/Release/net10.0/publish/wwwroot/ \
  ~/.botnexus/extensions/botnexus-signalr/blazor/
```

The reason is **pre-compressed variants**. The build emits `app.css`,
`app.css.br` and `app.css.gz` side by side, and the gateway serves the compressed
one to any client sending `Accept-Encoding` — which every browser does.
Hand-editing `app.css` therefore changes the only copy a browser never reads: the
file is correct on disk, correct to `curl`, and the page keeps rendering the old
styles. `index.html` / `index.html.gz` behave the same way, so a hand-bumped
cache-buster silently does nothing.

**Verify the way a browser asks, not the way `curl` does:**

```
curl -s --compressed <url>/            | grep -o 'app\.css?v=[a-z0-9]*'
curl -s --compressed <url>/css/app.css | grep -c '<your new rule>'
```

A plain `curl` reads the uncompressed file and will cheerfully confirm a change no
user can see. This cost real time: several rounds of "verified, it is serving the
fix" were all reading a file no browser ever receives.

**Bump `?v=` in `index.html` whenever any hand-maintained asset changes.** It is
the cache key; a stale one leaves returning visitors on the old file. Bump it in
the **source and rebuild** — editing the deployed copy leaves the compressed
variant behind and reintroduces the problem above.

Each client carries **one token**, on its stylesheet and on every script it adds
itself:

| client | token | covers |
|---|---|---|
| portal | `app.css?v=dsNN` | `app.css` + 14 `js/*.js` |
| mobile | `mobile.css?v=mN` | `mobile.css` + 9 `js/*.js` |

One number per client rather than one per asset, because this whole class of bug
exists *because* a hand-maintained version was forgotten — halving the things to
forget is worth the occasional redundant fetch. Assets under `_framework` are
fingerprinted by the Blazor build and must not be given a query string; that
fights the build rather than helping it.

`CI: CSS Cache-Buster Guard` fails a PR that changes `app.css` without moving the
portal token. It exists because #30 shipped 20 unstyled component rules past
thirteen green checks — nothing else in CI looks at the pairing, since the tests
pass, the rules are in the file, and even `curl` confirms they are served. The
guard does not yet cover `mobile.css` or the scripts.

A service worker also ships (`service-worker.js`, registered from
`index.html`). It was previously believed to be the cause of stale assets; that
has not been reproduced. In the one environment measured it never registered at
all (`getRegistrations()` returned 0, registration failing with "An unknown error
occurred when fetching the script" while the script itself served HTTP 200), so it
could not have been serving anything. Treat its caching behaviour as unverified
rather than as the explanation.

---

## Status

**Implemented**

- Full token layer: colour, category, typography, shape, hit targets, elevation,
  material, motion, focus, density
- Light theme token set — 52 overrides, no duplicated component rules
- All 34 previously-undeclared tokens declared and theming correctly
- Every colour in every component rule flows through a token — 133 literal values
  migrated, 0 remain
- 144 dead `var(--x, #hex)` fallbacks removed
- White-on-accent contrast fixed: 13 call sites moved from ~2:1 to ~8.5:1
- Hardcoded radii 62 → 18; ad-hoc shadows 7 → 0
- Global `:focus-visible`, `prefers-reduced-motion`, `prefers-reduced-transparency`
- Light/dark toggle in the top bar and Settings, persisted per browser and applied
  before first paint
- Inter self-hosted, subset and preloaded
- Type scale applied: 250 raw font sizes across 37 values collapsed onto seven
  roles (7 relative `em` values left by design)
- Both themes verified WCAG AA by measurement
- Icon set at 45, generated. The composer toolbar is now emoji-free: `Steer` is
  text (matching its `Redirect` sibling), `Stop` takes the `stop` icon, and the
  `⏳ Running` / `✅ Completed` strings turned out to be dead code and were deleted
- `color-scheme` declared in both themes, so browser-drawn controls follow the theme
- "Can delegate" capability chip on the agent roster, reading both delegation tools

**Next**

1. Tokenise icon sizing and migrate the seven raw `Size` values.
2. Move icon tones onto tokens so they respond to the theme.
3. Retire the legacy colour aliases once no rule references them.
4. Migrate remaining `var(--radius)` call sites onto the explicit names.
5. Bring the mobile client onto the same token layer. Its top-bar overflow and
   cache-busting are fixed, but the stylesheet is still raw hex throughout.
6. Decide what to do about `ToolDescriptionFormatter` — it still maps tool names onto
   emoji across 24 switch arms (23 named plus the `_` fallback: `📄` read, `💻` exec,
   `🗣️` agent_converse …) and those render in the chat tool chips and the todo panel. This is the largest emoji surface left, and
   several have no equivalent in the 45-icon set, so it needs a design decision
   rather than a mechanical swap.
