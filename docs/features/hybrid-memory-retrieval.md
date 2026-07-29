# Hybrid memory retrieval

Agent memory search combines two independent signals: lexical relevance (SQLite FTS5/BM25)
and semantic similarity (cosine distance over embedding vectors). This page describes how
the hybrid path behaves, how vector identity is enforced, and what happens when no
embedding model is available.

## Why hybrid

Lexical search can only return rows that share surface terms with the query. A memory that
records the same fact in different words is invisible to BM25 — the paraphrase gap.
Historical analysis of the memory store found the overwhelming majority of `memory_search`
calls returning nothing despite a large indexed corpus, which is the signature of that gap.

Vector similarity closes it, but on its own it is worse than BM25 for exact-term recall.
Hybrid retrieval keeps both.

## Ranking

Each candidate row carries up to three signals:

| Signal | Source | Notes |
| --- | --- | --- |
| Lexical score | `bm25()` on the FTS index, or term-hit count on the LIKE fallback | Clamped to be non-negative |
| Cosine similarity | Query vector vs. stored vector | `null` when no comparable vector exists |
| Age | `created_at` | Feeds the existing exponential temporal decay (30-day half-life) |

The lexical score is normalised by the maximum in the candidate set (scale normalisation,
not min-max — an affine shift would reorder rows once decay multiplies through). Similarity
is mapped from `[-1, 1]` into `[0, 1]`. The two are fused with a 0.6 / 0.4 weighting and
then multiplied by the temporal decay factor.

A row with **no comparable vector** receives a neutral similarity prior of `0.5` rather
than being treated as dissimilar. This matters: without it, enabling embeddings would
instantly bury every row written before the rollout, including exact lexical matches.

### What the weighting does and does not do

Hybrid retrieval improves recall by *surfacing rows BM25 never returned at all* (lexical
score `0`), not by outranking strong lexical hits. A dominant exact-term match is
deliberately not displaced by similarity alone. This is what keeps exact lexical matches
competitive.

## Vector identity

Vectors from different models — or different builds of the same model — occupy unrelated
coordinate spaces. A cosine similarity computed across identities is numerically valid and
semantically meaningless, so **identity is a hard precondition for every comparison**.

An identity has three components, all of which must match:

- **Model ID** — registry name, e.g. `nomic-embed-text-v2`
- **Model fingerprint** — version or content hash of the exact artefact. A re-quantised or
  re-exported model keeps its name but produces incomparable vectors, so the fingerprint is
  part of identity rather than mere metadata.
- **Dimensions** — vector width

Identity is stored **inline** in the `memories.embedding` BLOB alongside the vector, not in
a side table. A stale or missing join could otherwise separate a vector from its identity
and silently permit a cross-identity comparison.

The BLOB envelope is self-describing and validated on read. Any payload that is foreign,
truncated, over-long, or internally inconsistent (declared dimensions not matching the
actual byte count) is reported as undecodable. Such a row simply contributes no similarity
evidence — it is never guessed at.

## Degradation

Retrieval degrades to BM25-only, with no error surfaced to the caller, whenever:

- no embedding generator is configured;
- the model cannot be loaded or fails at generation time;
- the model returns a vector of unexpected width (the vector is discarded rather than
  stored under a declared identity it does not match);
- the stored BLOB is corrupt or undecodable;
- every stored vector carries a different identity than the active model.

When **no** candidate in a result set carries a similarity, the ranking collapses to
exactly the original `lexical × exp(-λ · age)` formula. Degradation is not a
near-equivalent code path; it is the original path.

Memory **writes** degrade the same way: a failure to generate a vector never fails the
write. The row is stored without an embedding and remains fully retrievable through BM25.

## Scope, filters and decay

The vector scan applies the *same* filter predicates as the lexical path — source type,
session, date range, tags, and the archived-row exclusion — from a single shared
code path, so the two halves of hybrid retrieval cannot silently diverge. Agent scoping is
unchanged: each agent has its own store, and shared-store access still goes through the
shared memory registry.

## Performance

v1 uses a brute-force, agent-scoped cosine scan. Cost is linear in the number of embedded
rows for that agent, bounded by a configurable row ceiling (default 5,000, newest-first) so
the worst case stays finite. The FTS path is unaffected by this ceiling. ANN indexes
(`sqlite-vec`, Qdrant) are explicitly out of scope for this slice.

## Providing an embedding generator

The store depends on `IMemoryEmbeddingService`, which wraps the provider-neutral
`Microsoft.Extensions.AI` abstraction
`IEmbeddingGenerator<string, Embedding<float>>`. Any provider satisfying that interface —
local ONNX, a hosted service, or a test double — can be supplied along with the
`EmbeddingIdentity` describing it.

When no generator is supplied the store uses `MemoryEmbeddingService.Disabled`, which is
the supported degraded mode rather than an error condition.

> **Not yet shipped:** the bundled local ONNX runtime, the curated model registry with
> SHA-256-pinned downloads, and backfill of existing rows. Until a generator is registered,
> memory retrieval behaves exactly as it did before this change.
