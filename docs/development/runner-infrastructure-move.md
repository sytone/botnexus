# Runner infrastructure source ownership

For developers reviewing the infrastructure extraction. Paths below describe source
ownership, not deployment or release approval. Standalone destinations are in
[sytone/botnexus-runner](https://github.com/sytone/botnexus-runner).

## Moved source and tests

The application has no runner Dockerfile, image entrypoint, infrastructure templates,
provisioning implementation or container containment implementation. Runtime tests
move with the runtime; application build/test and acceptance assertions stay here.

| Original application path | Destination or retained responsibility | Owner |
| --- | --- | --- |
| `infra/buildtest/Deploy-BuildTestInfrastructure.ps1` | `scripts/repo/New-RunnerInfrastructure.ps1` | Standalone |
| `infra/buildtest/RunnerImageProvenance.ps1` | `infra/buildtest/Import-RunnerImageProvenance.ps1` | Standalone |
| `main.bicep` in the former application infrastructure directory | [standalone main template](https://github.com/sytone/botnexus-runner/blob/refactor/5162-infrastructure/infra/buildtest/main.bicep) | Standalone |
| `network.bicep` in the former application infrastructure directory | [standalone network template](https://github.com/sytone/botnexus-runner/blob/refactor/5162-infrastructure/infra/buildtest/network.bicep) | Standalone |
| `infra/buildtest/runner/DiagnosticContainment.cs` | `infra/buildtest/runner/DiagnosticContainment.cs` | Standalone |
| `infra/buildtest/runner/DiagnosticOwnedCommand.ps1` | `infra/buildtest/runner/Invoke-DiagnosticOwnedCommand.ps1` | Standalone |
| `infra/buildtest/runner/Dockerfile` | `infra/buildtest/runner/Dockerfile` | Standalone |
| `infra/buildtest/runner/RunnerBuild.ps1` | `infra/buildtest/runner/RunnerBuild.ps1` | Standalone |
| `infra/buildtest/runner/RunnerCost.ps1` | `infra/buildtest/runner/RunnerCost.ps1` | Standalone |
| `infra/buildtest/runner/RunnerDiagnostic.ps1` | `infra/buildtest/runner/Import-RunnerDiagnostic.ps1` | Standalone |
| `infra/buildtest/runner/RunnerResult.ps1` | `infra/buildtest/runner/Import-RunnerResult.ps1` | Standalone |
| `infra/buildtest/runner/RunnerTimeout.ps1` | `infra/buildtest/runner/RunnerTimeout.psm1` | Standalone |
| `infra/buildtest/runner/entrypoint.ps1` | `infra/buildtest/runner/Invoke-Runner.ps1` | Standalone |
| `infra/buildtest/runner/tests/DiagnosticFixtureClock.ps1` | `infra/buildtest/runner/tests/Test-DiagnosticFixtureClock.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerBuild.Tests.ps1` | `infra/buildtest/runner/tests/RunnerBuild.Tests.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerCost.Tests.ps1` | `infra/buildtest/runner/tests/RunnerCost.Tests.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerDiagnostic.Tests.ps1` | `infra/buildtest/runner/tests/Test-RunnerDiagnostic.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerDiagnosticCleanup.Tests.ps1` | `infra/buildtest/runner/tests/Test-RunnerDiagnosticCleanup.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerDiagnosticDeadline.Tests.ps1` | `infra/buildtest/runner/tests/Test-RunnerDiagnosticDeadline.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerDiagnosticPublication.Tests.ps1` | `infra/buildtest/runner/tests/Test-RunnerDiagnosticPublication.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerResult.Tests.ps1` | `infra/buildtest/runner/tests/Test-RunnerResult.ps1` | Standalone |
| `infra/buildtest/runner/tests/RunnerTimeout.Tests.ps1` | `infra/buildtest/runner/tests/Test-RunnerTimeout.ps1` | Standalone |
| `infra/buildtest/tests/DedicatedRunnerCapacity.Tests.ps1` | `infra/buildtest/tests/DedicatedRunnerCapacity.Tests.ps1` | Standalone |
| `infra/buildtest/tests/DeployAsyncSubmission.Tests.ps1` | `infra/buildtest/tests/Test-DeployAsyncSubmission.ps1` | Standalone |
| `infra/buildtest/tests/DeployTagGuard.Tests.ps1` | `infra/buildtest/tests/Test-DeployTagGuard.ps1` | Standalone |
| `infra/buildtest/tests/RunnerImageProvenance.Tests.ps1` | `infra/buildtest/tests/Test-RunnerImageProvenance.ps1` | Standalone |
| `infra/buildtest/tests/Test-PreCommitGateMigration.ps1` | Application-owned release/receipt test; retained | Application |
| `infra/buildtest/tests/Test-RunnerReleaseContract.ps1` | Application-owned release/receipt test; retained | Application |
| `infra/buildtest/tests/Test-RunnerReleaseReceipt.ps1` | Application-owned release/receipt test; retained | Application |
| `infra/buildtest/tests/Test-RunnerReleaseSender.ps1` | Application-owned release/receipt test; retained | Application |

## Application-only helpers

| Original helper or test | Application destination | Preserved responsibility |
| --- | --- | --- |
| `runner/RunnerTimeout.ps1` and its fixture | `scripts/repo/ApplicationTestExecution.psm1`, `Test-ApplicationTestExecution.ps1` | Runnable application project selection, partial TRX attribution and bounded CORE child execution |
| `runner/RunnerResult.ps1` and its fixture | `scripts/repo/ApplicationTestResult.psm1`, `Test-ApplicationTestResult.ps1` | Existing fail-closed TRX counters, rows, fixture failures and declared-skip policy |
| Sender-used functions from `runner/RunnerDiagnostic.ps1` | `scripts/repo/ApplicationDiagnosticContract.psm1`, `Test-ApplicationDiagnosticContract.ps1` | Validate a diagnostic project and returned exact-source proof; no containment, watchdog or dump process implementation |
| CORE execution fixture | `scripts/repo/Test-CoreTestExecution.ps1` | Existing completion, bounded diagnostics and workflow assertions |
| Source snapshot fixture | `scripts/repo/Test-SourceSnapshot.ps1` | Actual sender capture, four-entry transport format, source restoration and application acceptance boundary |

Application timeout tests preserve project discovery and scope cases that do not
belong in a generic image. Runtime-only reserve and timeout-record cases remain in
the standalone timeout fixture. Diagnostic deadline, cleanup and atomic publication
fixtures remain with the standalone runtime. The application collector event test
stays in `RunnerDiagnosticArchitectureTests`.

The standalone generic source transport fixture executes its actual pipeline restore
block. The application fixture validates the sender payload and shared verifier API;
it no longer reads an image entrypoint from the application checkout. No cross-repo
checkout, source pin, fallback download or new execution adapter is introduced.

The removed advisory checkout-to-image source-tag comparison is not release approval.
Image-source provenance is checked by the infrastructure owner. Existing external
immutable-image qualification and application exact-source/result acceptance remain
fail-closed and unchanged.

## Infrastructure-only commands

The standalone repository supplies `New-RunnerInfrastructure.ps1` for registry and
infrastructure phases, `Invoke-RunnerRegistryBuild.ps1` for ACR build/publication,
`Invoke-RunnerImageBuild.ps1` for a local image check, and `Update-RunnerJobImage.ps1`
for image-only maintenance. Its `registry.bicep`, image runtime manifest and supporting
files are standalone additions, not application dependencies.

## Delivery boundary

The source move does not assert a merge, live image update or working application
execution. Independent review and applicable repository checks still apply. Live
runner use and application integration are separate work; they are not prerequisites
for moving infrastructure source. Human merge approval remains required.
