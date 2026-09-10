# The portal

The portal is the web interface the gateway serves — the same host and port as the
REST API, at `/`. This page describes the shell you look at: what is where, why the
sidebar comes and goes, and what the page is doing while it is still empty.

For the SignalR channel that the portal talks over, see
[Channels](channels/signalr.md).

---

## The shell

Three pieces, from the top:

| | |
|---|---|
| **Top bar** | The active agent's identity and the connection status indicator. Visible from every page. |
| **Navigation toolbar** | Primary navigation, running left to right under the top bar. |
| **Sub-toolbar** | A second row, present only when the section you are in has sub-navigation. |

Navigation used to live in the sidebar, above and below the conversation list, where
eleven entries and your conversations competed for the same vertical space. It now runs
along a toolbar and the sidebar is the conversation list and nothing else.

### The overflow menu

Entries that do not fit the width move into a **More** menu at the end of the toolbar.
Which ones move is measured rather than fixed: plugins contribute nav entries and you
can reorder them, so the count is not knowable in advance.

If you widen the window, items come back out of the menu.

### Sub-navigation

Sub-navigation is contextual — it appears only while you are on the page that has it,
which is how it behaved as an inline sub-list under a sidebar item. It is a second
toolbar row rather than a set of dropdowns, because dropdowns would promise persistent
menus that were never there.

The Tools sub-navigation is collapsed by default; expand it to list the active agent's
tools. An agent with no tools configured simply shows an empty sub-nav — there is no
placeholder row.

---

## The conversation sidebar

The sidebar holds the conversation list, and it appears **only on the routes where
picking up a conversation is the task**:

| Route | Sidebar |
|---|---|
| `/` (landing) | yes |
| `/chat`, `/chat/*` | yes |
| `/agent`, `/agent/*` | yes |
| everything else — Activity, Agents, Configuration, Skills, Plugins | no |

A query string does not change the decision: `/activity?agent=x` is still Activity.

This is an allow-list rather than a list of pages to exclude. The default is that a page
does **not** get chat furniture, and a page that genuinely wants the conversation list
says so in one place — otherwise every page added later silently gets 240px of sidebar
back.

Two controls travel with the sidebar and are hidden wherever it is:

- **The burger** toggles it. On a page with no sidebar it would be a control that does
  nothing.
- **The splitter** drags the boundary between sidebar and canvas. With no sidebar there
  is no boundary, and a drag handle against nothing reads as a bug.

---

## While the page is loading

The chrome paints almost immediately; the hub connection takes longer. Rather than sit
behind a centred "Connecting…", the landing page, chat and Activity each draw a
**skeleton of the page that is arriving** — summary tiles and a starter, alternating
message rows and a composer, or a toolbar and table rows.

A skeleton says three things a spinner cannot: that something is coming, roughly how
much, and where it will be.

If loading **fails**, the skeleton is replaced by the error rather than left in place. A
real error is information, and a skeleton over the top of it would promise content that
is not coming.

The shapes are `aria-hidden`; a single polite live region announces "Loading…" instead.
A screen reader given a dozen empty blocks learns less than one told what is happening.

---

## Activity

### Finding a row

The **search box** is the first control on the filter bar. It matches the title or the
agent id — agent, because "the deploy run that failed" is how people describe a row, and
the agent is a column already on screen.

### Filters

Two facets stay on the bar, because they are how you scope the page in the first place:

- **Agent**
- **Status** — Active, Archived, or All

Four sit behind **More filters**, because they are at "any" almost always and were most
of the bar's width for a fraction of its use:

- **Recency**
- **Origin**
- **Pin state**
- **Live state**

The **More filters** button carries a count of how many *hidden* facets are set to
something other than their default. Only the hidden ones are counted: a facet left set
behind a collapsed panel is otherwise an invisible explanation for missing rows, which
is worse than the crowding the split set out to fix. The visible facets need no badge —
they show their own state.

---

## Interface density

The portal ships a density preference that switches every chrome spacing token at once.
It is documented with the rest of the SignalR portal notes under
[Interface density](channels/signalr.md#interface-density).

---

## Next steps

- [Conversations & sessions](conversations.md)
- [Channels](channels/signalr.md) — the transport underneath, and PWA support
- [Plugins & the marketplace](plugins.md) — plugins contribute their own nav entries
