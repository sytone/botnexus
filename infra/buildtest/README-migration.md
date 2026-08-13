# Build/test ACA environment — subnet + NAT gateway migration

**Status:** required. **Flagged:** 2026-08-13, cluster `thankfulisland-09134225` (westus2).
**TSG:** `eng.ms/docs/coreai/devdiv/serverless-paas-balam/serverless-paas-vikr/azure-container-apps/azure-container-apps-tsg/firstparty/1pappaccessaad`

## Why

Azure Container Apps is a **HOBO** (Hosted-On-Behalf-Of) service. An environment with no subnet
sends outbound traffic from IP addresses owned by the Container Apps team. Those platform IPs are
being reclassified as **unprivileged**, and once the network team sets a cutover date, any
environment still using them **loses access to privileged resources**.

For `bnx-buildtest-env` that means the remote build/test gate — the authoritative validation path
for this repo — stops working, with no local fallback permitted.

There is no fix-in-place. Adding our own subnet with a NAT gateway attached to a public IP we own
is the only remedy the TSG offers.

## What was flagged

| Field | Value |
|---|---|
| Subscription | `d0024f9b-079d-4464-a10a-c19e2fd4a781` (`jobullen-tst-dev-lrn`) |
| Resource group | `botnexus-buildtest` |
| Environment | `bnx-buildtest-env` |
| Managed cluster | `thankfulisland-09134225` |
| Tenant / region | westus2 |
| `subnetResourceId` | *(empty)* |
| `natGatewayId` | *(empty)* |

## Which TSG case applies

The alert payload reports `envType: Workload profile env`. **That is wrong**, and the distinction
decides the entire procedure — so verify it rather than trusting the payload:

```powershell
az containerapp env show -g botnexus-buildtest -n bnx-buildtest-env `
  --subscription d0024f9b-079d-4464-a10a-c19e2fd4a781 `
  --query '{profiles:properties.workloadProfiles,vnet:properties.vnetConfiguration}' -o json
```

Measured 2026-08-13: `workloadProfiles: [Consumption]`, `vnetConfiguration: null`. That is the TSG's
fourth case — **Consumption-only environment without a vNet** — whose stated remedy is:

> The Container App environments need to be recreated to use container app workload profile
> environments with vNet.

A Consumption-only environment **cannot accept a subnet**, so `main.bicep` cannot be applied to the
existing environment as an update. It must be replaced.

## Blast radius

Replacing the environment destroys and recreates the environment and every job inside it
(`bnx-buildtest-runner`, `bn-reloadprobe-job`). It does **not** touch the ACR, the storage account,
the managed identity, or the role assignments — those are separate resources in the same template.

Consequence: **the remote validation gate is unavailable for the duration.** Since local test
execution is banned on the workstation, no PR can be validated while the migration runs. Do it when
no gate run is in flight:

```powershell
az containerapp job execution list -g botnexus-buildtest -n bnx-buildtest-runner `
  --subscription d0024f9b-079d-4464-a10a-c19e2fd4a781 `
  --query "[?properties.status=='Running']" -o json    # must be []
```

## Public IP — read before deploying

`main.bicep` allocates a **Standard static public IP** via `Microsoft.Network/publicIPAddresses`.
The TSG warns that a default-allocated IP is itself unprivileged:

> NAT gateway creation requires a public IP address. You may need to work with the network IPAM
> team (aka.ms/ipam) to apply a service tag and get an IP assigned, as the default public IP will
> be an unprivileged IP.

So the deployment below fixes the *topology*, and the IP may still need an IPAM service tag before
it counts as privileged. Treat those as two steps: deploy the subnet + NAT, then confirm with IPAM
whether this workload's destinations require a tagged IP. For a non-production build/test
subscription reaching only ACR and Storage in the same subscription, a tag is likely unnecessary —
**but that is an assumption, not a verified fact, and it should be checked before the alert is
declared resolved.**

## Procedure

1. **Confirm no run is in flight** (command above returns `[]`).
2. **Announce the outage window.** The gate is the only sanctioned validation path.
3. **Delete the existing environment and its jobs:**
   ```powershell
   $sub = 'd0024f9b-079d-4464-a10a-c19e2fd4a781'
   az containerapp job delete -g botnexus-buildtest -n bnx-buildtest-runner  --subscription $sub --yes
   az containerapp job delete -g botnexus-buildtest -n bn-reloadprobe-job    --subscription $sub --yes
   az containerapp env delete  -g botnexus-buildtest -n bnx-buildtest-env    --subscription $sub --yes
   ```
4. **Redeploy from the template** (creates the vNet, NAT gateway, public IP, environment, and job):
   ```powershell
   $oid = az ad signed-in-user show --query id -o tsv
   az deployment group create --subscription $sub -g botnexus-buildtest `
     --template-file infra/buildtest/main.bicep --parameters operatorObjectId=$oid
   ```
   Or run `infra/buildtest/Deploy-BuildTestInfrastructure.ps1`, which wraps the same deployment.
5. **Re-push the runner image.** The ACR survives, so the image should still be present — verify
   rather than assume:
   ```powershell
   az acr repository show-tags -n bnxbt2fd4a781acr --repository botnexus-buildtest-runner -o table
   ```
6. **Verify the fix took:**
   ```powershell
   az containerapp env show -g botnexus-buildtest -n bnx-buildtest-env --subscription $sub `
     --query '{vnet:properties.vnetConfiguration.infrastructureSubnetId,internal:properties.vnetConfiguration.internal}' -o json
   ```
   `infrastructureSubnetId` must be non-empty and `internal` must be `false`.
7. **Prove the gate still works end to end** — a green deployment is not evidence the runner runs:
   ```powershell
   scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <a clean worktree>
   ```
   Read `test-result.json` (`total`, `executed`, `skipped`, `fixtureFailures`, `isComplete`).
   Exit 0 alone is not evidence.

## Rollback

Re-deploying the previous `main.bicep` recreates a Consumption-only environment without a subnet.
That restores the gate but reinstates the alert, so it is a recovery path for a broken migration,
not an acceptable end state.

## Unresolved / needs a decision

1. **Two orphaned resource sets exist in this resource group** — `bnxbtc19e2fd4acr` /
   `bnxbtc19e2fd4sa` alongside the live `bnxbt2fd4a781acr` / `bnxbt2fd4a781sa`. The suffix derives
   from the subscription ID, so an earlier deployment used a different suffix algorithm. The older
   pair appears unused; confirm and delete rather than migrating dead resources.
2. **`bn-reloadprobe-job` is not in `main.bicep`.** It was created manually
   (`createdBy: jobullen@microsoft.com`, 2026-08-05) from image `bn-inoprobe:1`. It will be
   destroyed by step 3 and **will not be recreated** by the template. Either add it to the
   template or accept its loss deliberately.
3. **Whether the NAT public IP needs an IPAM service tag** (see above).
