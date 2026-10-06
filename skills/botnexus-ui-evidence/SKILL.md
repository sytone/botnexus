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
  -PagePath '/release-history' `
  -Selectors '[data-testid="release-source-status"]','[data-testid="release-history-entry"]' `
  -AccessibleNames 'Stop agent','Expand composer'
```

The image is built from the current worktree, including tracked and untracked source in the build context. The committed revision and a SHA-256 digest of the copied build context are captured inside the image. A temporary integration-mock agent and catalog are provisioned in the container. The real gateway and Playwright Chromium run together on container loopback with external networking disabled. Use `-AccessibleNames` only for exact names expected on the candidate branch; selector assertions remain the portable default.

For the full composer evidence matrix on the candidate branch, use the same invocation with `-CaptureMatrix` and retain the default chat page. The opt-in mode captures desktop idle, mobile idle and active in the actual `/mobile/agent/evidence-agent/conversation/{id}` app route, active pre-token, an active tool-call gap, focused-active, reduced-motion, returned-idle, and the mobile composer CSS animation at 0%, the measured-iteration midpoint (~50%), and the measured end position. Mobile captures assert the `mobile-composer` test id, `aria-busy`, and the stable `role=status` text while active (and status absence while idle); animation captures assert computed `::before` `offsetDistance` changes between start and midpoint. Every image is taken by the in-container browser, paired with DOM/accessibility state assertions and a per-image SHA-256; no image fixtures or host browser are accepted. The original single-capture invocation remains the default.

## Outputs and failure behavior

The single-capture workflow proves a real active integration-mock run, then navigates to `PagePath` and validates the requested selectors before capture. The matrix workflow captures eleven real browser screenshots into the output directory plus `evidence.json`; it validates the live composer `aria-busy` state, rendered tool call, focus, reduced-motion computed style, and CSS animation positions and computed offset-distance travel. The manifest records source commit/tree, scenario, viewport, screenshot SHA-256, browser/accessibility assertions, the rendered accessible-name census for all four active-run controls, UTC timestamps, and cleanup state. Any missing state, accessible name, inactive run, browser error, corrupt/empty artifact, or hash mismatch fails closed.
The container, named volume, temporary image, gateway, browser, home, and data are removed on success or failure. The manifest is marked `containerRemoved: true` only after host cleanup. If image build or container startup is unavailable, stop and report that infrastructure failure rather than attempting a host fallback.

## Publication boundary

Artifacts are evidence, not authorization to publish, merge, or upload them. Review the screenshots and sanitized `evidence.json` before attaching them to an issue or pull request. No logs, configuration, host paths, secrets, conversation IDs, or container IDs are copied out.
