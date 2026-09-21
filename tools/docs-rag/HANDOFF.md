# Trackdub Docs RAG Handoff

Date: 2026-09-21
Full architecture and agent onboarding: [SPEC.md](SPEC.md).
Ingest setup and commands: [README.md](README.md).

## State: live, tune-up verification incomplete

| Component | Verified state |
|---|---|
| Worker `trackdub-docs-rag` | Deployed API commit `59037f8`; version `27a4fccc-6c5d-4d0a-8fc7-e64206653515` |
| Endpoint | `https://trackdub-docs-rag.trackdub.workers.dev`; health returned `ok: true` |
| Source R2 bucket | 197 uploaded documents: 126 first-party objects with `is_first_party: "true"`, 71 vendor objects with the field absent; zero incorrect source metadata values |
| AI Search settings | 24h sync (`86400`), `trigram` tokenizer, boolean `is_first_party` schema, `exists` boost; read back from the instance |
| Index status at 18:38 UTC | 197 completed, zero queued/running/outdated/error items |
| Indexed metadata | 83 of 126 first-party items still lack `is_first_party`; vendor items correctly omit it |
| Code isolation | Core and API changes on their respective `agent/rag-tuneup` worktree branches; original checkouts preserved |

Completed item status does **not** establish that metadata or tokenizer changes
were applied. Do not report the entire tune-up as verified from the count alone.

## Behavior checks

- Exact symbol search, `NvTensorRTRTXExecutionProvider RegisterExecutionProviderLibrary`,
  first-party scope, limit 3: returned the EP ABI reference and ADR-0002 among
  exactly three hits, `matchMode: "and"`.
- Scoped ask about session-bootstrap downloads: correctly answered no automatic
  download and named `NvTensorRTRTXExecutionProvider`, with exactly three
  Trackdub-scoped source chunks. Answer and citations now use one scoped REST
  chat-completions request, not separate retrieval and generation calls.
- `searchQuery: null` is expected when upstream omits `search_query`. Cloudflare
  does not rewrite the first user message; this stateless interface supplies no
  follow-up history. No actual rewrite was demonstrated.
- `RegisterExec`, all scope, limit 3: still returns zero hits after the single OR
  fallback. Cache-disabled diagnostics found no keyword-only hits, but hybrid
  search without reranking returned candidates; default reranking removed them.
  Partial-symbol acceptance remains unmet. Thresholds and models were not changed.
- `runtime cache`, all scope, limit 5: vendor result first, Trackdub result second.
  Existence boost is not a hard sort guarantee and indexed metadata is incomplete;
  first-party preference has not been established by this check.

## Indexing recovery evidence

All 197 uploads succeeded. The automatic job request returned HTTP 524, but job
`ef4dcfc9-406b-4c90-aa49-7dac6b3f0b15` had started and ended at 18:17:43 UTC.
At that point 23 items were outdated with `workers_ai_out_of_capacity_error`.

After inspecting job and item state, recovery job
`5a408bb6-8322-4860-8049-ea125a1b090a` ran from 18:34:01 to 18:34:40 UTC.
It cleared the capacity errors, but not the 83 missing indexed metadata flags.
A targeted `INDEX` request for the EP ABI document also returned success without
showing the missing metadata on readback. Re-uploading identical source files or
accepting another job is not proof of metadata refresh.

## Verification commands

From the core task worktree:

```bash
python -m unittest discover -s tools/docs-rag -p test_sync_corpus.py
node --test tools/docs-rag/upload_corpus.test.mjs
```

Fresh results: 5 Python tests and 3 Node tests passed. Expected simulated upload
and reindex failures are covered, including skipping reindex on upload failure.

From the API task worktree:

```bash
npm run typecheck
npx vitest run test/docs-rag.test.ts
```

Fresh results: typecheck passed; 76 docs-rag tests passed. Earlier activation
verification passed 20 tests. The earlier full API run passed 125 tests but also
reported two baseline Better Auth unhandled rejections; it was not a clean full
suite. No full .NET build was run for this tooling-only change.

## Recurring operations

- `python tools/docs-rag/sync_corpus.py --upload` stages, fetches, uploads metadata,
  then automatically requests indexing only if every upload succeeds.
- Scheduled sync is every 24h, not 6h. Job acceptance is asynchronous.
- If a job request fails or times out, inspect
  `npx wrangler ai-search jobs list trackdub-docs` from `api.trackdub` before
  retrying. A failed response can still have created a job.
- Check item status **and metadata** through the AI Search Items API in namespace
  `default`; aggregate stats alone previously concealed outdated-item errors.
- Wrangler authentication uses its OAuth login or `CLOUDFLARE_API_TOKEN`, not the
  Worker's `AI_SEARCH_API_TOKEN`. Stale token overrides failed locally; existing
  OAuth worked with those overrides unset for the command only. No secrets rotated.
- Windows: invoke Wrangler through `node node_modules/wrangler/bin/wrangler.js`.
  Child output is inherited, not decoded by Python; use `PYTHONUTF8=1` for Python
  output. The uploader uses a temporary remote R2 binding because installed
  Wrangler 4.114.0 does not support `r2 object put --header`.

## Remaining acceptance work

1. Resolve missing indexed first-party metadata using a supported re-ingestion
   operation, then verify all 126 flags and unchanged vendor omission.
2. Repeat cache-disabled partial-symbol and first-party preference checks after
   index refresh; retain the reranking/threshold caveat if they still fail.
3. Linear tracking was unavailable in this session; no issue was created or
   marked Done. Security scanning was not performed.
