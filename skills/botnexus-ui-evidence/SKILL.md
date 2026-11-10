---
name: botnexus-ui-evidence
description: Capture deterministic, validated BotNexus portal screenshots with gateway and Chromium isolated in one disposable container.
---

# BotNexus UI evidence

Use `Invoke-BotNexusUiEvidence.ps1` when a change needs reviewable portal evidence. The host entrypoint invokes only Docker or Podman; it never starts a host gateway or browser and never reads the host BotNexus home.

## Prerequisites and invocation

Docker or Podman must be installed and able to build Linux containers. The normal NuGet global-packages cache is mounted read-only into the image build when `NUGET_PACKAGES` is set. No host binaries, home, data, browser profiles or secrets are mounted into the runtime container. Run from the repository root:

```powershell
$evidence = @{
    RepositoryPath = '<worktree>'
    OutputDirectory = '<empty-output-directory>'
    ScratchDirectory = '<task-owned-scratch-outside-worktree-and-.botnexus>'
    ViewportWidth = 1440
    ViewportHeight = 1000
    PromptKey = 'SLOW_STREAM'
    PagePath = '/release-history'
    Selectors = @('[data-testid="release-source-status"]', '[data-testid="release-history-entry"]')
    AccessibleNames = @('Stop agent', 'Expand composer')
}
./skills/botnexus-ui-evidence/Invoke-BotNexusUiEvidence.ps1 @evidence
```

The image is built only from an immutable export of a clean committed Git candidate. Dirty or untracked inputs are rejected. Ignored local files never enter the export. Tracked secret paths, symlinks, submodules and checkout reparse points are rejected. The exporter checks HEAD and cleanliness before and after export and verifies every exported file against its Git blob. The scratch directory must be outside the repository and `.botnexus`. Python 3.10+ and Git are required.

The host independently hashes sorted source-file SHA-256 records before any build. The Dockerfile receives that exact digest, recomputes it before Git initialization/restore/publish and fails on mismatch. The runtime source and browser scripts come from this checked pre-build copy, not generated build output or a mutable host tree. The host compares manifest commit and digest with its own snapshot packet. Runtime networking remains disabled. A temporary integration-mock agent and catalog are provisioned in the container. The real gateway and Playwright Chromium run together on container loopback with external networking disabled. Use `-AccessibleNames` only for exact names expected on the candidate branch; selector assertions remain the portable default.

For the full composer evidence matrix, add `-CaptureMatrix` and retain the default chat page. It captures eleven real browser states: desktop idle, mobile idle and active in the actual `/mobile/agent/evidence-agent/conversation/{id}` route, active pre-token, an active tool-call gap, focused-active, reduced-motion, returned-idle, preference-disabled active, and measured low/high desktop bottom-edge opacity. Mobile states assert `mobile-composer`, `aria-busy`, and stable active status text or idle status absence. Animation states assert computed `::before` opacity changes without a motion path. The original single-capture invocation remains the default.

## Focused sidebar scenario

Use `-FocusedSidebar` instead of `-CaptureMatrix`:

```powershell
./skills/botnexus-ui-evidence/Invoke-BotNexusUiEvidence.ps1 -RepositoryPath '<worktree>' -OutputDirectory '<empty-output-directory>' -ScratchDirectory '<task-owned-scratch>' -FocusedSidebar
```

The scenario seeds 48 task-owned mock agents and 48 conversations through the candidate configuration/API, pins a conversation and assigns another to a custom section. It enables the preference through real settings controls. It captures populated-at-top and scrolled states for Chat and Agents in light/dark themes at 1440x900 and 600x844: **16 PNG/JSON pairs**. Every pair has hashes, actual viewport/theme/section and scroll-owner geometry. Accepted sidebar output is exactly evidence.json plus these sixteen PNG/JSON pairs; extra files (including hidden/config/log files), directories and links fail closed. Chat uses the inner `.focused-chat-scroll`; Agents uses the outer focused content area. Input checks require actual wheel and native PageDown movement, picker/document stability and Chat header/control stability. Sequential Tab navigation reaches the Chat region through New section; Agents keyboard navigation reaches its first real link. Picker Home/End/Escape focus is checked. Direct peer groups, the custom member link and built-in move-only menus are asserted without menu mutations. Seeding and teardown affect only disposable candidate state.

This is additional candidate evidence, not a replacement for `FocusedSidebarBrowserTests.cs`, the core gate, independent review or the full feature lifecycle qualifications. Synthetic tests validate harness failure behavior; they do not prove the real scenario passes.

## Outputs and failure behavior

The single-capture workflow proves a real active integration-mock run, then navigates to `PagePath` and validates requested selectors. Matrix outputs include the screenshots plus `evidence.json`; focused-sidebar outputs also include hashed geometry sidecars. Missing state, unsupported scenario, incomplete/duplicate captures, failed or non-boolean assertions, missing/corrupt artifacts, hash mismatch or unproven cleanup fails closed.

The runtime has no external network, a read-only root, task-owned tmpfs home/data/profile and a named output volume. Native GNU `timeout` is PID 1: TERM after 1490 seconds, KILL after a further 10 seconds, bounding the whole container to 1500 seconds. A timeout is failure, not partial success. Host cleanup removes only the unique owned container/volume and requires successful exact-name absence inventories before setting `containerRemoved`. CLI errors and survivors fail closed. Temporary image removal is attempted on every outcome. No logs, configuration, secrets or container IDs are copied into accepted artifacts. If build or startup is unavailable, stop early; do not fall back to a host runtime.

## Synthetic checks (no runtime)

```powershell
python -B skills/botnexus-ui-evidence/tests/test_focused_sidebar.py
python -B -m unittest discover -s skills/botnexus-ui-evidence/tests -p test_hardening.py
./skills/botnexus-ui-evidence/tests/Test-BotNexusUiEvidence.ps1
```

The PowerShell module now exposes `Get-UiEvidenceContainerArgument` (formerly `New-UiEvidenceContainerArguments`); all repository callers use the singular, read-only command name. The fixture script has also been renamed to meet the shared executable Verb-Noun naming standard.

## Publication boundary

Artifacts are evidence, not authorization to publish, merge or upload them. Review screenshots and sanitized `evidence.json` before attaching them to an issue or pull request. Real browser execution and independent acceptance remain separate stages.

## Private failure diagnostics

For the focused-sidebar scenario only, a failed browser run attempts a sidebar-only screenshot and a bounded structural DOM projection before browser/fixture disposal. The projection includes trigger expanded/focus state, overlay popover/visibility, menu geometry, scroll-owner geometry, recent scroll/resize/focus/pointer state transitions, and categorized page/console errors. It never writes raw HTML, page text/input values, URLs or raw error/log payloads. The screenshot contains only the task-seeded sidebar, not the conversation/configuration page.

On nonzero container exit, the main host helper attempts to copy only `private-failure/failure.json` and `private-failure/failure.png` into a new `failure-<run>` directory under the caller’s private ScratchDirectory BEFORE removing the exact owned container. Missing diagnostics do not convert failure to success; cleanup is still required. Limits are64KiB JSON and4MiB PNG. No gateway logs, config, home/data or arbitrary directory copy is performed. These private artifacts are diagnostic-only, never passing evidence or publication-ready output. A successful sidebar run still requires exactly the original33 accepted files and no diagnostics directory. Review privacy and output handling before the next isolated run.
