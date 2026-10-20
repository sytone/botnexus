# Updater compatibility contract

For contributors changing lifecycle update behavior or publishing the CLI. This contract prevents an unsupported, already-loaded updater from applying a selected source target. It does not detect all breaking code changes automatically.

## Rollout boundary

The first contract-aware **released** CLI is 0.50.2 (the next patch after the current source package 0.50.1). The release workflow computes the package version from tags; do not manually bump the global source version for this rollout. Release validation refuses versions below 0.50.2 or below the source-declared minimum. A development build still labeled 0.50.1 cannot satisfy that minimum.

Pre-contract binaries, including the incident's 0.46.0 global tool, cannot enforce a check added to newer source. Operators must explicitly update their global `BotNexus.Cli` tool before using lifecycle updates. This is not retroactive protection for those binaries.

## Independent declarations

- Root `updater-compatibility.json` describes the **target**: `schemaVersion`, `minimumUpdaterVersion`, `requiredUpdaterCapabilities`, and `providedTargetCapabilities`.
- `src/gateway/BotNexus.Cli/updater-support.json` describes the **running updater**: `supportedContractSchemas`, `supportedUpdaterCapabilities`, and `requiredTargetCapabilities`. This is embedded into the CLI package, not read from the deployment checkout at runtime.
- Package version is a release identity and minimum installation boundary, not the compatibility schema or a substitute for behavior checks. A newer package is not automatically safe for every older target.

Both declarations require their exact, independent property sets: unknown properties, properties from the other declaration, duplicate keys, missing values, scalar capability lists, and malformed schema types are rejected. Schema 1 requires nonempty, unique, case-sensitive capability arrays of strings; supported schemas are a nonempty, unique array of positive integers. Malformed embedded CLI support fails closed rather than throwing.

Initial behaviors are `planned-shutdown-v1` (authenticated planned host shutdown with bounded escalation and process identity checks), `readiness-v1` (bounded readiness with alive-versus-exited reporting), and updater-only `immutable-target-preflight-v1`.

Stable, exact-version and `--latest` updates first resolve a commit. The updater reads the root contract with `git show <resolved-commit>:updater-compatibility.json`, never from HEAD, the working tree, or a movable branch name. Fetching is preparatory; compatibility refusal occurs before dirty-tree handling (including stash/force), checkout, shutdown, build, deployment, restart, or the no-op fast path. Missing/unreadable metadata, legacy targets without a contract, malformed contracts, unsupported schemas, missing updater behavior, and missing target behavior fail closed with exit **4**. Cancellation remains **130**, resolution errors **2**, dirty-tree refusal **3**. `update check` remains an availability check, not authorization to perform an update.

Every lifecycle-update caller must supply a release request. `ExecuteAsync` rejects a null request with exit **4** before any update mutation; it never falls back to an unchecked pull. Lifecycle unit fixtures explicitly provide compatible immutable release metadata. Direct pull-step diagnostics remain isolated from the full update entry point.

## Changing lifecycle behavior

1. Decide whether an existing updater can safely operate the changed target **and** whether the changed updater can safely operate old targets. Review shutdown endpoints/authentication, escalation, identity checks, deployment layout and readiness semantics.
2. For additive, compatible fixes, keep the same behavior token. Ordinary package changes do not require a schema or capability bump.
3. For incompatible behavior, introduce a new capability token. Update required/provided target declarations and the CLI's supported/required declarations deliberately. Raise `minimumUpdaterVersion` to the first release implementing the required updater behavior. A changed document format needs a new schema and an explicit parser/support decision; unknown schemas are refused.
4. Add cross-version scenarios proving both accepted combinations and rejected combinations before every mutation. Do not merely add a token to make the policy check pass: implement and test the behavior it promises.
5. Run focused scenarios and the normal remote repository gate before publication. The release workflow runs `scripts/repo/Test-UpdaterCompatibility.ps1 -ReleaseVersion <computed-version>` before committing or publishing release artifacts. This detects inconsistent schema/capability declarations and missing matching CLI support; scenario tests check the packaged descriptor and both compatibility directions. Neither check can infer semantic breaking changes in arbitrary source code. Maintainer review of steps 1–4 is required.

## Refusal and migration

The diagnostic names the running package/revision and immutable target package/revision, explains the failed boundary, and says no lifecycle mutation was performed. It does not claim a gateway is running without observing it.

If a compatible package has been published and public NuGet is reachable:

```text
dotnet tool update -g BotNexus.Cli
```

For local packaging, the diagnostic renders the resolved source path with separate PowerShell and bash quoting. Navigate to a checkout containing a compatible released CLI, then run:

```text
dotnet pack src/gateway/BotNexus.Cli -c Release -o ./artifacts
dotnet tool update -g BotNexus.Cli --source ./artifacts
```

`--source` replaces configured feeds for tool resolution; do not replace it with `--add-source`. Local packaging still needs restored dependencies or reachable dependency feeds. The current deployment checkout may be older than the selected target; do not assume packing it produces the required tool. Neither channel is guaranteed to contain a compatible package yet. Verify the installed tool, then rerun the original update action with the same options. Pulling source does not replace the CLI assembly already executing.
