---
name: AI Search config tune-up
overview: Implementation delivered; known live acceptance deficiencies remain open and do not block this handoff.
todos:
  - id: fix-rewrite
    content: Use one scoped messages-format REST chat request with query rewriting enabled; preserve null when upstream omits search_query.
    status: completed
  - id: auto-reindex
    content: Request indexing automatically after all staged uploads succeed.
    status: completed
  - id: stamp-metadata
    content: Upload first-party R2 objects with is_first_party=true and omit the field for vendors.
    status: completed
  - id: instance-config
    content: Apply 24h sync, trigram tokenizer, boolean metadata schema and existence boost; request indexing.
    status: completed
  - id: search-fallback
    content: Retry successful empty AND retrieval once with OR, preserving scope and limit and reporting matchMode.
    status: completed
  - id: verify
    content: Resolve known indexed-metadata and partial-symbol deficiencies and establish first-party preference.
    status: pending
  - id: docs-commit
    content: Update README, SPEC and HANDOFF; commit and push core and API separately.
    status: completed
isProject: false
---

# AI Search config tune-up: execution status

This is the tracked execution record for the supplied plan from
`Trackdub-gated/external/Trackdub/docs/plans/AI Search config tune-up.md`.
The pinned submodule is unchanged. Known deficiencies are accepted for the
implementation handoff, not represented as passing acceptance checks.

## Delivered implementation

| Plan step | Implementation and evidence |
|---|---|
| Messages-format ask | API `src/docs-rag/query.ts`: one scoped REST chat-completions call supplies both answer and citations; query rewriting enabled; limit nested under retrieval. |
| First-party metadata | Core `tools/docs-rag/upload_corpus.mjs`: temporary Wrangler remote R2 binding preserves source bytes and attaches metadata only to `first-party/**`. All 197 source objects verified: 126 first-party flags, 71 vendors without flags. |
| Automatic indexing | Core `tools/docs-rag/sync_corpus.py`: uploads must all succeed before requesting the manifest-selected instance's indexing job. Failures exit nonzero; uncertain job acknowledgments direct operators to inspect existing jobs. |
| Instance settings | Live readback: `sync_interval=86400`, `keyword_tokenizer=trigram`, boolean `is_first_party` schema, `boost_by=[{field:is_first_party,direction:exists}]`. |
| Empty-search fallback | API `searchDocs`: explicit AND, one OR retry only on empty successful retrieval, unchanged query/scope/limit, `matchMode` returned. Upstream errors are not mistaken for empty results. |
| Publish | Core implementation `a3e3f18`, verification handoff `c751d4a`; API `59037f8`. Both repositories pushed to `agent/rag-tuneup`; no merge to main or PR. |
| Deploy | Worker version `27a4fccc-6c5d-4d0a-8fc7-e64206653515` serves API commit `59037f8`. |

## Verified behavior

- API typecheck and 76 RAG tests pass; core 5 Python and 3 Node tests pass.
- Exact provider/registration-symbol search returns the EP ABI reference and
  ADR-0002 within exactly three scoped results.
- Scoped ask correctly rejects session-bootstrap downloads and names
  `NvTensorRTRTXExecutionProvider`, with exactly three supporting Trackdub chunks.
- Latest index readback, `2026-09-22T03:09:33Z`: all 197 items completed.
- Full API run: 125 main-suite tests and 20 activation tests passed, with two
  baseline Better Auth unhandled rejections. Existing error suppression allowed
  exit 0; this was not a clean full-suite result.

## Known deficiencies and corrected assumptions

- [ ] **Indexed metadata:** 83 of 126 first-party index items still lack the flag,
  despite correct source metadata; all vendor index items omit it correctly.
  Source sync and targeted `INDEX` did not establish recovery. Investigate the
  interrupted configuration-triggered rebuild with Cloudflare; no documented
  force-reingestion flag or guaranteed identical-config retrigger was found.
- [ ] **Partial identifiers:** `RegisterExec` returned no hits after OR fallback.
  Cache-disabled keyword-only retrieval found none; hybrid retrieval without
  reranking found candidates that default reranking removed. No model or
  threshold changes were made to hide this failure.
- [ ] **First-party preference:** `runtime cache` ranked a vendor result first
  and Trackdub second. Existence boost biases candidates before reranking; it is
  not a hard first-party ordering guarantee. Recheck after metadata recovery.
- **First-message rewriting:** Cloudflare uses the first user message as-is.
  This stateless interface has no follow-up history, so the plan's requirement
  to demonstrate an actual rewrite is not achievable merely by enabling it.
  `searchQuery: null` truthfully reports omitted upstream `search_query`.
- **Wrangler upload interface:** installed Wrangler 4.114.0 does not support
  `r2 object put --header`; the remote-binding uploader replaces that plan step.
  Vendor `false` would still satisfy an existence boost, so the field is omitted.
- **Operational follow-up:** Linear access was unavailable; no issue was marked
  Done. No cloud security scan was performed.

Detailed commands, job evidence and recovery notes:
[docs-rag handoff](../../tools/docs-rag/HANDOFF.md).
