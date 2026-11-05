# Qualify a remote runner release

For operators and developers who maintain the [Azure build and test runner](azure-build-test-runner.md).
This reference describes the runner compatibility implementation for #5140. It is not approval
of the currently installed image, proof of a successful remote run, or permission to install or
deploy anything. Qualification and installation remain separate, operator-reviewed actions.

## Keep three identities separate

| Identity | What it identifies | What it does not prove |
| --- | --- | --- |
| Immutable image, `repository@sha256:<digest>` | The runner image selected from trusted provider data and pinned for this execution. | That every candidate execution input is compatible, or that tests passed. |
| `runnerInputDigest` | The execution and toolchain inputs externally qualified against that image. | The image's bytes or the complete application candidate. |
| Exact candidate snapshot | Raw files uploaded for this run, plus HEAD, base and tree in the validation fingerprint. | Operator approval or runner capability. |

A `src-...` image tag is useful source attribution, not release approval. A checkout's runner
source tag may differ from the installed image without requiring an automatic rebuild.
The external qualification must still match the candidate execution inputs and immutable image.
Unrelated application or test `.cs` changes normally change the exact candidate snapshot, not
`runnerInputDigest`. They still require candidate validation.

## Operator prerequisites and lifecycle

Under the **trusted-coordinator-profile** model, the trusted coordinator selects an external
profile file through `BOTNEXUS_RUNNER_RELEASE_MANIFEST` in the launch environment. Its value
must be an absolute local path to a regular manifest file **outside the candidate directory**.
The sender, receipt reader and recovery command use this selector. They do not discover approval
in candidate configuration, Git metadata, a receipt or a recovery context. The compatibility
module receives the selected path from its callers; it does not select or create approval.

Before using this contract, the operator must provide:

- A reviewed immutable image and qualification evidence for the selected modes, capabilities,
  payload version, evidence version and execution-input digest.
- A coordinator-selected external profile under ordinary existing user-profile permissions.
  The coordinator and local shell are trusted. No separate operator-only ACL or inability
  of a same-user shell to replace the profile is promised.
- Stable candidate and profile directories during checks, with trusted local scripts, tools,
  Git metadata, receipts and provider responses. A JSON `approved` flag does not establish trust.
- Existing authorized access to the job, registry and artifact storage. This reference does not
  perform authentication, provisioning or deployment.

Use this release lifecycle:

1. Review the image and candidate execution contract independently of application validation.
   Decide whether the image supports the requested modes and evidence/containment capabilities.
2. Qualify those execution inputs against the immutable image through a separate reviewed process.
   Publish the resulting external manifest only after that decision. The compatibility module
   checks a qualification; it does not perform one or generate an approving manifest.
3. Launch with the trusted coordinator-selected profile and a stable candidate. The sender checks approval
   before upload and again immediately before starting the job.
4. Require provider execution identity, current approval, exact-source proof and complete test
   evidence before certification. Reuse and recovery reread current approval too.
5. Revoke or replace the manifest when qualification is no longer valid. Replacement invalidates
   old bindings even when the image and parsed values stay the same: the binding hashes the
   **exact manifest bytes**, so whitespace-only edits also invalidate them.

Compatible sender or receipt-reader changes may be **externally requalified against the same
immutable image**. A digest mismatch is a qualification blocker, not an instruction to rebuild
or redeploy automatically. Image changes or missing capabilities need a separate operator decision
and, where necessary, a separately authorized installation. Never repair old receipts or contexts
by editing them to match a new manifest.

## Manifest schema

The manifest is one strict UTF-8 JSON object, 1–65,536 bytes, with maximum JSON depth 8.
All ten properties below are required with their exact case. Unknown, duplicate and case-colliding
properties are rejected. Comments and trailing commas are not accepted.

| Property | Exact constraint |
| --- | --- |
| `schemaVersion` | JSON number written exactly as `1`; not `1.0`, an exponent or a string. |
| `releaseId` | String matching `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$` (1–128 characters). |
| `image` | Lowercase immutable repository image with `@sha256:` and exactly 64 lowercase hex digits; maximum 512 characters. Must equal the resolved provider image. See the accepted grammar below. |
| `approved` | JSON boolean; must be `true` to use the release. |
| `revoked` | JSON boolean; must be `false` to use the release. |
| `runnerInputDigest` | Exactly 64 lowercase hexadecimal characters; must match the candidate's qualified execution-input digest. |
| `modes` | Array of 1–6 distinct strings, each at most 256 characters. Allowed values: `strict`, `impacted`, `full`, `core`, `playwright`, `diagnostic`. Case-sensitive; must include the requested mode. |
| `payloadVersion` | JSON number written exactly as `1`. |
| `evidenceVersion` | JSON number written exactly as `1`. |
| `capabilities` | Array of 1–6 distinct strings, each at most 256 characters. Only `exact-source-v1` and `diagnostic-containment-v1` are known. `exact-source-v1` is always required; `diagnostic` also requires `diagnostic-containment-v1`. |

The image grammar in `Assert-RunnerReleaseCompatibility` is:

```text
^[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]{1,5})?/[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*@sha256:[0-9a-f]{64}$
```

**Synthetic, NONAPPROVING example — do not use as a live manifest.** The image and digest are
fictional. `approved: false` and `revoked: true` deliberately prevent use. Changing these flags
is not qualification and does not approve any installed image.

```json
{
  "schemaVersion": 1,
  "releaseId": "example-nonapproving",
  "image": "registry.example/runner@sha256:0000000000000000000000000000000000000000000000000000000000000000",
  "approved": false,
  "revoked": true,
  "runnerInputDigest": "0000000000000000000000000000000000000000000000000000000000000000",
  "modes": ["core", "diagnostic"],
  "payloadVersion": 1,
  "evidenceVersion": 1,
  "capabilities": ["exact-source-v1", "diagnostic-containment-v1"]
}
```

## Qualified execution-input digest

`Get-RunnerReleaseInputDigest` in the compatibility module defines the fixed input scope.
It is not a public qualification or manifest-generator command. External qualification tooling
must follow this convention exactly, rather than hash the whole repository or the image tag.

Included inputs are:

- Nonfixture files under `infra/buildtest/`.
- Direct `scripts/repo/*.ps1` and `*.psm1` execution inputs, excluding fixture names.
  **`scripts/repo/test-impacted.ps1` is explicitly included**, despite its test-like name.
- Files ending in `.props`, `.targets`, `.proj`, `.csproj` or `.runsettings`, at any depth.
- `global.json`, `nuget.config`, root `dotnet-tools.json` and `.config/dotnet-tools.json`.
- `.cs` and `.json` files under `tools/BotNexus.TestEventCollector/`.

Traversal excludes `.git`, `bin`, `obj`, `node_modules`, `artifacts` and `.affected` directories,
and every `tests` subtree under `infra/buildtest/`. The repository PowerShell fixture-name
convention is `Test-*.ps1`, `*.Tests.ps1` or `*.Mutation.ps1`, matched case-insensitively;
it excludes those names from the direct repository-script selection, except `test-impacted.ps1`.
Ordinary application and test `.cs` files are not selected. Project, build and toolchain policy
is deliberately conservative: even a policy change that ultimately proves compatible needs
external requalification. Linked traversal paths and portable case-colliding selected paths fail.
At least one execution input is required.

The digest is lowercase SHA-256 over this byte stream:

1. Strict UTF-8 bytes of `botnexus-qualified-execution-inputs-v1`, followed by one NUL byte.
2. For each selected relative path, sorted with ordinal case-sensitive comparison: encode the
   path with `/` separators as UTF-8. Decode content as strict UTF-8, remove leading U+FEFF
   characters, replace CRLF with LF, then re-encode as UTF-8 without a byte-order mark.
3. Append the decimal path-byte length, `:`, path bytes, decimal normalized-content-byte length,
   `:`, and normalized content bytes. Decimal lengths use invariant culture. There are no
   extra separators between files.

This normalization differs from the [exact-source snapshot](remote-source-snapshots.md),
which hashes raw candidate bytes. A CRLF/LF change can preserve the execution-input digest
while changing the exact candidate proof.

## Launch, certification, reuse and recovery

The sender reads the job's singleton container from trusted provider data. It accepts a digest
reference directly or resolves a supported Azure Container Registry tag through registry metadata.
It then starts an **execution pinned to the approved digest**, not the mutable tag. It does not
update the job definition. Under `image-entrypoint-v1`, command and args overrides must be absent,
null or empty arrays; alternate execution entrypoints cannot certify this contract.

| Boundary | Required check |
| --- | --- |
| Before upload and before start | Reread external manifest; compare execution inputs, requested mode, capabilities and pinned image with the launch binding. |
| Final receipt creation | Reread current approval; verify the provider's execution image/configuration, execution/run/mode identity, candidate fingerprint, source proof and complete test results. |
| Cached receipt reuse | Require v2 evidence, matching candidate and mode, valid source proof, `image-entrypoint-v1`, and a fresh external-manifest comparison. Uses the previously validated execution image in the **trusted local receipt**, not a fresh provider query. |
| Recovery | Require a v2 launch context; reread current approval and query the original provider execution before download. Final certification repeats binding, candidate and evidence checks. No new execution is started. |

The v2 launch context and receipt record `runnerRelease` with exactly `releaseId`, `image`,
`manifestSha256`, `runnerInputDigest`, `payloadVersion`, `evidenceVersion` and `mode`, plus
`executionConfiguration: image-entrypoint-v1` outside that binding. Persisted evidence is not
approval authority. Legacy contexts/receipts cannot self-upgrade; current approval alone cannot
make an old run qualify. See [receipt reuse and recovery](validation-receipts.md).

Missing approval, revocation, unknown modes/capabilities, missing required capabilities,
changed manifest bytes, changed execution inputs, or mismatched provider image/configuration
are real blockers. Stop and obtain operator review; do not bypass the checks or weaken the
source/completeness requirements. A cache miss may send the normal gate to a new run, but launch
still has to pass current compatibility checks.

`diagnostic` remains a private, contained investigation mode, **never a validation receipt**.
It requires both capabilities and keeps its existing project-path, process-containment,
source-proof and evidence-completeness checks. Qualification does not turn partial test evidence,
a requested dump, or a successful diagnostic run into CORE acceptance. Describing diagnostic
capabilities does not approve diagnostic execution; core-only qualification does not authorize it.

## Trust and limits

Path checks reject observed links/reparse points, provider/network/device paths, Windows
alternate data streams and a manifest inside the candidate. The profile remains outside the
repository under ordinary existing user-profile permissions. Native peer tool restrictions
remain applicable, but they are not OS or shell isolation. This model explicitly trusts the
coordinator and local shell, including the selected profile, local cache, tooling and provider
data. It does not promise operator-only ACL protection or nonreplacement by a same-user shell.

Path checks do not make directory traversal atomic, detect every hardlink alias, or prevent
rename races and change-and-restore (ABA) attacks. Repeated reads narrow consistency gaps;
they are not a lock against a hostile writer. Keep the directories stable.

These are compatibility and consistency checks, **not hostile-candidate cryptographic
attestation or measurement of deployed infrastructure**. The sender/tooling, local cache,
coordinator-selected qualification profile and provider data remain trusted. An image digest does not measure live
resource configuration or prove that a malicious runner executed honest tests. Exact-source
proof and complete test evidence remain separate mandatory requirements.

## Source references

- [Compatibility parser, digest and binding checks](https://github.com/sytone/botnexus/blob/main/scripts/repo/RunnerReleaseCompatibility.psm1): `Read-RunnerReleaseManifest`, `Get-RunnerReleaseInputDigest`, `Assert-RunnerReleaseCompatibility`, `Assert-RunnerReleaseExecution` and `Assert-RunnerReleaseBinding`.
- [Sender](https://github.com/sytone/botnexus/blob/main/scripts/repo/Invoke-AzureBuildTest.ps1): environment selector, provider resolution, digest-pinned launch and pre-upload/pre-start checks.
- [Finalization](https://github.com/sytone/botnexus/blob/main/scripts/repo/AzureBuildTestRecovery.psm1): `Complete-AzureBuildTestRecovery` and receipt writing.
- [Recovery](https://github.com/sytone/botnexus/blob/main/scripts/repo/Resume-AzureBuildTest.ps1) and [current gate](https://github.com/sytone/botnexus/blob/main/scripts/repo/Invoke-PreCommitValidation.ps1): current approval at recovery and cache reuse.

The source links identify repository files; availability on `main` depends on merge. This page
records implementation behavior, not a released-image or installed-image acceptance receipt.
