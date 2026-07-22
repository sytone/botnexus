# Azure build and test runner

BotNexus can validate worktrees remotely on an Azure Container Apps Job. The repository gate currently defaults to globally serialized local validation; this runner remains the supported `remote` mode. The runner captures the worktree as it exists—including staged, unstaged, and untracked files—without requiring a commit or push.

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

A successful `strict`, `impacted`, or `full` run writes a receipt under the worktree's Git metadata. The receipt records a SHA-256 fingerprint over the current HEAD, resolved base commit, and exact Git tree containing staged, unstaged, and untracked files. The pre-commit hook recalculates that fingerprint. It skips redundant validation only when the receipt matches exactly; any content or base-ref change invalidates it and starts a new remote run. Only a strict receipt satisfies the authoritative pre-commit gate because it additionally proves the Playwright safety net. The client refuses to issue a strict receipt unless the downloaded artifacts include `playwright.log`; this fails safely when an older deployed runner treats the mode as impacted-only. Impacted, full, and Playwright-only receipts remain useful diagnostic evidence but do not bypass strict validation. Local mode runs the same strict gate under a global host lock and is the operational default. Set `BOTNEXUS_VALIDATION_MODE` at process, user, or machine scope to `local` or `remote`; process scope wins. `-LocalFallback` and `BOTNEXUS_VALIDATION_LOCAL_FALLBACK=1` remain backward-compatible local aliases.

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

## Snapshot format

The local script uploads a small payload containing:

1. a Git bundle with repository history and refs, needed by `dotnet-affected`; and
2. a tar overlay containing all tracked and untracked, non-ignored worktree files.

The runner clones the bundle, applies the overlay, commits a temporary snapshot, and then invokes the repository's canonical build and test scripts. The temporary commit exists only inside the ephemeral job replica.

## Maintenance and PR automation

All agents, maintenance jobs, PR workflows, and human development flows call `scripts/repo/Validate-PreCommit.ps1` once for the final candidate. Record the selected mode and gate evidence in Merge Notes. Do not hand-run `dotnet build`, `dotnet test`, or `test-impacted.ps1` as an extra pre-push gate. Local mode is globally serialized across BotNexus worktrees; remote mode retains exact-content receipt reuse.
