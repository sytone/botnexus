## Summary

Publish the supported out-of-tree extension authoring path from the existing extension guide and link the public GitHub template repository.

The guide now distinguishes repository layout, source and binary checkout references, registration metadata, branch/tag/commit choices, full-trust execution, lifecycle status, last-known-good failure behavior, removal, and verification.

Closes #3905

## Changes

- Link the public, template-enabled `Sytone/botnexus-extension-template` repository from the canonical extension guide.
- Document `BotNexusRepoRoot` source and binary reference shapes without duplicating the full manifest/tool manual.
- Document current CLI and Configuration registration surfaces, requested-ref tradeoffs, trust acknowledgement, and the present metadata-only boundary.
- Document the intended shared lifecycle, last-known-good failure behavior, disable/remove semantics, manual-install pruning risk, and API/tool verification.

## Anti-reinvention

The change reuses the existing extension-development guide, public template, `ExtensionRepositoryCommand`, shared `ExtensionRepositoryRegistryService`, and Configuration UI delivered by the preceding slices. It does not add a second guide, repository model, reconciler, installer, or marketplace.

## Tests

- RED: the canonical guide did not contain the public template URL, `botnexus extensions add`, the full-gateway-trust warning, or last-known-good repository failure behavior.
- GREEN: the focused documentation contract check finds all four requirements.
- Template companion validation proves both reference shapes with the existing four tool tests; no assertion, skip, timeout, threshold, or baseline was weakened.

## Validation

- `npm run docs:build`: passed in 24.71 seconds with 0 dead links and 0 errors. Existing non-fatal syntax-highlighting, Rollup annotation, and chunk-size warnings remain.
- Exact-source remote CORE run `20260923030510-f4bcdeb2`, verified source digest `56d6de4520ebe8c4fe53862264c385919fce63d12613dad0f54cac9d52c3c4ac`: 19,597 total; 19,559 executed and passed; 0 failed; 38 skipped; 0 fixture failures; complete result contract.
- Public template readback: repository is public, `is_template=true`, default branch `main`, and its current main CI is terminal success.

## Risk & rollback

- Documentation describes current metadata-only registration explicitly and labels clone/build/deploy/sync as unavailable until #3844's ordered reconciler work lands.
- Revert this commit to remove the new out-of-tree section without affecting runtime behavior or the public template repository.
- No production code, schema, assertion, skip, threshold, or baseline changed.

## Merge notes

- Companion template PR adds the missing binary-reference shape and dual-shape CI; it should merge before or with this documentation PR so both-shapes wording remains exact.
- No schema migration, deployment-order change, credentials, private URLs, or machine-specific absolute paths are introduced.
- Passing validation does not authorize merge.

## Change profile

| Bucket | Code + | Code - | Comment + | Comment - | Net |
|---|---:|---:|---:|---:|---:|
| docs | 65 | 1 | 0 | 0 | +64 |
| **total** | **65** | **1** | **0** | **0** | **+64** |

<sub>Blank lines discarded. A line that is both code and comment is counted in both columns, so columns do not sum to the raw diff.</sub>

