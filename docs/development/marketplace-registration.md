
## Marketplace registration (configuration only)

For operators registering a Git repository as a future marketplace source, the CLI exposes
registration CRUD through the gateway. The `botnexus-plugins-api` extension must be loaded and
the CLI must be able to authenticate to the gateway. Registration does not need Git installed:
it does not clone, fetch, install, trust, or execute repository content.

```shell
botnexus plugin marketplace add community https://example.test/team/catalog.git --reference main
botnexus plugin marketplace list
botnexus plugin marketplace get community
botnexus plugin marketplace update community https://example.test/team/catalog.git --reference stable
botnexus plugin marketplace disable community
botnexus plugin marketplace enable community
botnexus plugin marketplace remove community
```

`community` is an example local identity, not a built-in marketplace. Replace the example URL
with your repository URL. `list` lists registrations, **not catalog entries**. `update` replaces
the source and reference; omitting `--reference` restores the repository's default branch.
Additions are enabled by default. Inspect `get` to check the stored source, reference, and enabled
state. A duplicate add fails; updating, enabling, disabling, getting, or removing a missing ID
also fails.

IDs must contain lowercase ASCII letters or digits in segments separated by single hyphens,
with at most 128 characters. URLs must be absolute `https`, `http`, or `ssh` repository URLs
with a host and repository path. Prefer HTTPS over unencrypted HTTP. Credentials, user information
(including `git@` in SSH URLs), query strings, fragments, whitespace, and control characters are
rejected rather than stored or echoed in validation errors. SCP-style URLs, local paths, and
`file:` URLs are not supported. For SSH use a credential-free URL such as
`ssh://example.test/team/catalog.git`; any later Git authentication must use external credential
helpers or SSH configuration. References may be omitted, or specify a branch, tag, or commit;
option-prefixed values and unsafe Git ref characters are rejected.

Registrations are machine-global within the configured BotNexus home, never scoped to an agent
or conversation. The canonical configuration path is
`world.extensions.botnexus-plugins-api.marketplaces.<id>`. The shared registry uses
`ConfigWriterFactory` / `PlatformConfigWriter` for both reads and mutations, so an
existing populated canonical SQLite store wins over an absent, corrupt, or conflicting JSON projection.
It does not introduce a separate registry file or backend switch. Changes preserve other
registrations and sibling extension settings. Do not edit a generated projection to manage them.

**Disable and remove affect registration metadata only. They do not uninstall installed plugins,
delete plugin files, revoke existing trust, or change installed-plugin update preferences.**
Disable preserves the registration for later re-enabling; remove deletes its metadata.
Discovery, catalog listing, and marketplace initialization are not implemented by this stage.
No marketplace UI is provided by this stage.

### Registration API for integrators

Use the gateway's normal authentication policy with an unrestricted operator identity. All registry
routes require an authenticated caller with an empty `AllowedAgents` list and reject satellite
identities. Agent-restricted callers receive `403`, including administrative callers with a nonempty
agent allow-list and requests supplying `?agent=allowed` or `?agentId=allowed`. The extension manifest
declares the `world` configuration scope; registry settings are not supported in agent or gateway scope.
The existing extension-owned contributor maps:

| Method | Path | Body / success |
| --- | --- | --- |
| `GET` | `/api/plugins/marketplaces` | Array of registrations, `200` |
| `GET` | `/api/plugins/marketplaces/{id}` | One registration, `200` |
| `POST` | `/api/plugins/marketplaces` | `{ "id": "community", "source": "https://example.test/team/catalog.git", "reference": "main" }`, `201` |
| `PUT` | `/api/plugins/marketplaces/{id}` | `{ "source": "https://example.test/team/catalog.git", "reference": null }`, `200` |
| `POST` | `/api/plugins/marketplaces/{id}/enable` | No body, `200` |
| `POST` | `/api/plugins/marketplaces/{id}/disable` | No body, `200` |
| `DELETE` | `/api/plugins/marketplaces/{id}` | No body, `204` |

Read and mutation responses (except delete) contain `id`, `source`, nullable `reference`, and
`enabled`. Invalid input returns `400`, missing registrations return `404`, and duplicate adds
return `409`; errors use a typed `{ "error": "..." }` response. Malformed stored registry configuration
also returns `409` with a configuration-path diagnostic, rather than a partial list or an unstructured
server error. Reads and mutations fail closed for a null collection, null entry, missing or unsafe
source, invalid reference, or non-boolean enabled state. Invalid registrations are never silently
activated, and diagnostics do not echo source credentials. Reads and mutations accept the same
case-insensitive setting names and reject ambiguous duplicates such as `enabled` plus `Enabled`.
Correct malformed settings through the
canonical configuration tools before retrying registry operations.
A successful registration is
not proof that a repository exists or that authentication will succeed: this stage performs no
network request. The static marketplace namespace takes precedence over installed-plugin routes,
including reserved collection writes, to avoid interpreting a registry request as plugin removal.
