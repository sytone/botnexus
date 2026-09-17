## Summary

Keep resizable desktop panes within their configured fraction when their flex container changes size. The visible pane contracts immediately and restores the user's persisted preferred width when space returns.

Closes #4049

## Root cause

The shared splitter converted the preferred width to a fixed pixel flex basis only during initialization and drag events. It did not observe later container geometry changes, so a valid width from a wide layout could exceed the configured fraction after narrowing.

## Changes

- Observe each splitter's actual flex container and reapply configured bounds whenever its size changes.
- Preserve the user's preferred width separately from temporary resize clamping so later expansion restores it.
- Make containment win when a container is too narrow to satisfy both the pixel minimum and fractional maximum.
- Preserve the keyboard and ARIA splitter behavior added on `main`, including accessible values updated after pointer, keyboard, and container-driven resize.
- Disconnect observers alongside existing pointer and keyboard handlers on reinitialization and disposal.

## Anti-reinvention

This extends the existing `BotNexus.splitter` lifecycle used by the sidebar, workspace, reports, and skills panels rather than adding sidebar-specific resize code. Source searches covered every `BotNexus.splitter.init` caller and existing panel interop coverage; no new production abstraction was necessary.

## Tests

`SplitterResizeTests.Container_resize_reapplies_bounds_without_losing_preferred_width` executes production `splitter.js` under Node with a deterministic rendered DOM boundary. It covers wide initialization, narrow-container clamping without persistence loss, expansion restoration, conflicting min/max bounds, reinitialization cleanup, and disposal cleanup.

After synchronization with `main`, the inherited `SplitterKeyboardAccessibilityTests` fixture was extended to model the production `ResizeObserver` API. Its keyboard, pointer, and ARIA assertions remain intact. No assertion or skip was weakened.

## Validation

- Focused splitter-resize Node harness passed after conflict resolution.
- Blazor client test project build passed with 0 warnings and 0 errors.
- Exact-source remote CORE: run `20260917175105-2d5f94e1`, source digest `59e4eb2d758f784d77c830fae487d335b93513ed7afdca246b1ed2ca72bc5354`, 19,380 total; 19,342 executed; 19,342 passed; 0 failed; 38 environmental skips; 0 fixture failures; complete result contract.
- A preceding exact-source run exposed one unrelated nondeterministic session telemetry assertion after all 1,916 Blazor client tests passed. A bounded unchanged-source retry passed the entire CORE gate.

## UI evidence

![Deterministic rendered comparison of expanded and narrowed splitter states](https://github.com/sytone/botnexus/releases/download/pr-4049-ui-evidence/sidebar-resize-evidence.png)

This is a deterministic rendering of the production-JavaScript regression values, not a live gateway capture. The executable DOM test restores an 800 px preference in a 2,000 px container, clamps it to 400 px after narrowing to 1,000 px, and restores 800 px after expansion without changing local storage. At 300 px, the visible pane resolves to the 120 px fractional maximum rather than overflowing to the 180 px minimum. Browser-emulated E2E remains quarantined repository-wide under #3601.

## Risk & rollback

- The shared helper changes all current splitter consumers; observing their own container is intentional and the regression covers generic lifecycle and bound semantics. Remaining uncertainty is browser-specific `ResizeObserver` delivery timing, handled by the platform API rather than custom scheduling.
- Rollback is a single revert of this PR.
- Test coverage is additive. The synchronized keyboard fixture only adds the browser API required by production; no assertion, threshold, baseline, or skip was weakened.

## Merge notes

No migration, new store, dependency, or merge ordering is required. The branch was synchronized by merge commit without rewriting published history. Passing validation and checks does not authorize merge; explicit human approval remains required.
