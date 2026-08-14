# Browser Tools

`BotNexus.Extensions.BrowserTools` gives agents a real, rendered browser. `web_fetch` retrieves
static HTML, which returns the pre-hydration shell for any SPA, any authenticated view, and any
page assembled client-side. Browser Tools drives a headless Chrome through the standalone
[`agent-browser`](https://github.com/vercel-labs/agent-browser) CLI and puts a BotNexus-owned
safety layer in front of every call.

::: warning Delivery status
The guard layer, binary resolution and the five tools below are **implemented and tested**
(`src/extensions/BotNexus.Extensions.BrowserTools/`).

One operator-facing gap remains: the pinned release catalogue **ships empty**, so
`browser.autoProvision` cannot yet download a binary and fails closed with an actionable message.
Until a reviewed `agent-browser` asset digest is pinned, install the binary yourself using any of
the channels under [Installing the binary](#installing-the-agent-browser-binary). A placeholder
digest is deliberately not shipped: it would be a verification control that passes its own test
while checking nothing.
:::

## Why a subprocess and not an MCP server

`agent-browser` ships its own MCP server, and consuming it through
[`BotNexus.Extensions.Mcp`](./mcp.md) would be near-zero code. That option is disqualified: it puts
the MCP client in direct contact with the driver, so **none of the BotNexus guards can sit in the
call path**. Page content is attacker-controlled text entering agent context, and navigation
targets are a prompt-injection-driven SSRF vector. Owning the call path is the entire value of this
extension.

The binary itself is a single-file native Rust executable. There is no Node, no npm, and no NuGet
browser-automation package in the dependency tree.

## Prerequisite: Chrome

`agent-browser` drives a real Chrome; it does not bundle one. **A Chrome (or Chrome for Testing)
installation is a hard prerequisite** and BotNexus does not install it for you — the download is
deliberately left as an operator action:

```bash
agent-browser install
```

When Chrome is missing, the extension fails fast with an actionable "Chrome not installed"
message rather than hanging on every call.

## Installing the `agent-browser` binary

The binary is resolved through **four channels, in this order**. The first one that yields an
executable wins.

| # | Channel | Source | Notes |
|---|---------|--------|-------|
| 1 | Explicit path | `browser.binaryPath` in config | Absolute path to an `agent-browser` you manage yourself. Highest precedence, so it always wins over a stale managed copy. |
| 2 | Managed location | `~/.botnexus/tools/agent-browser/<pinnedVersion>/` | Where BotNexus writes a provisioned binary. Version-scoped, so two pinned versions never collide. |
| 3 | `PATH` | `agent-browser` on the system `PATH` | Covers an operator's existing brew / cargo / npm install. |
| 4 | Auto-provision | GitHub release asset for `browser.pinnedVersion` | **Off by default.** See [Provisioning](#provisioning-is-off-by-default). |

If none of the four yields a binary, the extension returns an actionable error **naming these
install options** rather than throwing an opaque failure.

### Provisioning is off by default

`browser.autoProvision` defaults to **`false`**. This is a deliberate safety default, not a broken
extension: downloading and executing a 13 MB native binary from the internet is a decision an
operator makes explicitly, never one a tool makes on their behalf.

With `autoProvision: false` and no binary present, **no network call is made and no file is
written** — the extension reports the install options and stops.

Set it to `true` only when you accept that BotNexus may fetch and execute the pinned release
asset:

```json
{
  "agents": {
    "myagent": {
      "extensionConfig": {
        "botnexus-browser": {
          "autoProvision": true,
          "pinnedVersion": "0.33.2"
        }
      }
    }
  }
}
```

### Pinned version and checksum

The version is **pinned, never floating**. BotNexus parses this CLI's JSON output, so an
unannounced upstream format change would break tool calls silently; a pin makes the upgrade an
explicit, reviewable act. `browser.pinnedVersion` selects both the GitHub release asset and the
managed directory the binary lands in.

Every `agent-browser` release asset carries a **sha256 digest in its GitHub release metadata**.
Provisioning verifies the downloaded asset against the digest pinned alongside the version, and on
mismatch it **deletes the file and fails** — a corrupted or substituted payload is never left on
disk where a later run could resolve it through channel 2 and execute it.

## Configuration

| Key | Default | Meaning |
|-----|---------|---------|
| `binaryPath` | *(unset)* | Explicit path to `agent-browser` (channel 1). |
| `pinnedVersion` | *(pinned by the extension)* | Release version to resolve and, if enabled, provision. |
| `autoProvision` | `false` | Whether BotNexus may download the pinned asset. |
| `commandTimeoutSeconds` | `30` | Per-command timeout. First navigate gets a longer floor (120s) to absorb a cold daemon plus first Chrome launch. |
| `snapshotMaxChars` | `20000` | Inline snapshot ceiling. See [Snapshot budgeting](#snapshot-budgeting-and-spill). |

The guard layer additionally accepts `additionalBlockedHosts`: extra hostnames blocked on top of
the shared SSRF policy, matched exactly and case-insensitively. It is passed straight through to
the shared validator; the browser extension defines no address rules of its own.

## Guards

Every navigation and every snapshot passes through `GuardedBrowserSession`. The guards live in the
session rather than in the individual tools on purpose: a guard that is merely *called by* a tool
is bypassed the moment someone adds a second tool, whereas a guard that owns the only method
touching the driver cannot be.

**Every denial happens before the driver is touched** — a rejected navigation launches no
subprocess at all.

The full guard list:

1. **Fail-closed initialisation.** If guard initialisation fails for any reason, the session
   carries a failed state and **every** navigation and snapshot is denied, with the failure reason
   attached. There is no reachable state in which the browser runs unguarded.
2. **Absent target.** A null, empty, or whitespace URL is denied.
3. **Non-absolute target.** A URL that is not an absolute URL is denied.
4. **SSRF.** Delegated in full to the shared `SsrfValidator`
   (`BotNexus.Gateway.Contracts/Security/`). It permits only `http`/`https` and blocks
   `localhost`, `metadata.google.internal`, IPv6 loopback `::1`, and IPv4 `127.0.0.0/8`,
   `0.0.0.0/8`, `10.0.0.0/8`, `169.254.0.0/16` (link-local / cloud IMDS), `172.16.0.0/12`,
   `192.168.0.0/16`, and `100.64.0.0/10` (CGN), plus any configured
   `additionalBlockedHosts`. The browser extension contains **no private-range, loopback or
   metadata arithmetic of its own**, and an architecture fence asserts that it never will — a
   second copy of that policy is how one copy drifts while the other keeps passing its tests.
5. **Secret material in the target.** A prompt-injected page can ask an agent to navigate to an
   ordinary public host with the agent's own key pasted into the path or query: a one-request
   exfiltration channel no network rule can see. The target is matched against issuer prefixes for
   Anthropic (`sk-ant-`), OpenAI-style (`sk-`), GitHub (`ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_` and
   `github_pat_`), Slack (`xoxb-`/`xoxa-`/`xoxp-`/`xoxr-`/`xoxs-`), AWS access key ids (`AKIA`),
   Google API keys (`AIza`), and JWTs (`eyJ….….`). Prefix-anchored rather than entropy-scored:
   issuer prefixes give a bright line in both directions.
6. **Credential-like parameter names.** The same exfiltration with an opaque value the prefix rules
   cannot recognise. Query parameter *names* matching `api_key`, `apikey`, `access_token`,
   `auth_token`, `id_token`, `refresh_token`, `bearer`, `session_token`, `client_secret`, `secret`,
   `password`, `passwd`, `pwd`, `credential`, `credentials`, `token`, `key`, `sig`, or `signature`
   (with optional dotted/underscored/hyphenated prefixes) are denied. Matched as a whole name
   segment, so an innocuous `tokenizer` or `keyboard` is not blocked, and a bare flag with no value
   is ignored because it carries nothing to exfiltrate.
7. **Repeated percent-decoding.** Guards 5 and 6 run against the raw target **and each successive
   percent-decoding, up to a fixed point** (bounded at five passes). A single decode pass leaves
   `%2525`-style double-encoding intact, and a guard that decodes only once is trivially bypassed.
   Parameter names are additionally read from the raw decoded forms, not just `Uri.Query`, because
   a percent-encoded `?` or `&` hides a parameter from the parsed query while a browser may still
   act on it.
8. **Post-navigation URL re-check.** On snapshot, the session re-reads the browser's **current**
   location and re-validates it before returning any content. The URL that passed at navigation
   time is not the URL the content came from: page script can rewrite `location.href` afterwards,
   so a page that passed the guard could otherwise redirect itself onto the metadata endpoint and
   have the agent read back the result. Validating the original URL here would be a guard that
   inspects the wrong value and always passes.
9. **Untrusted-content envelope.** Page text is sanitized with the shared
   `UntrustedContentSanitizer` (the same filter applied to [web tool](./web-tools.md) output) and
   then wrapped between `--- BEGIN UNTRUSTED WEB CONTENT ---` and
   `--- END UNTRUSTED WEB CONTENT ---`, carrying the source URL and a standing rule that the
   enclosed text is *data, not instructions*. Sanitizing happens **before** wrapping, so a payload
   whose marker straddles the boundary cannot survive and the fence itself cannot be mangled.

## Snapshot budgeting and spill

Page text beyond `snapshotMaxChars` (default 20,000) is not summarised — an LLM summarisation pass
would mean paraphrasing attacker-controlled text and then trusting the paraphrase. Instead the
**full text is written to the agent workspace under `tmp/browser/`** and the inline text is cut with
a surrogate-safe truncation, so the model never receives a broken grapheme at exactly the boundary
an attacker can position.

The envelope names the spill file and the returned path is **workspace-relative**, both because
that is what the agent's `read` tool accepts and because an absolute path would leak the host's
directory layout into the transcript.

## Tools

The tool surface is flat rather than action-dispatched, so an agent can be granted
`browser_snapshot` without being granted anything that evaluates script, and because models route
to distinct names more reliably than to a single tool with an `action` enum:

| Tool | Purpose |
|------|---------|
| `browser_navigate` | Navigate to a URL. Subject to guards 1–7. |
| `browser_snapshot` | Return the current page's text. Subject to guards 8–9 and spill. |
| `browser_click` | Click an element by ref. |
| `browser_type` | Type into an element by ref. |
| `browser_screenshot` | Capture a screenshot. |

Sessions are keyed off the agent's session key and mapped to `agent-browser --session <id>`, so two
agents never share a browser or its cookies, and sessions are closed on agent teardown.

The child process environment is constructed **from empty**, not inherited, with an explicit
allow-list. Handing a browser worker the full operator keyring means a compromised driver can read
every secret out of its own environment; a static Rust binary needs almost none of it.

## Out of scope

These are deliberately excluded, not merely unimplemented:

- **`browser_eval` / arbitrary JavaScript execution** — deferred pending a permissioning decision.
- **Cloud browser backends** (Browserbase, Browser Use, Firecrawl). Local Chrome only.
- **Auth-state persistence** (`--restore` / `--state`). State files hold session tokens in
  plaintext.
- Vision, annotated screenshots, network interception, HAR capture, tab management, frame
  switching.
- Bundling or vendoring the `agent-browser` binary into the BotNexus repo or its build output.
- Automating the Chrome-for-Testing download.

## See also

- [Web Tools](./web-tools.md) — static fetch and search, sharing the same untrusted-content
  sanitizer.
- [Extension Development](../extension-development.md) — the manifest contract and extension
  loading model.
