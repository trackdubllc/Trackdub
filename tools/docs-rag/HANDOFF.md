# Trackdub Docs RAG — Handoff

Date: 2026-09-21
This file is the quick handoff. Full architecture, agent onboarding, rate
limits, and token rotation live in [SPEC.md](SPEC.md). Ingest usage lives in
[README.md](README.md).
## State at handoff: LIVE

Everything below is deployed, verified, and committed.

| Component | State |
|---|---|
| R2 `trackdub-docs-corpus` | 183 objects (126 first-party + 38 vendor), indexed |
| AI Search `trackdub-docs` | Live, hybrid + reranking, ~6h auto-reindex |
| Worker `trackdub-docs-rag` | Live at `https://trackdub-docs-rag.trackdub.workers.dev` |
| MCP tools | `search_trackdub_docs`, `ask_trackdub_docs`, `get_trackdub_doc` — all verified |
| Rate limits | burst 30/10s, steady 300/60s, ask 5/10s — verified clean 429s |
| Agents wired | Cursor global + both repos (committed), Claude Code user scope |
| Code | Pushed: `api.trackdub` `c7d5ee0`, `Trackdub` `9615ec5`, `Trackdub-gated` `e7b2d06` |

## Recurring ops

- Refresh corpus: `python tools/docs-rag/sync_corpus.py --upload`
  (then `npx wrangler ai-search jobs create trackdub-docs` to reindex now)
- Redeploy Worker: `cd ../api.trackdub && npm run deploy:docs-rag`

## If something breaks

See SPEC.md section 8. Fastest triage:

1. `curl https://trackdub-docs-rag.trackdub.workers.dev/health` — is the Worker up?
2. `npx wrangler ai-search jobs list trackdub-docs` — did indexing finish?
3. Check which secret the error implicates (`DOCS_RAG_TOKEN` = client auth,
   `AI_SEARCH_API_TOKEN` = Cloudflare REST).

## Windows gotchas (sync script, already handled)

- Wrangler must be invoked via `node node_modules/wrangler/bin/wrangler.js`,
  not the `.bin/wrangler` shim (POSIX-only, `WinError 193`)
- Subprocess output must be decoded UTF-8 (Wrangler emoji breaks cp1252)
