# Connection registry and secret references

A plan for letting an agent act on a named server — a Proxmox host, a NAS, an internal
API — without ever holding the credential for it.

Status: Phases 1-4 and 6 delivered. Phase 5 (the Proxmox agent) designed but paused before
implementation - see its section for the findings and the open questions.

## The problem

Today an operator who wants an agent to manage a server has nowhere to put the details.
The endpoint could go in a system prompt, and the password could go in `config.json`
next to it, and both of those are bad in ways that are worth naming precisely.

## The rule that shapes the whole design

**An agent that can read a credential can be talked into disclosing it.**

Anything that reaches an agent's context can carry instructions: a fetched web page, a
file in its workspace, an inbound channel message. If a root password is in the context
because the agent read it from configuration, then a prompt injection walks out with it.
No amount of system-prompt instruction fixes this, because the injected text and the
legitimate text arrive through the same channel.

So the design goal is **capability without custody**: the agent learns that
`proxmox-main` exists and what it can ask of it, and a tool performs the work holding a
credential the agent never sees.

This is not a new position for the codebase. `EnvironmentApiKeys` exists so provider keys
live in the environment rather than in config, and `WebhookSecret` is a value type whose
`ToString()` returns a redacted marker so a secret cannot reach a log by accident. This
plan extends both patterns rather than introducing a policy.

## What already exists

| Piece | Where | Notes |
|---|---|---|
| Location registry | `gateway.locations` in `PlatformConfig` | `Dictionary<string, LocationConfig>`; `type` ∈ filesystem, api, mcp-server, database, remote-node |
| REST surface | `LocationsController` (`/api/locations`) | list, add, update, remove |
| CLI surface | `botnexus locations` | |
| Portal client | `LocationsApiClient` | |
| Config pipeline | JSON + SQLite behind `IOptionsMonitor` | `ResilientJsonConfigurationSource` keeps last-known-good; `CrossProcessConfigLock` serialises writes |
| Generated schema | `docs/botnexus-config.schema.json` | Built from `[Display]`/`[ConfigField]` annotations; also drives the portal Configuration page |
| Redacting secret type | `WebhookSecret` | `Reveal()` explicit, `ToString()` redacted, `TryCreate`/`Create`/`Generate` |

Two gaps: **no agent can see locations**, and **nothing in that shape models a secret**.

Note the config pipeline already reloads live rather than at start-up, so "the app reads
the latest settings" is satisfied by construction — with one caveat handled in Phase 2.

## Design

### 1. Locations carry connection metadata and a *reference*

```jsonc
// gateway.locations
"proxmox-main": {
  "type": "proxmox",
  "endpoint": "https://pve.example.lan:8006",
  "username": "automation@pve",
  "credentialRef": "env:PROXMOX_TOKEN",
  "verifyTls": true,
  "description": "Main hypervisor",
  "tags": ["homelab", "hypervisor"]
}
```

New fields on `LocationConfig`: `Username`, `CredentialRef`, `VerifyTls`, `Tags`.
`Endpoint`, `Description` and `Properties` already exist.

`credentialRef` is a **reference, never a value**. A literal in that field must fail
validation — not be discouraged by review. This is the `SkillPath` trick the repo already
uses: make the wrong state unrepresentable rather than merely unwise.

### 2. `SecretRef` and `Secret`

- **`SecretRef`** — a parsed `scheme:identifier`. Construction rejects anything that
  looks like a bare secret (no scheme, or a scheme nobody registered). Validation surfaces
  the failure at config load, next to the offending key.
- **`Secret`** — modelled directly on `WebhookSecret`: `Reveal()` explicit, `ToString()`
  returns `Secret(redacted)`, equality is length-independent where it matters.

### 3. `ISecretResolver` with pluggable providers

```csharp
Task<Secret> ResolveAsync(SecretRef reference, CancellationToken ct);
```

Four providers, registered by scheme. All four were requested; their properties differ
enough to be worth stating plainly rather than treating as interchangeable:

| Scheme | Backing | Honest assessment |
|---|---|---|
| `env:` | Process environment, from `botnexus.env` | Simplest, matches existing provider-key handling. Visible to anything that can read `/proc/<pid>/environ` as the same user. |
| `file:` | One secret per file, mode `0600` | Easy per-target rotation; works with config management. Protection is filesystem permissions plus whatever disk encryption exists. |
| `sqlite:` | Alongside `config.db` | Convenience and a single backup artifact. **SQLite is not encrypted at rest** — this is not a security improvement over `file:`, and the plan should not present it as one. |
| `keyring:` | libsecret / Keychain | Best at-rest protection. Needs a session bus, so on a headless host it requires deliberate setup and may be unavailable; the provider must degrade with a clear error rather than a stack trace. |

Resolution happens **at call time**, not at start-up. That is what makes credential
rotation take effect without restarting the gateway — the caveat referenced above, since
environment variables are otherwise fixed for a process lifetime.

### 4. The agent-facing `locations` tool

Returns **metadata only**: name, type, endpoint, username, description, tags. Never
`credentialRef`, never a resolved secret. The DTO is a separate type from `LocationConfig`
so a field added to config cannot silently start flowing to agents.

### 5. Fences

The repo enforces architecture rules as tests, and this design depends on two:

- Nothing outside `ISecretResolver` implementations may call `Secret.Reveal()`.
- The locations tool DTO may not expose `credentialRef`, asserted structurally rather
  than by string match.

Pattern to copy: `ConfigurationReadPathFenceTests`, `SkillPathConstructionArchitectureTests`.

## Security properties, and what this is not

**Holds:** credentials stay out of agent context and out of transcripts; a compromised or
injected agent can enumerate targets but cannot read secrets; secrets never enter git; a
secret cannot reach a log through ordinary string interpolation.

**Does not hold:** this is not a secrets vault. Anything running as the gateway user can
read what the gateway can read, `sqlite:` and `file:` are not encrypted at rest, and a
malicious *tool* — as opposed to a malicious prompt — is outside the model. Tools are
trusted code; that is exactly why the credential lives there and not in the context.

## Phases

**Phase 1 — Secret primitives. Delivered.**

| Type | Location |
|---|---|
| `Secret` | `src/domain/BotNexus.Domain/Security/Secret.cs` |
| `SecretRef` | `src/domain/BotNexus.Domain/Security/SecretRef.cs` |
| `ISecretResolver`, `ISecretProvider`, `SecretResolutionException` | `src/gateway/BotNexus.Gateway/Security/SecretResolution.cs` |
| `SecretResolver` | `src/gateway/BotNexus.Gateway/Security/SecretResolver.cs` |
| `EnvironmentSecretProvider` (`env:`) | same folder |
| `FileSecretProvider` (`file:`) | same folder |

Registered in `GatewayServiceCollectionExtensions`. Providers go in via `TryAddEnumerable` so a
repeat registration is a no-op, while two *different* providers claiming one scheme is fatal at
construction - silently letting one win would mean a credential resolving from a store nobody
expected.

Decisions taken while building it, beyond the design above:

- `Secret` is a new type rather than a widening of `WebhookSecret`. A webhook secret is one
  BotNexus generates, so it can be constrained to `A-Z a-z 0-9 _ -`; a credential someone else
  issued cannot be. Widening the existing type would have removed a rule that is load-bearing
  for webhooks.
- `SecretRef.ToString()` is deliberately *not* redacted. A reference names a location, and being
  able to print it is what makes a resolution failure diagnosable - the exception carries the
  reference for exactly that reason. The inversion relative to `Secret` is the design, not an
  oversight.
- `FileSecretProvider` refuses a group- or world-readable file. A secret file every account on
  the box can read offers no protection, and appearing to work would be worse than failing. The
  check reuses `SecureFilePermissions.IsReadableByOthers`, which already handles POSIX modes and
  Windows ACLs.
- It also trims trailing newlines. Every ordinary way of creating such a file appends one, and a
  credential silently carrying `\n` fails at the remote end looking like a wrong password rather
  than a formatting mistake. Leading whitespace is preserved - no tool adds that by accident.

*Verified:* 72 domain tests, 25 gateway tests, 8 fence tests.
`SecretUnwrapFenceArchitectureTests` registers every `Reveal()` call site under `src/` and asserts
both credential types keep their redacted `ToString` and their `PrintMembers` override. It was
confirmed to redden when an unregistered unwrap is introduced.

**Phase 2 - Config surface. Delivered.**

`LocationConfig` gains `Username`, `CredentialRef`, `VerifyTls` (defaulting to *on*) and `Tags`,
and every property on the type - the five pre-existing ones included - now carries `[Display]`
and `[ConfigField]`. That took five entries out of the `ConfigFieldCoverage` baseline, 174 to 169.

`credentialRef` is deliberately **not** marked `Secret` and renders as ordinary text. It holds a
pointer, and a value with no scheme fails validation, so a pasted password cannot end up there.
Masking it would hide the one part an operator needs to read to fix a mistyped reference while
protecting nothing. `connectionString` stays masked, because it really does hold a credential.

`PlatformConfigValidator` rejects a `credentialRef` that is not a well-formed `SecretRef`, naming
the key - `gateway.locations.proxmox-main.credentialRef` - and never echoing the value, since if
it really is a pasted credential the error may well be logged. The check applies to every
location type, not only the ones that currently take an endpoint.

`docs/config.example.jsonc` is the annotated example. It is documentation and nothing loads it:
the gateway's reader rejects comments, so a commented `config.json` is silently ignored and the
gateway runs on defaults. The generated schema is the mechanism that actually helps while editing
the real file - point `$schema` at it and an editor shows the same descriptions inline.

*Verified:* 13 validation tests, 6 coverage-fence tests, and a preview gateway booted on the
example config - all three example locations load, `example-db` is redacted by the API, and the
portal Configuration page renders a Locations group per location with the fields in their declared
order, `Connection string` masked and `Credential reference` plainly editable.

Two bugs found and fixed on the way, both the same shape as each other and as the test-isolation
one - a Windows path literal used on a platform where it means nothing:

- `botnexus config schema` defaulted its output to `docs\botnexus-config.schema.json`. Off
  Windows that wrote a file *named* `docs\botnexus-config.schema.json` into the working directory
  and no schema where anyone would look. The documented regeneration command therefore did not
  work on Linux or macOS at all.
- `botnexus validate` reported `VALID` for a config file that is not JSON. The loader falls back
  to defaults on unreadable JSON so the gateway stays up, which is right for the gateway, but the
  validator was then validating that pristine fallback rather than the operator's file - saying
  yes to a config the gateway could not read and was silently ignoring.

**Phase 3 - Locations tool. Delivered.**

`list_locations` (`src/gateway/BotNexus.Gateway/Tools/ListLocationsTool.cs`) lets an agent discover
what this installation knows about, and gives it no means of authenticating to any of it.

`LocationEntry` is a hand-written projection rather than a serialisation of `LocationConfig`, so a
field added to configuration does nothing until someone edits `Project`. Two exclusions are
deliberate:

- `credentialRef` - names where a credential lives. Harmless in a file an operator reads; a useful
  hint to anyone who has talked their way into an agent's context.
- `connectionString` - **is** a credential. The locations REST API derives its display value as
  `Path ?? Endpoint ?? ConnectionString` and redacts afterwards; reusing that helper here would have
  handed an agent the connection string of every database location. The projection reads `Path` and
  `Endpoint` by name and never consults `ConnectionString`. A database location therefore has no
  `address` field at all.

`hasCredential` is a boolean: enough for an agent to know a target is authenticated, never which
credential nor where it is kept.

Gated by the standard allowlist, like every other tool - see the resolved open question below. Only
configured locations are listed; the world descriptor's derived entries for agent workspaces and
internal directories are an implementation detail.

*Verified:* 14 tool tests, 5 gate tests, 4 fence tests, and a live agent call. The raw tool result
was read directly rather than trusting the agent's prose - which mattered, because the model
summarised "all three have credentials" when the payload correctly reported `hasCredential:false`
for the one without.

`LocationsToolExposureFenceArchitectureTests` asserts the member list **exactly**, not against a
deny-list of suspicious names. The field that would actually have leaked was called
`PathOrEndpoint`; an exact set catches that and a name filter does not. Confirmed to redden when a
`CredentialRef` member is added.

**Phase 4 - `sqlite:` and `keyring:` providers. Delivered.**

`SqliteSecretProvider` reads `sqlite:name` from `~/.botnexus/secrets.db`, and
`botnexus secret set|list|remove` writes it. The CLI is part of the same change because a store
nothing can populate would make the backend decorative.

The value is never a command-line argument - it is read from stdin, piped or prompted without echo.
Anything on a command line reaches shell history, `ps` output and CI logs. There is deliberately no
`secret get`: a command whose purpose is to print a credential to a terminal is a facility for
exfiltrating one. `list` shows names and timestamps only.

`secrets.db` is restricted to its owner on creation and after every write, and `SecretCommand.cs`
is registered with `SecretFilePermissionFenceArchitectureTests` as a secret-writing surface.

**This backend is not encrypted at rest and is not stronger than `file:`.** It buys one artifact to
back up instead of a directory of files. The documentation says so in those words rather than
letting "database" imply security.

`KeyringSecretProvider` reads `keyring:service/account` through the platform's own tool -
`secret-tool` on Linux, `security` on macOS. It is the only backend that protects a credential at
rest, and the least available: a Secret Service daemon needs a session bus and an unlocked keyring,
which a headless server has neither of. Absence produces an instruction, not a stack trace.

**Windows is unsupported and says so.** Reading a Credential Manager entry needs `CredReadW` rather
than a command, and an untested P/Invoke would be worse than an honest gap.

*Verified:* 10 sqlite tests, 14 keyring tests, and the CLI end to end - piped value stored, `600`
permissions, and the value appearing zero times in `list` output.

**Phase 5 - Proxmox agent. Paused before implementation** (2026-08-25), at the operator's request:
the Proxmox side needs work first. Nothing was built. The design discussion established the
following, which is recorded so resuming does not mean rediscovering it.

*What was asked for:* one agent that reaches **any** Proxmox host in the environment and can
manage, update, deploy, monitor and recommend improvements - with **no add, move or delete without
explicit approval** - built as a platform agent following the SOUL/IDENTITY/AGENTS/TOOLS/WORLD
structure.

*Findings about the platform's approval machinery:*

- `IExecApprovalManager.Issue/TryRedeem` is the usable primitive. A token is bound to **session
  plus canonical action** with a 15-minute TTL, so an approval for one action cannot be spent on
  another. This is what the mutating path must use.
- `IToolPolicyProvider.RequiresApproval` is **not** interactive - it returns a bool. The seam that
  calls it says so outright, in `ToolPolicyHookHandler.cs`: "Approval is required and there is no
  workflow at this seam that can obtain it. Apply the configured fallback posture instead of falling
  through silently." It then applies `GetApprovalFallback`, deny or allow. Useful as an outer guard,
  useless as an ask-a-human loop.
  (Declared on `IToolPolicyProvider` in `Gateway.Contracts/Security/ToolPolicy.cs`, implemented in
  `Gateway/Security/DefaultToolPolicyProvider.cs`; the comment is in the caller, not either of them.)
- Therefore approval must be **enforced in the tool**, never in `SOUL.md`. A prompt-level rule is a
  preference that a confused turn or injected text bypasses silently, which is the same reasoning
  that keeps credentials out of agent context.
- Open UX question with a security edge: if the agent describes the action and the operator says
  yes, the operator is trusting the agent's description. The approval prompt must render the
  **canonical action from the tool**, not the agent's summary, or "restart the test VM" can be
  attached to a token for something else.

*Finding about the agent file structure:* `WORLD.md` is **not** in the default load order.
`WorkspaceContextBuilder.DefaultPromptFiles` is `AGENTS.md, SOUL.md, TOOLS.md, BOOTSTRAP.md,
IDENTITY.md, USER.md, MEMORY.md`; only `ModelProfileTool`, a reporting surface, mentions
`WORLD.md`. A WORLD.md written on the assumption in the quick-start guide would never be read and
nothing would say so. Name the files explicitly via `AgentDescriptor.SystemPromptFiles` instead of
relying on defaults. Note also that `BOOTSTRAP.md` loads by default and is absent from that guide,
and that `USER.md` and `MEMORY.md` are owner-private - they never reach a conversation with
non-owner participants.

*Proposed shape, not built:*

- Two tools, not one: `proxmox_query` (read-only, no approval) and `proxmox_apply` (mutating,
  token-gated). Mixing verbs in one tool would force approval on every status check.
- Multi-host comes free from the locations registry - each host is a location, the tools take a
  location name.
- Three layers: a least-privilege Proxmox API token (strongest, because Proxmox enforces it), a
  per-location `allowWrites` flag, and the per-action approval token.
- Recommendations need no new machinery - read-only analysis plus a `proxmox-tuning` skill.
- Sub-phases so it can stop anywhere: 5a read-only query; 5b approval-gated apply; 5c the agent
  definition and skills; 5d create/delete last, being the largest surface and least reversible.

*Unanswered, needed before starting:* cluster or standalone hosts; how the operator wants to
approve, and on what surface; whether "update" includes host package updates (which can reboot);
whether a Proxmox host is `remote-node` with `properties.kind` or a new location type
(recommendation: the former, rather than growing the enum per vendor); and the PVE version.

**Phase 6 - Documentation. Delivered.**

`docs/user-guide/secrets-and-locations.md`, listed in the portal guide and copied into the
`botnexus-guide` skill: the rule the design follows, registering a location, all four backends with
exact commands and their honest at-rest properties, granting `list_locations`, a troubleshooting
table keyed on the actual error strings, and a section stating what the design does *not* protect
against.

It also records the gap Phase 5 leaves. **No tool consumes a credential yet**, so an agent can
discover a Proxmox host and not act on it. The two interim routes are described with their cost:
`shell` + `curl` works today but puts the token within the agent's reach, voiding the property for
that agent; an MCP server keeps the property at the cost of a second component. Documenting the
gap matters more than documenting the feature - someone planning around this needs to know where
it stops.


## Decisions taken

- All four secret backends will exist; `env:` and `file:` ship first (Phase 1), `sqlite:`
  and `keyring:` follow (Phase 4).
- Proxmox is a native extension rather than an MCP server, for control over the verb
  allow-list and the audit trail.
- First slice is Phases 1–3: the security model and target discovery, before anything can
  consume it.

## Open questions

1. Should `credentialRef` support more than one credential per location (e.g. a token
   *and* a TLS client cert)? A `credentials` map keyed by purpose is more general; a
   single ref is simpler. Recommend starting single and widening if a consumer needs it.
2. ~~Should the locations tool be opt-in per agent via `toolIds`, or available to all?~~
   **Resolved: available by default, gated by the standard allowlist.** Opt-in was built
   first and reversed. `toolIds` is not additive - a non-empty list restricts to exactly
   that list, and the isolation strategy applies it to the workspace tools too - so the
   only way to grant the tool also stripped `read`, `write`, `edit` and `shell` from the
   agent. And the marginal exposure is small next to `ShellTool`, which is already in the
   default set: anyone who can inject into an agent's context has a shell already. What
   carries the weight is that no credential reaches the payload, which holds either way.
