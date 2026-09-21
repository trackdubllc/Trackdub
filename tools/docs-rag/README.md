# Trackdub docs RAG (v1)

Cloudflare AI Search over an R2 corpus. Agents call the `trackdub-docs-rag` Worker. Local keyword MCP in `tools/mcp-trackdub-gpu-docs` stays for offline TRT-RTX lookup.

## Layout in R2 `trackdub-docs-corpus`

- `first-party/trackdub/` public core docs, pin manifest, NvTensorRtRtx olive READMEs
- `first-party/trackdub-gated/` gated docs
- `first-party/api/` api.trackdub docs
- `vendor/{nvidia,microsoft,onnxruntime,amd,intel,qualcomm,qwen,whisper}/` allowlisted pages

`--upload` uploads objects with first-party metadata and requests an indexing job after all uploads succeed. AI Search also syncs every 24 hours as a safety net; requesting a job does not mean indexing has finished.

## One-time Cloudflare setup

From `api.trackdub` (Wrangler 4, logged in):

```powershell
npx wrangler r2 bucket create trackdub-docs-corpus
npx wrangler secret put DOCS_RAG_TOKEN --config wrangler.docs-rag.jsonc
```

In the dashboard, create AI Search instance `trackdub-docs` with data source R2 bucket `trackdub-docs-corpus`. Do not enable the public unauthenticated MCP endpoint. Agents use this Worker, which requires `Authorization: Bearer <DOCS_RAG_TOKEN>`.

Confirm the generation model still exists before relying on answers:

```powershell
npx wrangler ai models
```

Default is `@cf/meta/llama-3.3-70b-instruct-fp8-fast` (`AI_SEARCH_MODEL` in `wrangler.docs-rag.jsonc`).

## Sync

```powershell
python tools/docs-rag/sync_corpus.py
python tools/docs-rag/sync_corpus.py --upload
```

Run from the Trackdub repo. Sibling checkouts `../Trackdub-gated` and `../api.trackdub` are picked up automatically. Override with `TRACKDUB_ROOT`, `TRACKDUB_GATED_ROOT`, `API_TRACKDUB_ROOT`.

Requires Node.js and installed Wrangler in `api.trackdub`, plus Python for staging. `upload_corpus.mjs` opens a temporary Wrangler remote R2 binding, uploads bytes with `customMetadata.is_first_party: "true"` only for `first-party/**`, then disposes the proxy. Vendors omit the field, including `false`, because the boost tests existence. Wrangler 4.114.0's `r2 object put` does not support the `--header` flag shown in AI Search docs.

Both upload and reindex use Wrangler authentication. For an existing OAuth login, unset stale `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_API_KEY` overrides in the calling shell. `AI_SEARCH_API_TOKEN` is a Worker secret, not a Wrangler authentication variable. No secrets are passed as CLI arguments.

`--reuse-staging --upload` resends the cached tree and requests reindex; `--skip-fetch --upload` stages only repo files and does not delete existing vendor objects in R2. Failed uploads skip reindex and exit nonzero. A failed job request also exits nonzero, but a timeout can occur after the job has started: inspect `npx wrangler ai-search jobs list trackdub-docs` before retrying `jobs create` from `api.trackdub`.

Local tests:

```bash
python -m unittest discover -s tools/docs-rag -p test_sync_corpus.py
node --test tools/docs-rag/upload_corpus.test.mjs
```

## Worker

```powershell
cd ../api.trackdub
npm run dev:docs-rag
npm run deploy:docs-rag
```

`POST /mcp` speaks MCP `initialize`, `tools/list`, and `tools/call` for `search_trackdub_docs` and `ask_trackdub_docs`. `POST /v1/search` and `POST /v1/ask` are the same operations over JSON. Optional `scope`: `all`, `first-party`, `trackdub`, `trackdub-gated`, `api`, `vendor`, `nvidia`, `microsoft`, `amd`, `intel`, `qualcomm`, `qwen`, `whisper`, `speech-models`, `onnxruntime`.

Cursor entry after deploy (workers.dev host is printed by Wrangler):

```json
"trackdub-docs-rag": {
  "url": "https://trackdub-docs-rag.<account>.workers.dev/mcp",
  "headers": { "Authorization": "Bearer <DOCS_RAG_TOKEN>" }
}
```
