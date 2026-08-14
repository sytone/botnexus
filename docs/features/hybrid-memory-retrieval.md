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

## Reading the score

Each `memory_search` result renders both its position and the fused magnitude that earned it:

```text
[1] ID: 3f2a...
Score: 0.7412 (rank #1)
```

The number is the ranker's fused `(0.6 x lexical + 0.4 x similarity) x decay` output - the *same*
value that produced the ordering, not a second relevance measure computed for display. Before #2781
the line read `Score: #1 (ranked)`, which was the loop ordinal under a `Score:` label: every result
set ran `#1 ... #N` regardless of match quality, so the strongest and weakest possible matches
rendered identically and a caller had no way to tell a good hit from the best row of a bad set.

The magnitude is **provider-specific and not a fixed 0-1 scale**. It is comparable *within* a result
set, and roughly comparable across queries against the same store, but it is not a probability and
not portable across stores. Calibrate against observed values rather than assuming a threshold.

### `minScore` - applying a relevance floor

`memory_search` accepts an optional `minScore` (number). Results whose fused score falls below it are
excluded, and if nothing clears the floor the tool returns no matches rather than a truncated ranked
list of near-misses:

```text
memory_search(query: "deployment rollback policy", minScore: 0.35)
```

The floor is applied **after** ranking, so it filters exactly the magnitude that is rendered.
Filtering also precedes the `topK` slice, so a floor can never produce a short page padded out of a
larger candidate set.

Because the scale is corpus-dependent, the practical way to pick a floor is to run the query without
one, look at the emitted scores, and set the threshold beneath the last result still worth citing.

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

## Configuring a hosted embeddings endpoint

A provider may **optionally** implement `IEmbeddingProvider`, the embeddings capability:

```csharp
public interface IEmbeddingProvider
{
    string ProviderKey { get; }
    IReadOnlyList<EmbeddingModelDescriptor> Models { get; }
    Task<float[]?> EmbedAsync(string modelId, string text, CancellationToken ct = default);
}
```

It is deliberately **separate** from `IApiProvider`. Chat completion and embeddings are
different endpoints with different model catalogues, and most providers serve only the
former. A provider that does not implement `IEmbeddingProvider` resolves as *absent* — never
as an error — and continues to work unchanged.

`OpenAICompatEmbeddingProvider` covers every endpoint speaking the OpenAI
`POST {baseUrl}/embeddings` shape: Ollama, OpenAI, Azure OpenAI and friends differ only in
`baseUrl` and bearer token. `EmbeddingProviderGenerator` adapts the selected capability to the
`IEmbeddingGenerator` seam above, and composition wires the result into the memory store. No
project reference runs from `BotNexus.Memory` into the provider stack in either direction —
both sides meet only at `Microsoft.Extensions.AI`.

### Configuration

```json
{
  "gateway": {
    "memoryEmbeddings": {
      "enabled": true,
      "provider": "ollama",
      "model": "nomic-embed-text",
      "dimensions": 768,
      "baseUrl": "http://localhost:11434/v1",
      "apiKey": null
    }
  }
}
```

| Field | Meaning |
|---|---|
| `enabled` | Off by default. Enabling it sends memory content to the configured endpoint. |
| `provider` | Provider key the embeddings capability is registered under. |
| `model` | Model identifier as the endpoint expects it. |
| `dimensions` | Declared vector width. A response of a different width is discarded. |
| `baseUrl` | Endpoint base URL; `/embeddings` is appended. |
| `apiKey` | Optional bearer token. Omit for a local endpoint that wants none. |

The section being **absent, `enabled: false`, or incompletely filled in** all resolve to
`MemoryEmbeddingService.Disabled` — the lexical-only behaviour described under
[Degradation](#degradation). A half-configured section degrades rather than throwing, so an
operator mid-way through setup gets a working gateway rather than one that refuses to boot.

### Fingerprint derivation for hosted endpoints

A local model artefact can be fingerprinted by hashing the file. A hosted endpoint offers no
such artefact, so the fingerprint is derived from everything the platform *can* observe that
would change the coordinate space:

```
SHA-256( providerKey ␟ normalisedBaseUrl ␟ modelId ␟ dimensions )   → first 16 hex chars
```

Provider key and base URL are lower-cased and trailing slashes trimmed, so a cosmetic config
edit does not orphan already-stored vectors. Components are joined with a unit separator, so
no two distinct component tuples can collide. Consequently two different hosted models — or
the same model name served by two different deployments — produce different identities and
`EmbeddingIdentity.Matches()` refuses to compare their vectors.

What this deliberately does **not** do is detect a silent vendor-side weight swap behind an
unchanged model name; nothing observable at this layer could. That remains the residual risk
re-embedding addresses.

> **Not yet shipped:** the bundled local ONNX runtime, the curated model registry with
> SHA-256-pinned downloads, and backfill of existing rows. Until an embeddings backend is
> configured, memory retrieval behaves exactly as it did before this change.
