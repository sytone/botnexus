# Decision: Unskip AgentPanel Vertical-Slice QA Gates

**Author:** Hermes (QA)  
**Date:** 2026-08-01  
**Issue:** #245 — Portal agent workspace tabs  
**Status:** Active

## Decision

Removed all `[Fact(Skip=...)]` markers from `AgentPanelVerticalSliceTests.cs` and kept the original vertical-slice scope intact:

1. Agent panel shell selector renders (`[data-testid='agent-panel']`).
2. Tab contract remains fixed to `Conversation`, `Workspace`, `Reports`, `Canvas`.
3. Conversation tab remains default-active via `.agent-panel-tab.active[data-tab='conversation']`.
4. Conversation pane still hosts chat parity surface (`[data-testid='agent-panel-conversation'] .chat-panel`).
5. Mobile CSS hooks remain in `app.css`, including `@media (max-width: 768px)` and placeholder styling hooks.

## QA Notes

- Stabilized tab-label assertion to target `.agent-tab-label` instead of full button text (avoids icon-text brittleness).
- Added explicit placeholder-content checks for Workspace/Reports/Canvas copy.
- Hardened CSS file lookup by resolving repository root via `BotNexus.slnx`, avoiding brittle relative traversal from test bin paths.

## Validation

- Targeted Blazor client tests pass with **0 skipped** in `AgentPanelVerticalSliceTests`.
- `dotnet build BotNexus.slnx --nologo --tl:off` passes.
- `dotnet test BotNexus.slnx --nologo --tl:off` passes.
