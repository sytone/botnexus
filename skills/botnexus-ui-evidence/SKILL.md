---
name: botnexus-ui-evidence
description: Capture deterministic, validated BotNexus portal screenshots with gateway and Chromium isolated in one disposable container.
---

# BotNexus UI evidence

Use `Invoke-BotNexusUiEvidence.ps1` when a change needs reviewable portal evidence. The host entrypoint invokes only Docker or Podman; it never starts a host gateway or browser and never reads the host BotNexus home.

## Prerequisites and invocation

Docker or Podman must be installed and able to build Linux containers. The normal NuGet global-packages cache is mounted read-only into the image build when `NUGET_PACKAGES` is set, allowing deterministic offline restores after packages have been restored once; no host binaries or BotNexus state are mounted into the runtime container. Run from any directory:

```powershell
pwsh -NoProfile -File skills/botnexus-ui-evidence/Invoke-BotNexusUiEvidence.ps1 `
  -RepositoryPath <worktree> -OutputDirectory <empty-output-directory> `
  -ViewportWidth 1440 -ViewportHeight 1000 -PromptKey SLOW_STREAM `
  -Selectors '[data-testid="streaming-badge"]','[data-testid="chat-abort-btn"]' `
  -AccessibleNames 'Stop agent','Expand composer'
```

The image is built from the current worktree, including tracked and untracked source in the build context. The committed revision and a SHA-256 digest of the copied build context are captured inside the image. A temporary integration-mock agent and catalog are provisioned in the container. The real gateway and Playwright Chromium run together on container loopback with external networking disabled. Use `-AccessibleNames` only for exact names expected on the candidate branch; selector assertions remain the portable default.

## Outputs and failure behavior

The output directory receives only `portal.png` and `evidence.json`. `evidence.json` follows `evidence.schema.json` and records source commit/tree, scenario, viewport, screenshot SHA-256, selector/accessibility/active-run assertions, the rendered accessible-name census for all four active-run controls, UTC timestamps, and cleanup state. Any missing selector or exact accessible name, inactive run, browser error, corrupt/empty artifact, or hash mismatch fails closed. The portable active-run defaults require the streaming badge and stop control; add transient content selectors such as `streaming-message` only when the candidate scenario guarantees their visibility at capture time.

The container, named volume, temporary image, gateway, browser, home, and data are removed on success or failure. The manifest is marked `containerRemoved: true` only after host cleanup. If image build or container startup is unavailable, stop and report that infrastructure failure rather than attempting a host fallback.

## Publication boundary

Artifacts are evidence, not authorization to publish, merge, or upload them. Review `portal.png` and sanitized `evidence.json` before attaching them to an issue or pull request. No logs, configuration, host paths, secrets, conversation IDs, or container IDs are copied out.
