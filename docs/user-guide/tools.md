# Tools

**Tools are bookmarks to your other web interfaces, opened inside the portal.**

Add Grafana, a NAS, a download client or a hypervisor, and it appears in the sidebar under
**Tools**. Clicking it opens that site inside BotNexus rather than in a separate tab, so the things
you administer sit next to the agents that administer them.

## This is not the same "tools" as an agent's tools

BotNexus uses the word twice, for unrelated things. This page is about the second.

| | What it is | Where it is configured |
| --- | --- | --- |
| **Agent tools** | Capabilities an agent can invoke — `read`, `bash`, `web_fetch` | `toolIds` on the agent, in `config.json` |
| **Tools (this page)** | Links to external web interfaces, framed in the portal | The Tools page, stored in the gateway |

An agent cannot use anything listed on this page, and adding a tool here grants nothing to any
agent. If you are trying to control what an agent may *do*, see [Agents](agents.md).

## Adding one

**Tools → + Add Tool**, then five fields:

| Field | Notes |
| --- | --- |
| **Name** | What appears in the sidebar |
| **URL** | The full address, including scheme and port — `http://192.168.1.10:8080` |
| **Icon** | An emoji, up to 8 characters. Optional |
| **Order** | Ascending. Leave gaps (10, 20, 30) so you can insert later without renumbering |
| **Sandbox the embedded frame** | On by default. Leave it on unless a site misbehaves |

Tools are stored by the gateway, in `tools.sqlite` beside its other stores — not in your browser —
so they are the same on every device that reaches the portal.

## Sandboxing

The checkbox renders the frame with `sandbox="allow-scripts allow-same-origin allow-forms
allow-popups"`. The embedded site can run its own scripts, sign you in and submit forms, but is
constrained in what it can do to the page hosting it.

Turn it off only for a site you trust that genuinely breaks inside the sandbox, and understand that
you are then giving that page more latitude over the portal around it.

## Many sites refuse to be embedded

This is the part worth reading before adding a dozen tools and wondering why half of them are
blank.

A site can forbid framing with an `X-Frame-Options` header or a `frame-ancestors` directive in its
Content-Security-Policy. **The browser enforces that and BotNexus cannot override it** — no setting
here changes the outcome. It is the site's decision, and a reasonable one: it is what stops another
page wrapping its login form.

Checking before you add is one command:

```bash
curl -sI http://192.168.1.10:8080 | grep -iE 'x-frame-options|content-security-policy'
```

Anything reporting `SAMEORIGIN`, `DENY`, or `frame-ancestors 'self'` will not embed here.

In practice, on a typical homelab:

| Usually embeds | Usually refuses |
| --- | --- |
| Radarr, Sonarr, Lidarr and the rest of the *arr family | SABnzbd (`X-Frame-Options: SameOrigin`) |
| Plex | Unraid (`frame-ancestors 'self'`) |
| Proxmox | Most router and firewall admin pages |
| Grafana, Home Assistant (when configured to allow it) | Anything with a strict security posture |

A tool that refuses framing is still worth adding as a launcher if you are happy to open it in a
new tab — but see the limitation below first.

### When a site refuses

The tool page is meant to detect this and offer **Open in new tab** instead of a blank frame.

**That detection is unreliable.** A blocked frame gives the page almost nothing to work with: the
browser fires no distinguishable event, and cross-origin access to the frame's contents is
forbidden, so the check is a timeout. A site that returns a page the browser then refuses to
*render* can still look like a successful load, and the fallback never appears — leaving a blank
panel with no explanation and no link.

If a tool shows an empty white panel, that is what has happened. The site is fine; it is declining
to be framed. Open it directly in a browser tab.

## Editing and removing

The Tools page lists what you have configured, with edit and remove on each row. Removing asks for
confirmation and cannot be undone — but a tool is only a name, a URL and an icon, so re-adding one
takes a moment.

**Reload** re-reads the list from the gateway. Useful if you have changed tools from another device
or through the API.

## The API

Tools are managed over REST, so they can be scripted or seeded.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/tools` | List all tools |
| `GET` | `/api/tools/{id}` | Fetch one |
| `POST` | `/api/tools` | Create. The `id` is supplied by the caller |
| `PUT` / `PATCH` | `/api/tools/{id}` | Update |
| `DELETE` | `/api/tools/{id}` | Remove |

```bash
curl -X POST http://your-gateway:5005/api/tools \
  -H 'Content-Type: application/json' \
  -d '{"id":"radarr","name":"Radarr","url":"http://192.168.1.10:7878",
       "icon":"🎬","order":20,"sandboxEnabled":true}'
```

`id` is yours to choose and must be unique; the portal uses it in the URL of the hosted page
(`/tools/radarr`). `sandboxEnabled` defaults to `true` when omitted.

## Related

- [Agents](agents.md) — including `toolIds`, which is the *other* kind of tool
- [Extensions](extensions.md) — how a tool an agent can actually call gets added
