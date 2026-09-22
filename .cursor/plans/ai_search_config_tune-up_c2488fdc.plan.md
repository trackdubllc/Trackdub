---
name: AI Search config tune-up
overview: Fix the query-rewriting no-op bug, wire automatic reindexing into the sync script, and apply the reindex-required instance config wins (trigram tokenizer, 24h sync interval, first-party boost via R2 metadata) in one pass.
todos:
  - id: fix-rewrite
    content: "Fix ask rewriting: switch legacy binding call to messages format in query.ts, deploy, verify search_query shows rewrites"
    status: in_progress
  - id: auto-reindex
    content: Append automatic ai-search jobs create to sync_corpus.py upload path; update README/SPEC
    status: pending
  - id: stamp-metadata
    content: Stamp x-amz-meta-is_first_party on staged uploads in sync_corpus.py (true for first-party/**, absent for vendor/**)
    status: pending
  - id: instance-config
    content: "Update instance via REST: sync_interval 86400, keyword_tokenizer trigram, custom_metadata [is_first_party:boolean], retrieval_options.boost_by [{field:is_first_party,direction:exists}]; trigger full reindex"
    status: pending
  - id: search-fallback
    content: Add keyword_match_mode 'or' retry on empty result set in searchDocs
    status: pending
  - id: verify
    content: Verify instance settings, symbol search, trigram partial-match, first-party boost ordering, rewriting on ask
    status: pending
  - id: docs-commit
    content: Update SPEC.md/HANDOFF.md, commit and push both repos
    status: pending
isProject: false
---

## Answers to the two open questions

**Why boost first-party (real conflicts in this corpus):**
1. EP device name: vendor docs say `NvTensorRtRtxExecutionProvider` (WinML catalog); Trackdub requires `NvTensorRTRTXExecutionProvider` (standalone plugin). ADR-0002 forbids the catalog spelling.
2. Download policy: NVIDIA docs green-light normal TRT-RTX install paths; Trackdub forbids downloads during session bootstrap.
3. ORT GenAI + TRT-RTX: NVIDIA says it "optimizes LLM inference"; Trackdub's manifest hard-excludes it for all GenAI engines (fatal native stack overflow, qwen-instruct).
4. Engine cache: Trackdub has a specific layout and invalidation rules (`%LOCALAPPDATA%\Trackdub\EngineCache\`, `trackdub cache clear engines`); NVIDIA gives generic advice.
5. Olive: generic CLI authoring vs Trackdub's pinned-recipe governance (`trackdub-optimize.ps1`, manifest gates).

Today this precedence is prompt-only (`ask` system prompt) — plain `search` returns vendor chunks interleaved with Trackdub ones. Boosting makes Trackdub rank first structurally.

**Docs sweep findings:**
- Similarity cache was already enabled at instance creation (`close_enough`, 48h TTL) — dropped from plan.
- R2 custom metadata is documented (`x-amz-meta-*` headers at upload + `custom_metadata` schema on instance) — unblocks boost.
- Query rewriting confirmed to apply only to `messages` format — our `ask` uses `query` format, so it is a silent no-op (the bug to fix).
- `sync_interval` supports `86400` (24h).
- `keyword_tokenizer: "trigram"` — "good for partial matches, code, identifiers"; triggers full reindex.
- Boost: `boolean` fields support `exists`/`not_exists` only — `is_first_party: exists` is exactly the needed pattern. Max 5 custom fields (we need 1). Changing schema triggers full reindex.
- `keyword_match_mode` `and|or`, overridable per request.
- Chunking 1024/10 and llama-3.3-70b 24k context are fine at our scale; embedding model (qwen3-embedding, 8k input) is the best available. No changes.
- Items API, namespaces, multitenancy, built-in storage: not applicable.

Current instance state (verified): hybrid on, reranking on, cache `close_enough`/48h, tokenizer `porter`, `sync_interval: 21600`, `custom_metadata: null`, `keyword_match_mode: "and"`.

## Plan

### 1. Fix query rewriting no-op — `api.trackdub/src/docs-rag/query.ts`

In `askDocs`, switch the legacy binding generation call from `query` format to `messages` format so `rewrite_query: true` actually applies (system prompt moves into messages; REST retrieval path unchanged):

```ts
const answer = await env.AI.autorag(env.AI_SEARCH_INSTANCE).aiSearch({
  messages: [
    { role: "system", content: SYSTEM_PROMPT },
    { role: "user", content: request.query },
  ],
  model: env.AI_SEARCH_MODEL,
  max_num_results: request.limit,
  rewrite_query: true,
  ranking_options: { score_threshold: 0.4 },
});
```

Typecheck, deploy, verify `search_query` in ask responses shows rewrites on a vague query.

### 2. Stamp first-party metadata at upload — `Trackdub/tools/docs-rag/sync_corpus.py`

In the upload loop, add `--header "x-amz-meta-is_first_party:true"` when the staged key starts with `first-party/`; omit (or set false) for `vendor/**`. Wrangler `r2 object put` supports `--header`.

### 3. Auto-reindex after upload — same script

After a successful upload run, trigger `jobs create trackdub-docs` automatically via the existing `node wrangler.js` prefix, so `--upload` = one command: stage + fetch + upload + reindex.

### 4. Instance config update (single REST call, then full reindex)

One `PUT /accounts/{account}/ai-search/instances/trackdub-docs` with the existing `AI_SEARCH_API_TOKEN`:

```json
{
  "sync_interval": 86400,
  "indexing_options": { "keyword_tokenizer": "trigram" },
  "custom_metadata": [{ "field_name": "is_first_party", "data_type": "boolean" }],
  "retrieval_options": { "boost_by": [{ "field": "is_first_party", "direction": "exists" }] }
}
```

- `sync_interval: 86400` (24h) — per your call; uploads now trigger reindex themselves, scheduled syncs are just safety nets.
- `trigram` — substring matching for code identifiers (docs: "good for partial matches, code, identifiers"). Triggers full reindex.
- `custom_metadata` + `boost_by` — structural first-party precedence for `search` AND `ask` ranking. Changing schema also triggers full reindex, so all three changes share one reindex.

Then trigger one reindex job and wait for completion.

### 5. Search fallback — `api.trackdub/src/docs-rag/query.ts`

In `searchDocs`, when the strict `keyword_match_mode: "and"` result set is empty, retry once with `"or"` and mark the response (`matchMode: "or-fallback"`). Improves recall for multi-term symbol queries without lowering precision in the common path.

### 6. Verify

- `ai-search get`: `sync_interval: 86400`, tokenizer `trigram`, `custom_metadata` present, `boost_by` present
- Symbol search correctness: `NvTensorRTRTXExecutionProvider RegisterExecutionProviderLibrary` still returns ADR-0002/EP ABI doc
- Trigram partial match: `RegisterExec` returns relevant chunks
- Boost ordering: query with both first-party and vendor matches puts first-party first in plain `search` (e.g. "runtime cache" — TRT-RTX vendor page vs Trackdub EP ABI doc)
- Rewriting: vague `ask` query shows a rewritten `search_query`
- Worker deployed with messages-format ask + or-fallback; tests green (12)

### 7. Docs + commit/push

- SPEC.md: new instance settings table row(s), metadata stamping note, or-fallback behavior, revised rate/config section
- HANDOFF.md: `--upload` includes reindex; note the 24h interval
- Commit `Trackdub` (script + metadata stamping + docs) and `api.trackdub` (query.ts) separately; push both

## Deferred

- `$in` multi-folder scopes: current single-range works; not worth churn.
- Generation/embedding/reranking model swaps: current choices are the best supported.
- Per-agent tokens / Access: unchanged.
