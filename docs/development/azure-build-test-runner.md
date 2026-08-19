# Azure build and test runner

BotNexus validates worktrees remotely on an Azure Container Apps Job, and **remote is the default gate** (#2158). The runner captures the worktree as it exists-including staged, unstaged, and untracked files-without requiring a commit or push.

## Security model

The workflow does not use connection strings, storage keys, registry passwords, or SAS tokens.

- The signed-in Azure CLI user uploads source and downloads artifacts with Microsoft Entra authentication.
- The Container Apps Job uses a user-assigned managed identity to pull its image and access Blob Storage.
- ACR admin credentials and anonymous pulls are disabled.
- Storage shared-key authentication and public blob access are disabled.
- Source and result blobs are deleted after a completed run unless `-KeepRemoteArtifacts` is specified.

The storage and registry endpoints remain publicly routable but require Entra authentication. This permits the developer workstation and Container Apps consumption environment to access them without maintaining private networking infrastructure.

## Run validation

Authenticate with Azure CLI and configure your deployment through environment variables. Do not commit subscription-specific values:

```powershell
az login
$env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID = '<subscription-id>'
$env:BOTNEXUS_BUILDTEST_RESOURCE_GROUP = '<resource-group>'
$env:BOTNEXUS_BUILDTEST_STORAGE_ACCOUNT = '<storage-account>'
$env:BOTNEXUS_BUILDTEST_JOB_NAME = '<container-apps-job>'
$env:BOTNEXUS_VALIDATION_MODE = 'remote'
./scripts/repo/Validate-PreCommit.ps1
```

Set these as user-level environment variables when the runner should survive shell restarts and reboots. On Windows, use `[Environment]::SetEnvironmentVariable('<name>', '<value>', 'User')`, then open a new shell. Explicit script parameters remain available for one-off overrides.

The standard hook command always uses strict mode. When `BOTNEXUS_VALIDATION_MODE=remote`, it accepts an exact-content receipt or invokes Azure. Use the lower-level runner to select a diagnostic mode:

Modes:

```powershell
# Default authoritative gate: full build, impacted/architecture/scenario, and strict Playwright
./scripts/repo/Invoke-AzureBuildTest.ps1 -Mode strict

# Full solution build plus impacted, architecture, and scenario tests
./scripts/repo/Invoke-AzureBuildTest.ps1 -Mode impacted

# Full solution test suite
./scripts/repo/Invoke-AzureBuildTest.ps1 -Mode full

# Integration E2E suite with Playwright Chromium available
./scripts/repo/Invoke-AzureBuildTest.ps1 -Mode playwright
```

Results are downloaded to `artifacts/azure-buildtest/<run-id>/`. The script returns a failing exit status when the Azure execution or test process fails.

### Phase timings

Each run records how long every phase took, in `runner-timing.log` and in the `timings` object inside `result.json` (#2889):

```
source-download        12.40s  ok
payload-extract         8.10s  ok
restore                63.20s  ok
tool-restore            4.80s  ok
build                 141.60s  ok
test                  502.30s  ok
artifact-upload         6.90s  ok
```

Without this the only derivable number was total wall clock, so a twelve-minute run might have been three minutes of restore plus nine of test, or seven plus five - and those call for entirely different fixes. Any proposed change to gate performance should quote a measured before and after from this artifact rather than argue from structure.

Three properties are deliberate:

- **It is an artifact, not console output.** Runner stdout is not uploaded and `az containerapp job logs show` hangs against this environment, so a diagnostic that only reaches the console cannot be read afterwards.
- **It cannot fail a run.** Every timing write is best-effort and swallows its own errors. A measurement bug turning a green suite red would be worse than having no measurement.
- **A phase that did not run is marked `skipped`, never `0.00s`.** A phase that was absent and a phase that was genuinely instant are different findings.

Because artifacts are deleted after each run, pass `-KeepRemoteArtifacts` when the timings are the point of the run.

### Timeouts produce evidence, not an empty directory

The Container Apps replica timeout is a **hard kill**: when it expires the replica is destroyed, the entrypoint's `finally` block never executes, and the artifact upload that lives in that block never happens. Before #3305 a run that overran therefore produced an **empty** artifact directory — no `result.json`, no TRX, no timing log. That outcome cannot distinguish a genuine hang from a suite that is merely slow, and it attributes the cost to nothing at all. Two measured `-Mode full` runs on one worktree died exactly that way at 20.1 minutes against a 20-minute budget.

The runner now keeps **its own deadline, set strictly inside the platform budget**, and ends the run on a path it controls:

- The client passes the budget through `REPLICA_TIMEOUT_SECONDS`, derived from `-ReplicaTimeoutMinutes`, so the runner's deadline cannot drift from `replicaTimeout` in `main.bicep`.
- `Get-RunnerDeadlineSeconds` subtracts a 90-second reserve. That reserve is what pays for writing `result.json` and completing the upload; landing exactly on the platform budget would reproduce the original defect.
- The test phase runs through `Invoke-BoundedProcess` rather than a blocking `& dotnet test | Tee-Object` pipeline. A pipeline returns only when the child exits, so there is no instant at which the runner could notice it is about to be killed. The child is killed with its whole process tree, because testhost processes outlive their parent and a survivor would keep writing into the results directory during the upload.
- `result.json` gains a `timeout` object — `null` on an ordinary run — carrying the elapsed time, the deadline, the assemblies that did report, and the projects that did not.

The attribution is derived from the TRX that exist **at the moment of the deadline**, not from filenames: `dotnet test` writes every project's TRX into one directory under the same prefix, so the names carry no project identity. The assembly is read out of each row's `storage` attribute by text scan rather than an XML parse, because a TRX truncated by a kill is frequently not well-formed and refusing to read it would discard the only evidence the run produced.

Two properties are deliberate:

- **A filtered run never accuses a project it was not going to run.** `core` excludes the browser/E2E projects, so their absence from the results is by design. A confidently wrong attribution is worse than none.
- **An empty outstanding set is stated, not filled in.** "Every project reported and the phase still overran" is a real and different finding — the overrun is outside test execution — and inventing a culprit would hide it.

`infra/buildtest/runner/tests/RunnerTimeout.Tests.ps1` pins all of this, including the truncated-TRX case and the sensitivity of the derivation to the budget; `RunnerDeadlineConsistencyTests` fails the build if the budget stops being passed through or the bound is removed.

**The budget itself was deliberately not raised.** A larger number would hide the defect rather than remove it: the failure mode was "killed, no evidence", not "killed too early". Whether `-Mode full` genuinely needs more than 20 minutes is a separate question, answerable only once a timed-out run produces attribution — which is what this change delivers.

A successful `strict`, `impacted`, or `full` run writes a receipt under the worktree's Git metadata. The receipt records a SHA-256 fingerprint over the current HEAD, resolved base commit, and exact Git tree containing staged, unstaged, and untracked files. `Validate-PreCommit.ps1` recalculates that fingerprint when it is invoked. It skips redundant validation only when the receipt matches exactly; any content or base-ref change invalidates it and starts a new remote run. Note that no git hook consumes this receipt: #2841 removed the pre-commit hook, and `scripts/repo/install-hooks.ps1` activates only the `pre-push` `core.bare` guard (#1602). The client refuses to issue a strict receipt unless the downloaded artifacts include `playwright.log`; this fails safely when an older deployed runner treats the mode as impacted-only. Impacted, full, and Playwright-only receipts remain useful diagnostic evidence but do not bypass strict validation.

**Remote is the default and local is opt-in (#2158).** With nothing configured, `Resolve-BotNexusValidationMode` returns `remote`. Local validation spawns real gateway processes on the development host; when their parent dies the children survive, and because every gateway opens the shared cron store they claim scheduled jobs belonging to the live gateway and fail them. On 2026-08-06 three such orphans - two of them 30+ hours old - starved the live gateway until the portal would not load. To choose local deliberately, set `BOTNEXUS_VALIDATION_MODE=local` at process, user, or machine scope (process scope wins), pass `-ValidationMode local`, or use the `-LocalFallback` / `BOTNEXUS_VALIDATION_LOCAL_FALLBACK=1` aliases.

Deployment-specific settings come from the environment:

| Setting | Environment variable |
|---|---|
| Subscription | `BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID` |
| Resource group | `BOTNEXUS_BUILDTEST_RESOURCE_GROUP` |
| Region | `BOTNEXUS_BUILDTEST_LOCATION` |
| Storage account | `BOTNEXUS_BUILDTEST_STORAGE_ACCOUNT` |
| Job | `BOTNEXUS_BUILDTEST_JOB_NAME` |

The deployed job uses 4 vCPU, 8 GiB, and a two-hour timeout. All identifiers can also be supplied through script parameters.

## Provision or update infrastructure

The Bicep template and runner image live under `infra/buildtest/`. Deployment requires Owner or equivalent resource and RBAC permissions:

```powershell
$env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID = '<subscription-id>'
$env:BOTNEXUS_BUILDTEST_RESOURCE_GROUP = '<resource-group>'
$env:BOTNEXUS_BUILDTEST_LOCATION = '<azure-region>'
./infra/buildtest/Deploy-BuildTestInfrastructure.ps1
```

The script uses the current Azure CLI user as the operator identity and builds the runner image through ACR Tasks. The subscription used for the shared deployment does not permit Basic or Standard ACR, so the template uses Premium. Container Apps compute scales to zero, but the registry has a standing charge.

### Runner image tags are content-addressed

Do not pick a version number for the runner image. The tag is **derived from a SHA-256 over the contents of `infra/buildtest/runner/`** and looks like `src-1bc35f62d232` (#2900).

This is a correctness guard, not a convenience. **ACR tags are mutable**: `az acr build` against an existing tag republishes over it with exit 0 and no warning. On 2026-08-09 a hand-picked "next" version destroyed the existing `0.1.12` image, because the default in the deploy script read `0.1.11` while the deployed job was actually running `0.1.15` and `main.bicep` claimed `0.1.4` — three sources of truth, all disagreeing.

Content addressing removes the failure mode rather than documenting it:

- Identical content always yields the same tag, so a redundant deploy is a **skipped no-op** instead of an overwrite.
- Different content always yields a different tag, so a change can never land on an existing one.
- There is no number left to drift, and `main.bicep` now requires the parameter instead of defaulting.

Passing `-RunnerImageTag` explicitly is still allowed for pinning a historical image during a rollback, but the script **refuses to publish over a tag that already exists**. Deleting a tag has to be a deliberate, separate act.

Files under `infra/buildtest/runner/tests/` are excluded from the hash — they never enter the image, so editing them must not force a rebuild. `infra/buildtest/tests/DeployTagGuard.Tests.ps1` pins all of these properties and is mutation-verified.

## Snapshot format

The local script uploads a small payload containing:

1. a Git bundle with repository history and refs, needed by `dotnet-affected`; and
2. a tar overlay containing all tracked and untracked, non-ignored worktree files.

The runner clones the bundle, applies the overlay, commits a temporary snapshot, and then invokes the repository's canonical build and test scripts. The temporary commit exists only inside the ephemeral job replica.

## Maintenance and PR automation

All agents, maintenance jobs, PR workflows, and human development flows call `scripts/repo/Validate-PreCommit.ps1` once for the final candidate. Record the selected mode and gate evidence in Merge Notes. Hand-run `dotnet build` freely - compiling the projects you changed before spending a remote gate is expected practice, and a build starts no test host or gateway process - but a build is not an extra pre-push gate and does not substitute for one. Do not run `dotnet test` or `test-impacted.ps1` on the development host at all: the test host boots gateway processes that outlive their parent, and that is the leak path #2158 exists to close. Remote mode retains exact-content receipt reuse; local mode, when explicitly requested, is globally serialized across BotNexus worktrees.

**Do not run this gate for a documentation-only change.** When a change touches nothing but `*.md`, `docs/**`, or `mkdocs.yml`, the remote suite exercises no part of the diff: it costs a container run and roughly twelve minutes to prove something unrelated. Build the documentation instead with `npm run docs:build` (about twenty seconds, and it exits non-zero on a dead link). `ci-build-test.yml` takes the same position already, listing `docs/**` and `**/*.md` under `paths-ignore` so a docs-only PR never triggers the test workflow. A change that touches documentation *and* code is a code change and takes the full gate.
