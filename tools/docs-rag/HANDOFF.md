# Trackdub Docs RAG Handoff

Date: 2026-09-22 (UTC)
Architecture and onboarding: [SPEC.md](SPEC.md).
Ingest commands: [README.md](README.md).
Plan milestones: [execution status](../../docs/plans/AI%20Search%20config%20tune-up.md).

## State: metadata repaired; follow-up code local; capacity failures remain

| Component | Verified state |
|---|---|
| Worker `trackdub-docs-rag` | Still deployed API commit `59037f8`, version `27a4fccc-6c5d-4d0a-8fc7-e64206653515`; follow-up code below is not deployed |
| Endpoint | `https://trackdub-docs-rag.trackdub.workers.dev` |
| Source R2 bucket | 197 objects: 126 first-party string `is_first_party: "true"` values, 71 vendors without the field |
| Indexed metadata | All 126 first-party boolean flags present; no vendor flags; every indexed scope folder matches its key |
| Latest verifier inventory | 197 source objects and 197 unique indexed items with matching keys and sizes; 25 outdated items report `workers_ai_out_of_capacity_error` |
| AI Search settings | 24h sync (`86400`), trigram tokenizer, boolean `is_first_party` schema, existence boost; temporary `metadata_refresh` field removed |
| Code isolation | Core and API `agent/rag-tuneup` worktrees preserved; follow-up changes uncommitted and unpushed; original checkouts and gated submodule unchanged |

The verifier correctly exits 1 for the remaining capacity failures. Metadata
repair does not imply current content is fully indexed. The earlier observation
of 197 completed items preceded the configuration rebuild and is not current.

## Follow-up implementation

- `sync_corpus.py` stages a separate candidate tree. Missing repositories,
  repositories with no eligible documents, and failed/empty vendor fetches stop
  before upload or reindex and retain the previous cache. Successful staging
  replaces the cache. If promotion and rollback both fail, the backup survives
  at `.staging.previous`. Existing size limits and intentional `--skip-fetch`
  behavior remain. Remote uploads are not transactional.
- `verify_index.mjs` checks source/index key identity, duplicates, source and
  indexed first-party flags, scope folders, item status/errors, pending actions,
  chunks, sizes and last-seen timestamps. It refuses active/changing jobs and
  checks the source inventory again. A clean inventory also requires an uncached
  canonical-symbol content canary. Authentication is bounded to 20 seconds and
  API checks share a 90-second deadline. It never uploads or creates a job.
- API `searchDocs` retains AND then OR. Only if both are successfully empty and
  the query is a code-like identifier does it request up to 20 scoped hybrid
  candidates with cache and reranking disabled for that request. Returned text
  must contain a literal identifier-prefix match; output remains capped by the
  caller's limit. `matchMode: "symbol-fallback"` exposes the path. Global models,
  thresholds and ranking settings are unchanged.
- Stateless ask remains unchanged. Cloudflare does not rewrite the first user
  message. Enabling rewriting provides no demonstrated benefit without history;
  `searchQuery: null` honestly reports an omitted upstream field.

## Live behavior checks

These search checks ran the modified local `searchDocs` against live Cloudflare,
not the deployed Worker:

- `RegisterExec`, scope `all`, limit 3: `symbol-fallback`, three literal matches
  from ONNX Runtime plugin EP and Microsoft WinML documentation.
- `RegisterExec`, scope `first-party`, limit 1: `symbol-fallback`, one literal
  match from `first-party/trackdub/docs/reference/tensorrt-rtx-ep-abi-plugin.md`.
- `NvTensorRTRTXExecutionProvider RegisterExecutionProviderLibrary`, scope
  `first-party`, limit 3: ordinary `and`, exactly three hits headed by the EP ABI
  reference and ADR-0002. Exact-symbol retrieval did not regress.
- Cache-disabled diagnostics after metadata repair still found no reranked
  `RegisterExec` hits; unreranked hybrid retrieval supplied literal candidates.
  The fallback is bounded recovery, not exhaustive substring search. Its scores
  are not comparable to reranked scores.
- Uncached `runtime cache`, all scope, limit 5, after schema cleanup: NVIDIA's
  first-engine guide ranked first and Trackdub's reference index second. Full
  metadata does not turn the existence boost into a first-party-first sort.

Earlier deployed scoped ask correctly rejected automatic session-bootstrap
provider downloads and named `NvTensorRTRTXExecutionProvider` with three
Trackdub-scoped citations. No actual query rewrite was demonstrated.

## Indexing recovery evidence

The original upload's HTTP 524 still created job
`ef4dcfc9-406b-4c90-aa49-7dac6b3f0b15`, ending 2026-09-21 at 18:17:43 UTC.
Recovery job `5a408bb6-8322-4860-8049-ea125a1b090a` cleared capacity errors at
18:34:40 UTC, but source sync and targeted `INDEX` left 83 metadata flags missing.
Job acknowledgment and completed counts did not prove metadata refresh.

Adding temporary boolean schema field `metadata_refresh` triggered configuration
rebuild `53d92de9-cd0e-4c39-bd95-5f1917cda8b0`, running 2026-09-22 from 03:44:48
until 04:01:09 UTC. A stable 197-unique-item listing then showed all 126 flags,
zero vendor flags, 127 completed items and 70 outdated capacity/timeout items.
Removing the temporary field triggered rebuild
`cb55060e-fde2-4e4a-97d8-5acc9cd91903`, running from 04:03:07 to 04:05:51 UTC.
At 04:15:23 UTC, readback confirmed only the permanent boolean schema field,
86400-second sync and trigram tokenizer. No source bytes were changed for metadata
repair, no items were deleted and no model/threshold changes hid failures. The
post-cleanup full verifier run found 25 remaining outdated capacity items and no
other inventory/metadata issues.

R2 terminal list responses omit `result_info`; truncated responses include a
cursor and `is_truncated: true`. Both shapes were confirmed live and covered by
regressions. A local review also caught missing scope-folder validation, now
covered by missing, incorrect and missing-trailing-slash regression cases.

## Verification commands and results

From the core task worktree:

```bash
python -m unittest discover -s tools/docs-rag -p test_sync_corpus.py
node --test tools/docs-rag/upload_corpus.test.mjs tools/docs-rag/verify_index.test.mjs
node tools/docs-rag/verify_index.mjs --account <account-id> --api-root ../api.trackdub
```

15 Python tests and 32 Node tests pass. Simulated staging, rollback, upload and
reindex failures are intentional test cases. Live verification exits 1 for the
25 capacity-error items, not a pagination or authentication failure. Its canary
is skipped while inventory issues exist; the independent search checks above
are not a substitute for full index readiness or every chunk's byte freshness.

From the API task worktree:

```bash
npm run typecheck
npx vitest run test/docs-rag.test.ts
npm run test:all
```

Typecheck and all 87 docs-rag tests pass. Full API run: 136 main-suite tests and
20 activation tests pass, but two existing Better Auth unhandled rejections
remain. Existing `dangerouslyIgnoreUnhandledErrors` permits exit 0; this is not
a clean full-suite result. No .NET/UI code changed; no .NET build was run.

## Operational follow-up

1. Inspect existing jobs before any recovery request; a timeout may still have
   started a job. Do not interrupt an active rebuild or blindly repeat schema
   toggles. Resolve remaining Cloudflare capacity failures, then rerun the
   verifier until it reports no issues and passes its content canary.
2. First-party preference is not a hard final ordering guarantee; existence
   boost applies before reranking. Do not equate complete flags with guaranteed
   first-party ordering.
3. Commit/push and deploy the follow-up only when authorized. Until deployment,
   the hosted API does not include the new partial-symbol fallback.
4. Linear access was unavailable; no issue was marked Done. No cloud security
   scan or cloud code review was performed.

Wrangler uses OAuth or `CLOUDFLARE_API_TOKEN`, not the Worker's
`AI_SEARCH_API_TOKEN`. Unset stale overrides in the calling shell when using
OAuth; no credentials were rotated. Windows uses `node wrangler.js` and inherited
child output; use `PYTHONUTF8=1` for Python output. The remote-binding uploader
remains necessary because Wrangler 4.114.0 lacks `r2 object put --header`.
