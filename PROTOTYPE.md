# Conversation recovery prototype — not ready for use

This branch preserves an exploratory idea associated with [issue #5176](https://github.com/sytone/botnexus/issues/5176). It is not a proposed implementation PR, a supported client contract, a deployment candidate or evidence of passing validation. No PR should be opened for this snapshot.

## Generic platform idea

Separate workflow policy from runtime admission. A caller decides what work should continue; the platform could provide a narrowly scoped operation to attempt one continuation of an exact conversation/session without racing ordinary message admission.

The prototype explores queue reservations, pending-input checks, command uncertainty, duplicate-operation records and actual processing callbacks. These mechanisms contain no inner-loop issue selection, phase or SQL policy. Other authorized workflows could potentially use a future supported API/tool, subject to existing permissions.

## Known limitations and open questions

- This changes central queue and maintenance behavior, including ordinary callers. Its concurrency and shutdown implications need more understanding and independent review.
- Command tracking survives handle recreation only within one gateway process, not gateway restart. Agent/workspace owner granularity is too broad for precise task/attempt recovery.
- Some never-started command paths retain unnecessary uncertainty. Descendant-process termination and supported reconciliation are incomplete.
- Recovery status strings need a reviewed typed contract. Session metadata retention is capped without a complete reconciliation/archival lifecycle.
- Only resident in-process sessions supply the proposed checks. There is no finished public agent tool, UI, unloaded-session recovery or conversation replacement mechanism.
- Receipt completion is not product acceptance or proof that every background command stopped. Controller substitutes are not end-to-end command-lifecycle proof.
- The final command-ledger changes were unfinished when captured. No qualifying exact-snapshot full validation is claimed. Historical targeted results must not be presented as validation of this snapshot.

## Inner-loop direction is separate

The shared inner-loop skill should first use a pragmatic recovery indication: SQL still records allocated work, no result is recorded for the current assignment/attempt, and its exact agent loop has remained stopped without recent activity beyond a grace period. This is a heuristic, not termination proof.

Keep live loops, declared waits, protected approvals, saved results, uncertain sends, identity changes and potentially surviving commands visible. Prefer inspecting preserved state and continuing the same assignment/conversation; do not silently create a replacement writer. A rare missed indication is acceptable for the MVP. Repeated loop stops should lead to fixing the underlying runtime bug rather than expanding recovery into a second platform.

This generic platform prototype is not a prerequisite for that read-only assessment. Start/continuation policy and any future scheduling remain separate decisions. No gateway restart, deployment, schedule activation, merge or PR is authorized by this branch.
