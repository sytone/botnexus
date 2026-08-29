# File-Backed Secrets Directory

A `secrets/` directory under the BotNexus home holds arbitrary, user-named secrets that the platform
configuration schema knows nothing about: a third-party API key a script needs, a token for a service
BotNexus has never heard of.

**Each file's name is the key. Each file's content is the value.** There is no wrapper format, no
encoding layer, and no index.

## Why this exists alongside config secrets

BotNexus already handles secrets that the *schema declares* — a provider API key, a channel bot
token. Those live inside `config.json`, are discovered by reflection over
`[ConfigField(Secret = true)]`, and are shown to the UI as `***`. That machinery is sound, but it
only works for a field the schema has an entry for.

An arbitrary secret has no schema property to annotate, so there is nothing to redact, so there is
nowhere in the config document to put it. The usual workaround is an environment variable set outside
the platform — invisible to the UI and undiscoverable by the operator.

This directory answers exactly that case. The key space is open, no schema change is needed to add a
key, and the unit of access control is a single file.

| | Schema-declared secret | File-backed secret |
|---|---|---|
| Where it lives | inside `config.json` | its own file under `secrets/` |
| Key space | fixed by the schema | open, operator-chosen |
| Read-back | yes — `***` round-trips to the real value on save | **never** |
| In config backups / revision digest | yes | no |

## Location

The directory is `secrets/` under the BotNexus data directory, alongside `agents/`:

```
~/.botnexus/secrets/
```

It honours `BOTNEXUS_HOME` and `BOTNEXUS_DATA_DIR` like every other runtime directory. Resolve it
through `BotNexusHome.SecretsPath` rather than composing the path by hand — a hand-built path stops
following a configured data-dir override and silently splits the store across two directories.

## Write-only by design

The API and the UI can **add**, **overwrite** and **delete** a secret. Neither can **read** one back.

This is a stronger guarantee than the `***` placeholder scheme used for config secrets, which must be
able to restore a real value when the placeholder round-trips on save and therefore keeps a read
channel permanently open. Here there is no such channel:

- Listing returns the key name, creation time, modification time and size — and nothing derived from
  the file's content. Not the value, not a prefix, not a masked form (a mask leaks the length), and
  not a hash (a hash is an offline-guessable oracle for a short secret).
- Overwriting requires the full new value. The UI never pre-populates or partially masks the existing
  one, because it cannot read it.
- There is no read-value endpoint, and an architecture test asserts by reflection that no controller
  action returns secret content.

**A forgotten value is not recoverable through BotNexus.** That is the intended trade-off, and the
escape hatch below is the whole of the recovery story.

## Recovering a value from the host

A secret is a plain file, so anyone who can read the file can read the value:

```bash
cat ~/.botnexus/secrets/MY_API_KEY
```

```powershell
Get-Content $env:USERPROFILE\.botnexus\secrets\MY_API_KEY
```

This requires filesystem access on the machine running the gateway, which is precisely the access
level the permissions below are set to require.

## Key names

Keys must match `^[A-Za-z0-9._-]{1,128}$`. `.` and `..` are rejected outright.

The charset is an allowlist, not a blocklist, and it excludes `/`, `\` and `:` by construction — so a
directory separator, a traversal segment, an absolute path and a drive-qualified path are not merely
filtered but unrepresentable. A rejected key writes nothing, anywhere.

## Permissions

Every secret file is narrowed to owner-only on write through `SecureFilePermissions.RestrictToOwner`,
the single seam every secret-bearing file in BotNexus goes through:

- **Linux/macOS** — mode `0600`, removing the group and other read bits that a default `umask 022`
  leaves on.
- **Windows** — inheritance is broken and an explicit DACL grants FullControl to the file's owner,
  `SYSTEM` and `Administrators`.

An architecture fence fails the build if the write path stops calling it, so a future refactor cannot
quietly ship a world-readable secret store.

## Not encrypted at rest

Secrets are protected by filesystem permissions, exactly as `config.json` is. They are **not**
encrypted. Claiming encryption without a key-management story would be a false assurance: the key
would have to live next to the data.

An operator with root, or with the gateway user's credentials, can read every stored secret. If that
is not an acceptable threat model for a particular value, that value belongs in a dedicated secret
manager rather than in this directory.

## Excluded from the config document

Secrets are files, not configuration. They do not appear in `GET /api/config`, they are not copied
into the config backup set, and they are not part of the config revision digest. Nothing about a
secret changes the config document, so nothing about a secret can leak through it.

## API

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/secrets` | List keys with created, modified and size. Never returns a value. |
| `PUT` | `/api/secrets/{key}` | Create or overwrite with the complete new value. |
| `DELETE` | `/api/secrets/{key}` | Delete the file. The key stops being listed immediately. |

Access is mediated by this endpoint rather than by agents reading the directory directly. Today the
endpoint is unscoped; having the seam is what makes per-agent key scoping a later change to one
component instead of an audit of every filesystem call in the tree.
