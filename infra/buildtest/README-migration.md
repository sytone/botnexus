# Build/test ACA environment — subnet + NAT gateway migration

**Status:** required. **Raised:** 2026-08-13.

## Why

Azure Container Apps is a **HOBO** (Hosted-On-Behalf-Of) service. An environment with no subnet
sends outbound traffic from IP addresses owned by the Container Apps platform rather than by us.
Those shared platform addresses are being reclassified, so an environment that keeps relying on
them will eventually lose access to resources that authorise on source IP.

For `bnx-buildtest-env` that means the remote build/test gate — the authoritative validation path
for this repo — stops working, with no local fallback permitted.

## This is an IN-PLACE update, not a rebuild

**Verified against the live control plane, not inferred.** An earlier revision of this document
claimed the environment had to be destroyed and recreated. That was wrong, and the correction
matters: it is the difference between a config change and an outage.

The environment type decides the procedure, and a **Consumption-only** environment does
indeed require recreation. The trap is that our environment reports a `workloadProfiles` array
containing a profile *named* `Consumption`:

```json
"workloadProfiles": [ { "name": "Consumption", "workloadProfileType": "Consumption" } ]
```

That is a **workload-profiles environment whose only profile is the Consumption one** — not a
legacy Consumption-only environment. A legacy Consumption-only environment has `workloadProfiles`
absent or null. Reading the profile *name* rather than the array's presence is what produced the
wrong call.

Confirmed empirically with a non-destructive probe — PATCH the existing environment with a
`vnetConfiguration` pointing at a deliberately non-existent subnet:

```
ManagedEnvironmentInvalidNetworkConfiguration:
  Invalid vnet resource ID provided, or the virtual network could not be found.
```

Azure rejected the **subnet**, not the operation. A Consumption-only environment returns a refusal
naming the environment type instead. The environment was verified unchanged
(`provisioningState: Succeeded`, `vnetConfiguration: null`) after the probe.

Adding a subnet is supported for an existing non-vnet **workload profile** environment, and is not
supported for a consumption-only environment. We are the first case.

## What prompted this

| Field | Value |
|---|---|
| Subscription | `$env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID` |
| Resource group | `botnexus-buildtest` |
| Environment | `bnx-buildtest-env` |
| Current static IP | Query at runtime with the command in step 4 (platform-owned — this is the thing being fixed) |
| `subnetResourceId` | *(empty)* |
| `natGatewayId` | *(empty)* |

## Constraints — read before deploying

1. **Adding the subnet must be the ONLY change in the request.** Do not change tags, log
   configuration, or anything else in the same deployment. This is why the migration deploys the
   network resources first and attaches the subnet second.
2. **The frontend IP will change.** Anything referencing the current address directly, or an A
   record in DNS, must be updated. CNAME records need no change. DNS refresh takes several minutes.
   `Invoke-AzureBuildTest.ps1` resolves the job by resource ID, not IP — verify before deploying.
3. **Subnet must be delegated** to `Microsoft.App/environments`, and a **NAT gateway must be
   attached** to it.
4. **CIDR must not overlap** reserved ranges: `169.254.0.0/16`, `172.30.0.0/16`, `172.31.0.0/16`,
   `192.0.2.0/24`, and the workload-profile reservations `100.100.0.0/17`, `100.100.128.0/19`,
   `100.100.160.0/19`, `100.100.192.0/19`. This template uses `10.0.0.0/16` with a `10.0.0.0/23`
   subnet — no overlap.
5. **Removing a subnet later is NOT supported.** This is one-way.
6. **ARM/Bicep only** — CLI and Portal do not support adding a subnet.

## Public IP — may need a service tag

A default-allocated public IP is not automatically treated as one we own for authorisation
purposes. Creating the NAT gateway requires a public IP, and getting that address recognised may
require the networking team to apply a service tag and assign it.

So this deployment fixes the *topology*; the address may still need a service tag to be recognised
as ours. For a non-production build/test subscription reaching only ACR and Storage in the same
subscription a tag is likely unnecessary — **that is an assumption, not a verified fact**, and it
should be settled before this work is declared complete.

## Procedure

1. **Confirm no gate run is in flight:**
   ```powershell
   $sub = $env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID
   if ([string]::IsNullOrWhiteSpace($sub)) {
     throw 'Set BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID before running this migration.'
   }
   az containerapp job execution list -g botnexus-buildtest -n bnx-buildtest-runner `
     --subscription $sub --query "[?properties.status=='Running']" -o json   # must be []
   ```
2. **Deploy the network prerequisites FIRST**, using `network.bicep` — not `main.bicep`. The subnet
   attach must be the only change in its request, and a full `main.bicep`
   deployment also re-evaluates the ACR, storage, role assignments and job.

   > **`az deployment group create` HANGS against this subscription when run synchronously.**
   > Observed 2026-08-13: the call never returned, and it had submitted **nothing** — no
   > deployment was recorded and no resource created. That failure mode looks alarming (an
   > apparent timeout mid-deploy) but is safe: nothing is half-applied. Always use `--no-wait`
   > and poll. Do not retry synchronously, and do not assume a hang means a partial deployment.

   ```powershell
   az deployment group create --subscription $sub -g botnexus-buildtest --no-wait `
     --name buildtest-network --template-file infra/buildtest/network.bicep
   # poll until Succeeded:
   az deployment group show --subscription $sub -g botnexus-buildtest `
     --name buildtest-network --query properties.provisioningState -o tsv
   ```

2b. **Attach the subnet as the ONLY change in its own request.**
   ```powershell
   $subnetId = az network vnet subnet show -g botnexus-buildtest --subscription $sub `
     --vnet-name bnx-buildtest-vnet -n bnx-buildtest-aca-subnet --query id -o tsv
   ```
   Then PATCH `vnetConfiguration` (`internal: false`, `infrastructureSubnetId: $subnetId`) onto
   the existing environment via ARM. Expect several minutes while it reconfigures. Existing jobs
   and their definitions survive; the ACR, storage account, managed identity and role assignments
   are untouched.

   Verified 2026-08-13: both the build-test runner and the manually-created reload probe job came
   through the attach with `provisioningState: Succeeded`.
3. **Verify the subnet attached:**
   ```powershell
   az containerapp env show -g botnexus-buildtest -n bnx-buildtest-env --subscription $sub `
     --query '{subnet:properties.vnetConfiguration.infrastructureSubnetId,internal:properties.vnetConfiguration.internal,ip:properties.staticIp}' -o json
   ```
   `subnet` must be non-empty, `internal` must be `false`, and `ip` will have **changed**.
4. **Verify egress now uses our NAT IP:**
   ```powershell
   az network public-ip show -g botnexus-buildtest -n bnx-buildtest-nat-pip --subscription $sub --query ipAddress -o tsv
   ```
5. **Prove the gate still works end to end** — a green deployment is not evidence the runner runs:
   ```powershell
   scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <a clean worktree>
   ```
   Read `test-result.json` (`total`, `executed`, `skipped`, `fixtureFailures`, `isComplete`).
   Exit 0 alone is not evidence.

## Rollback

**There is none for the subnet itself** — removing a subnet from an existing environment is not
supported. Roll-forward only. If the environment is left broken, the recovery path is to recreate
it from the pre-change template, which restores the gate and reinstates the original problem.

This is the strongest argument for running step 1 properly: once started, the change is one-way.

## BCDR — rebuilding from scratch

**A from-scratch deploy of `main.bicep` produces a compliant environment with no extra steps.**
The network resources are declared in `main.bicep` itself, and the environment declares its
`vnetConfiguration` inline, so a greenfield deployment creates the public IP, NAT gateway and
delegated subnet and brings the environment up already inside them. There is no post-deployment
attach to remember and no ordering constraint to get right — ARM resolves the dependency from the
`vnet.properties.subnets[0].id` reference.

Verified 2026-08-13: `az bicep build` compiles `main.bicep` clean to **14 ARM resources**,
including `Microsoft.Network/publicIPAddresses`, `natGateways` and `virtualNetworks`.

```powershell
$oid = az ad signed-in-user show --query id -o tsv
az deployment group create --subscription $sub -g botnexus-buildtest --no-wait `
  --name buildtest-platform --template-file infra/buildtest/main.bicep `
  --parameters operatorObjectId=$oid suffix=<suffix> runnerImageTag=<tag>
```

The two-step `network.bicep` + PATCH procedure above exists **only** for attaching a subnet to an
environment that already exists. Do not use it for a rebuild.

### Caveats a rebuild must account for

- **The reload probe job is not in the template.** It was created by hand on 2026-08-05. A
   from-scratch deploy will not recreate it.
- **The public-IP service-tag question is unresolved** (see above). A rebuild inherits it.
- **`Deploy-BuildTestInfrastructure.ps1` submits with `--no-wait` and polls** (#3118). It no
  longer hangs on this subscription, so the script is a valid rebuild path. It fails with the
  ARM error detail on a failed deployment and throws on a bounded timeout rather than stalling.


## Unresolved / needs a decision

1. **Two orphaned resource sets exist in this resource group.** The generated suffix derives from
   the subscription ID, and an earlier deployment used a different algorithm. Identify the legacy
   pair from Azure Resource Graph, confirm it is unused, and delete it.
2. **The reload probe job is not in `main.bicep`.** An operator created it manually on 2026-08-05
   from a temporary probe image. It SURVIVES this migration, but it remains undeclared
   infrastructure that a future template deployment will not reproduce. Either add it or remove it
   deliberately.
3. **Whether the NAT public IP needs a service tag** (see above).
