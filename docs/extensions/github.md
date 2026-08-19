# GitHub Extension

The GitHub extension (`botnexus-github`) provides **structured GitHub agent tools** over a
**platform-scoped GitHub App installation credential**. It exists so that GitHub operations do not
require an agent to mint a token into its own shell context, quote JSON through PowerShell, or
re-parse command output.

Across the live session store, 16,241 of 56,401 shell invocations (28.8%) were GitHub-related, and
4,638 of those existed only to regenerate an auth token. This extension replaces that ceremony.

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

### Per-agent acting identity

A host that needs more than one acting identity declares them by name and maps agents onto them.
Multiple identities are a **configuration fact**, never a mutation of the ambient `gh` CLI account:

```json
{
  "GitHub": {
    "identities": {
      "farnsworth-app": {
        "appId": "4039269",
        "installationId": "12345678",
        "privateKeyPath": "/secrets/farnsworth-bot.pem"
      },
      "nova-app": {
        "appId": "4039270",
        "installationId": "87654321",
        "privateKeyPath": "/secrets/nova-bot.pem"
      }
    },
    "agentIdentities": {
      "farnsworth": "farnsworth-app",
      "nova": "nova-app"
    }
  }
}
```

| Key | Description |
| --- | --- |
| `identities:<name>` | A named GitHub App identity profile. Requires `appId`, `installationId` and `privateKeyPath`. |
| `agentIdentities:<agentId>` | The profile name the given agent acts as. |

Each profile gets its own credential provider and its own token cache, so two agents running
concurrently in one process cannot re-author one another's writes. Ambient `gh auth switch` state is
process-global and does exactly that, which is why it is a red line - and why the mechanism here has
no switch operation at all.

**Resolution fails closed.** An agent that is not mapped, names a profile that does not exist, or
names an incomplete profile gets a `GitHubCredentialException` whose message contains the
**configuration key** that is missing - for example
`GitHub:identities:nova-app:privateKeyPath`. It deliberately never falls back to another identity:
a fallback would succeed under the wrong authorship, which cannot be undone once the write lands.
The message names keys only, never configured values.

Hosts that declare neither `identities` nor `agentIdentities` keep the single flat identity bound
from `appId`/`installationId`/`privateKeyPath` and are unaffected.

## Agent tools

Tools are contributed **per agent** by `GitHubToolsContributor`. An agent with no
`botnexus-github` extension configuration receives no GitHub tools at all, rather than tools that
fail when called - a tool the model can see but never use is a tax paid on every prompt.

| Tool | Operation |
| --- | --- |
| `github_issue_get` | Read one issue, optionally with its comments. |
| `github_issue_list` | List issues with explicit pagination and an optional label filter. |
| `github_issue_comment` | Post a comment on an issue **or** a pull request. |
| `github_pr_get` | Read one pull request, including merge state and diff statistics. |
| `github_pr_list` | List pull requests with explicit pagination. |
| `github_pr_checks` | Read the CI check runs for a pull request, with a rollup summary. |
| `github_pr_diff` | Read a pull request's changed files, optionally with unified patch hunks. |
| `github_workflow_runs` | List GitHub Actions workflow runs, filterable by workflow, branch and status. |
| `github_api` | Escape hatch: any REST path with the managed credential. |

### No tool takes a credential

No tool schema accepts a token, credential, or identity argument. The acting identity is resolved
from the agent's configuration when tools are contributed, and the credential is attached inside
`HttpGitHubApiClient` - a single place, below every tool. A test enumerates every registered schema
and fails if such a parameter is ever added.

The consequence is that `gh auth switch` becomes unnecessary *and* unreachable from a tool call:
there is no ambient CLI account for a tool to change. The prohibition moves from convention to
mechanism.

### Results are structured

Every tool returns a JSON object, never command output. A caller reads `state` or `number` as a
field rather than running a `jq` filter over text - the filter that, under PowerShell's
nested-quoting rules, previously forced throwaway `tmp/*.ps1` files just to escape quotes.

Failures are structured too:

```json
{ "tool": "github_issue_get", "repository": "owner/repo", "ok": false, "status": 404, "error": "Not Found" }
```

The status is a field, so an agent deciding whether to retry does not have to pattern-match stderr.

### Pagination is explicit, never silent

List results carry `page`, `perPage`, `count`, `hasMore`, and `perPageClamped`. A request for a page
larger than `maxPageSize` is clamped **and told so**, because silently returning fewer rows would let
a caller conclude the repository is small when it merely hit a bound.

`hasMore` is derived from a full page, so it can over-report by one page at an exact boundary. That
is the correct direction to be wrong in: claiming completeness you do not have is the defect being
avoided.

`github_workflow_runs` is the exception: the Actions API returns a real `total_count`, so its
`hasMore` is *computed* from the total rather than inferred from a full page. It reports `totalCount`
alongside the page bounds.

### Reading CI state

`github_pr_checks` resolves the pull request's head commit SHA and then reads the check runs for it.
Both calls happen inside one tool because check runs are addressed by SHA, not by pull request
number - the shell equivalent was two `gh` invocations plus a `--jq` filter to thread the SHA between
them.

The result carries a derived rollup so an agent does not count conclusions itself:

```json
{
  "tool": "github_pr_checks",
  "number": 3300,
  "headSha": "a1b2c3d…",
  "summary": { "total": 3, "succeeded": 1, "failed": 1, "pending": 1, "allCompleted": false },
  "checkRuns": [ { "name": "build", "status": "completed", "conclusion": "success" } ]
}
```

`allCompleted` exists because "green" is not "no failures": a run set that has not finished also has
zero failures. An in-flight run keeps `conclusion: null` rather than a substituted `"pending"`, so a
red pull request can never be read as merely slow.

### Diffs are file records, not diff text

`github_pr_diff` returns the pull request's changed files as records - `path`, `status`, `additions`,
`deletions` - rather than GitHub's raw `.diff` media type. A raw diff is exactly the command text
this extension exists to stop returning: answering "which files changed" from it means parsing file
boundaries out of a string.

The unified patch hunk is available per file, but only when `includePatch` is set. Patch text is
unbounded, and a large pull request would otherwise spend an entire transcript budget on hunks the
caller never asked for.

### Comments use the REST path

`github_issue_comment` posts to `POST /repos/{owner}/{repo}/issues/{number}/comments`. The GraphQL
`addComment` mutation fails under an Enterprise Managed User account, and that workaround was
previously rediscovered per agent, per session. There is no GraphQL code path in this extension, so
the failure mode is unreachable rather than merely avoided.

### The escape hatch

`github_api` exists so the modelled surface can stay small. Without it, the first unmodelled endpoint
would send an agent back to shelling out with a hand-minted token, restoring every cost this
extension removes for the sake of one missing operation. It still takes no credential argument, and
it accepts relative paths only - an absolute URL is rejected.

## Per-agent configuration

Set under the agent's `extensionConfig` as `botnexus-github`:

| Key | Default | Description |
| --- | --- | --- |
| `defaultRepository` | - | `owner/repo` used when a call omits `repository`. |
| `identity` | - | Human-readable label for the acting identity, reported in write results. |
| `defaultPageSize` | `30` | Page size when a list call does not specify one. |
| `maxPageSize` | `100` | Upper bound on a page; a larger request is clamped and the clamp reported. |

```json
{
  "extensionConfig": {
    "botnexus-github": {
      "defaultRepository": "owner/repo",
      "identity": "my-agent[bot]"
    }
  }
}
```

## What stays in the shell

Local `git` operations - clone, branch, worktree, commit, push, rebase - are filesystem operations
against a working tree, not GitHub API calls. They are deliberately out of scope.

Policy-linting skill scripts such as `New-BotNexusPr.ps1` and `New-BotNexusIssue.ps1` keep their
conventional-commit and issue-schema validation. They are consumers of this extension, not
casualties of it: the linting is the value, and only the auth and quoting handling goes away.
