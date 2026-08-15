# GitHub Extension

The GitHub extension (`botnexus-github`) owns a **platform-scoped GitHub App installation
credential**. It exists so that GitHub operations do not require an agent to mint a token into its
own shell context.

This is the foundation slice: it ships the credential only. There are **no agent tools** in this
extension yet.

## Why the credential is platform-owned

Before this extension, every GitHub operation re-minted an installation token and assigned it to
`GH_TOKEN` in agent-visible shell context. That has three problems:

1. **The agent can read the secret.** Anything in the environment block is visible to the agent and
   inherited by every child process it spawns.
2. **It is re-minted constantly.** An environment variable set inside one shell invocation does not
   survive to the next, so the token is regenerated roughly once per two operations.
3. **It leaks by default.** Any tool that dumps the environment, or any error path that echoes a
   command line, surfaces the token.

The provider closes all three: the token lives in gateway process memory, is reused until it
expires, and is never returned to a caller.

## Design

| Type | Role |
| --- | --- |
| `IGitHubCredentialProvider` | Public seam. Its only operation attaches the credential to an outbound `HttpRequestMessage`. It has **no** token-returning member. |
| `CachedGitHubCredentialProvider` | Caches the installation token until expiry and refreshes transparently. Expiry is evaluated against an injected `TimeProvider`. |
| `IGitHubInstallationTokenSource` | Mints a fresh token. Swappable for tests. |
| `HttpGitHubInstallationTokenSource` | Signs an App JWT with the configured PEM key and exchanges it for an installation token. |
| `GitHubServiceContributor` | Registers the above through the `IServiceContributor` seam. |

Refresh happens inside a single `AuthenticateAsync` call, so an expired token costs one extra HTTP
round trip and **no agent turn**.

### What the provider never does

- It never returns the token to a caller.
- It never writes the token to a log line. Log statements carry the expiry instant only, and
  `GitHubInstallationToken.ToString()` renders `[redacted]` so an accidental interpolation cannot
  leak it.
- It never assigns the token to an environment variable. An architecture fitness function
  (`GitHubCredentialEnvironmentFenceArchitectureTests`) fails the build if extension source ever
  calls `Environment.SetEnvironmentVariable` or writes into a process environment block.
- Error messages carry a status code, never a GitHub response body — token endpoint responses can
  echo credential material.

## Configuration

Bound from the `GitHub` configuration section:

| Key | Default | Description |
| --- | --- | --- |
| `appId` | — | GitHub App id, used as the JWT issuer. |
| `installationId` | — | Installation whose scoped token is minted. |
| `privateKeyPath` | — | Path to the GitHub App PEM private key. |
| `apiBaseAddress` | `https://api.github.com/` | Override for GitHub Enterprise Server. |
| `expirySkewSeconds` | `60` | Refresh this many seconds before the reported expiry, so a token cannot expire mid-flight. |

Nothing contacts GitHub until the first authenticated request, so an unconfigured host pays no
startup cost; it fails at first use with a `GitHubCredentialException` naming the missing setting.
