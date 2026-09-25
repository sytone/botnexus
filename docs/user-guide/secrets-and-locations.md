# Servers, credentials and agents

How to tell BotNexus about a server, where to keep the credential for it, and what an agent can
and cannot do with either.

## The rule this is all built around

**An agent that can read a credential can be talked into disclosing it.**

Injected text — a fetched web page, a file in a workspace, an inbound channel message — reaches an
agent through the same channel as your instructions. Once a password is in the context, "manage my
server" and "print your server password" are not distinguishable to the model. No system prompt
fixes that.

So the arrangement is: the agent learns *that* a target exists and *where* it is, and a tool does
the work holding a credential the agent never sees.

Everything below follows from that one sentence.

## Register a server

Named servers live under `gateway.locations` in `config.json`, keyed by the name you refer to them
by:

```json
"gateway": {
  "locations": {
    "proxmox-main": {
      "type": "remote-node",
      "endpoint": "https://pve.example.lan:8006",
      "username": "automation@pve",
      "credentialRef": "env:PROXMOX_TOKEN",
      "verifyTls": true,
      "description": "Main hypervisor",
      "tags": ["homelab", "hypervisor"],
      "properties": { "node": "pve" }
    }
  }
}
```

`type` must be one of `filesystem`, `api`, `mcp-server`, `database`, `remote-node`. There is no
vendor-specific type — a Proxmox host is a `remote-node`, and anything vendor-specific goes in
`properties`.

You can edit this file directly, use the portal's Configuration page, or use
`botnexus locations add`. Configuration reloads live; you do not need to restart the gateway.

### `credentialRef` holds a reference, never a credential

```json
"credentialRef": "env:PROXMOX_TOKEN"        // correct — names where the credential is
"credentialRef": "hunter2"                  // rejected at validation
```

A value with no `scheme:` fails validation, naming the key:

```
gateway.locations.proxmox-main.credentialRef is not a valid credential reference.
A credential reference must be scheme:identifier (for example env:MY_TOKEN …)
```

That is deliberate. The field is a pointer, and making the wrong thing fail loudly is cheaper than
noticing later that a password is sitting in a config file.

`verifyTls` defaults to **on**. Turn it off only for a target with a self-signed certificate, and
only for that target — which is the point of it being per-location rather than global.

## Where credentials live

Four backends. They are not equivalent, and the differences are worth reading before choosing.

| Scheme | Example | Protected at rest? | Use when |
|---|---|---|---|
| `env:` | `env:PROXMOX_TOKEN` | No — readable via `/proc/<pid>/environ` as the same user | Simplest; matches how provider API keys already work |
| `file:` | `file:~/.botnexus/secrets/proxmox` | No — filesystem permissions only, enforced as `0600` | Per-target rotation; config-management friendly |
| `sqlite:` | `sqlite:proxmox` | **No** — SQLite is plaintext on disk | One artifact to back up rather than many files |
| `keyring:` | `keyring:botnexus/proxmox` | **Yes** — the OS encrypts it | A workstation with a real desktop session |

Only `keyring:` protects anything at rest. `sqlite:` is convenience, not security — it is listed
here as such rather than implied to be stronger because it involves a database.

Credentials are resolved **when they are used**, not at start-up, so rotating one does not require
restarting the gateway.

### `env:` — environment variables

Keep them in `~/.botnexus/botnexus.env`, which the start-up script sources:

```bash
echo 'PROXMOX_TOKEN=PVEAPIToken=automation@pve!bot=xxxxxxxx' >> ~/.botnexus/botnexus.env
chmod 600 ~/.botnexus/botnexus.env
```

Restart the gateway so it picks up the new variable — this is the one backend where rotation *does*
need a restart, because a process's environment is fixed once it starts.

### `file:` — one credential per file

```bash
mkdir -p ~/.botnexus/secrets
install -m 600 /dev/null ~/.botnexus/secrets/proxmox
printf '%s' 'PVEAPIToken=automation@pve!bot=xxxxxxxx' > ~/.botnexus/secrets/proxmox
```

Use `printf`, not `echo`: `echo` appends a newline, and a credential carrying a trailing `\n` fails
at the far end looking like a wrong password. BotNexus trims trailing newlines for exactly this
reason, but the habit is worth having.

A file that other users can read is **refused**, not merely warned about:

```
Could not resolve credential 'file:~/.botnexus/secrets/proxmox':
'/home/you/.botnexus/secrets/proxmox' is readable by other users.
Restrict it to its owner (chmod 600) and try again.
```

### `sqlite:` — the built-in store

```bash
botnexus secret set proxmox          # prompts without echo, or accepts a pipe
botnexus secret list                 # names only, never values
botnexus secret remove proxmox
```

The value is never taken as a command-line argument — anything on a command line lands in shell
history, in `ps` output, and in CI logs. Pipe it or type it when prompted.

There is deliberately no `botnexus secret get`. A command whose purpose is to print a credential to
a terminal is a facility for exfiltrating one.

The store lives at `~/.botnexus/secrets.sqlite` and is restricted to its owner on every write.

### `keyring:` — the OS credential store

`keyring:service/account`, or `keyring:name` for the default `botnexus` service.

**Linux** needs `libsecret-tools` and a running Secret Service daemon with an unlocked keyring —
which a headless server does not have by default:

```bash
sudo apt install libsecret-tools
secret-tool store --label='BotNexus proxmox' service botnexus account proxmox
```

**macOS** uses the built-in keychain:

```bash
security add-generic-password -s botnexus -a proxmox -w
```

**Windows is not supported.** Reading a Credential Manager entry needs a native call rather than a
command, and shipping that untested would be worse than saying so. Use `env:` or `file:` there.

When the tooling is absent you get an instruction rather than a stack trace:

```
Could not resolve credential 'keyring:botnexus/proxmox': 'secret-tool' is not installed.
Install libsecret-tools and run a Secret Service daemon, or use env: or file: instead.
```

## Let an agent discover your servers

Grant the `list_locations` tool:

```json
"agents": {
  "infra": {
    "provider": "anthropic",
    "model": "claude-sonnet-5",
    "systemPrompt": "You help manage homelab infrastructure.",
    "toolIds": ["list_locations", "read", "ls", "grep", "glob", "shell"]
  }
}
```

**`toolIds` is a restriction, not an addition.** A non-empty list means *only* those tools —
including the workspace ones. Leave it unset and the agent gets everything, `list_locations`
included; set it and you must name everything you want. This catches people out.

The agent sees names, kinds, addresses, usernames, descriptions and tags — and never a credential:

```json
[{"name":"proxmox-main","type":"remote-node","address":"https://pve.example.lan:8006",
  "username":"automation@pve","tags":["homelab","hypervisor"],
  "hasCredential":true,"verifyTls":true}]
```

`hasCredential` tells it the target is authenticated without telling it which credential or where
it lives. A `database` location shows **no address at all**, because for a database the address is
the connection string, and the connection string is the credential.

## Acting on a target — read this before you plan around it

**There is currently no tool that authenticates to a target on an agent's behalf.** Registering a
location and granting `list_locations` gets an agent as far as knowing your Proxmox host exists and
where it is. Nothing yet takes `credentialRef`, resolves it, and makes the call.

That tool is the next piece of work. Until it exists, the two ways to get an agent acting on a
server both have the same cost, and it is the cost this whole design exists to avoid:

**Option A — `shell` plus `curl`, credential in the environment.** Works today. But the token is in
the gateway's environment, the agent has a shell, and therefore the agent can read the token. Every
guarantee above is void for that agent. Reasonable for a throwaway experiment on a host you do not
care about; not something to leave wired to an inbound channel.

**Option B — an MCP server that holds the credential.** The credential lives in the MCP server's
own configuration rather than in BotNexus, and the agent calls verbs. This keeps the property —
the agent has capability without custody — at the cost of managing a second component, and of the
credential sitting outside the registry described above.

If you want the property without the second component, wait for the native tool. It resolves the
credential internally, exposes an explicit verb allow-list, and is read-only by default.

## When something does not work

| What you see | What it means |
|---|---|
| `is not a valid credential reference` | The value has no `scheme:`. You probably pasted the credential itself. |
| `environment variable 'X' is not set, or is empty` | Not in the gateway's environment. Check `botnexus.env` and restart. |
| `no file at '…'` | Path typo, or a relative path — it must be absolute or start with `~`. |
| `is readable by other users` | `chmod 600` the file. |
| `no secret store at '…'` | Nothing stored yet: `botnexus secret set <name>`. |
| `'secret-tool' is not installed` | No keyring on this host. Install it, or use `env:`/`file:`. |
| `no provider is registered for scheme 'X'` | Typo in the scheme. Valid: `env`, `file`, `sqlite`, `keyring`. |
| Agent says it has no `list_locations` tool | `toolIds` is set and does not include it — remember it is a restriction. |

A resolution failure always names the **reference**, never the value. If you ever see a credential
in a log, that is a bug worth reporting.

## What this design does not protect against

Worth stating as plainly as the guarantees:

- Anything running as the gateway user can read what the gateway can read. This is not a vault.
- `env:`, `file:` and `sqlite:` are not encrypted at rest.
- A malicious *tool* is outside the model. Tools are trusted code — that is precisely why the
  credential lives there and not in the agent's context.

The property being defended is narrower and more useful than "secrets are safe": a credential does
not enter an agent's context, so text that reaches that context cannot carry it back out.
