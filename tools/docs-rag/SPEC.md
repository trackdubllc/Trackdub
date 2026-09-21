# Trackdub Docs RAG — Spec

Version: 1.1 (2026-09-21)
Status: Live
Endpoint: `https://trackdub-docs-rag.trackdub.workers.dev`
Owner: Tony Thompson
Code: `api.trackdub/src/docs-rag/` (Worker), `Trackdub/tools/docs-rag/` (ingest)

## 1. What it is

A hosted, bearer-authenticated MCP server backed by Cloudflare AI Search
(AutoRAG) over a curated corpus of Trackdub implementation documentation. It
answers "how does X actually work at Trackdub" from first-party docs plus
pin-accurate vendor docs, for any MCP-capable agent (Cursor, Claude Code, etc).

It is one of three complementary doc sources an agent may use:

| Source | Covers | When to prefer |
|---|---|---|
| **trackdub-docs-rag** (this) | Trackdub first-party docs + pinned vendor docs | Implementation facts, pin policy, provider wiring, "how does Trackdub do X" |
| NVIDIA CUDA MCP (`search_cuda_docs`) | CUDA toolkit internals | CUDA kernel/driver questions outside the RAG corpus |
| `tools/mcp-trackdub-gpu-docs` (local) | Offline TRT-RTX keyword lookup | No-network / fast local lookup; same Trackdub pin docs |

## 2. Architecture

```text
Agent (Cursor / Claude Code)
   |  Authorization: Bearer <DOCS_RAG_TOKEN>
   v
trackdub-docs-rag Worker (api.trackdub, agents-SDK MCP)
   |  tools: search / ask / get_doc
   |  rate limits: burst / steady / ask (per client)
   v
Cloudflare AI Search instance "trackdub-docs"
   |  Authorization: Bearer <AI_SEARCH_API_TOKEN>   (REST, server-side only)
   v
R2 bucket trackdub-docs-corpus  <-- sync_corpus.py (Trackdub repo)
```

- **Auth is two tokens with different jobs.** `DOCS_RAG_TOKEN` (random string,
  we issue it) authenticates the agent to the Worker. `AI_SEARCH_API_TOKEN`
  (real Cloudflare API token, AI Search Edit + Run) authenticates the Worker to
  the Cloudflare REST API and never leaves the Worker.
- **Scopes are enforced at retrieval**, not by trust: the Worker translates the
  `scope` argument into R2 folder range filters on the AI Search REST call
  (`{ "folder": { "$gte": "vendor/nvidia/", "$lt": "vendor/nvidia0" } }`). The
  legacy `aiSearch` binding rejects these filters, so scoped retrieval always
  goes through REST; `ask` generates from those REST-retrieved chunks.
- **First-party precedence** is enforced by the `ask` system prompt and by
  corpus layout: `first-party/**` outranks `vendor/**` on disagreement, and
  vendor `/latest/` pages are treated as possibly newer than the Trackdub pin.

### The pin this corpus is keyed to

TensorRT-RTX shipped as the **standalone ONNX Runtime EP ABI plugin**, version
**0.3.0**, CUDA **cu12** (`runtime/trt-rtx-ep.manifest.json`; Windows bundle
contains `tensorrt_rtx_1_5.dll`). The device name is
`NvTensorRTRTXExecutionProvider` via `RegisterExecutionProviderLibrary`. It is
**not** the Windows ML catalog EP (`NvTensorRtRtxExecutionProvider` spelling is
the deprecated catalog identity). Vendor `/latest/` docs may describe newer
TRT-RTX; the pin and first-party docs win.

## 3. Corpus

R2 bucket `trackdub-docs-corpus`, AI Search instance `trackdub-docs`
(embedding `@cf/qwen/qwen3-embedding-0.6b`, hybrid + reranking, ~6h
auto-reindex). Managed by `tools/docs-rag/sync_corpus.py` + `corpus.v1.json`.

```text
first-party/trackdub/…         public core docs, AGENTS.md, pin manifest, olive recipe READMEs
first-party/trackdub-gated/…   gated repo docs
first-party/api/…              api.trackdub docs
vendor/nvidia/…                TRT-RTX docs, EP ABI release notes, model cards
vendor/microsoft/…             Windows ML, DirectML
vendor/onnxruntime/…           EP docs, perf tuning, quantization, ORT GenAI
vendor/amd/…                   MIGraphX docs
vendor/intel/…                 OpenVINO docs
vendor/qualcomm/…              QNN docs
vendor/qwen/…                  Qwen2.5 / Qwen3-Embedding cards
vendor/whisper/…               openai-whisper, faster-whisper
vendor/speech-models/…         Kokoro, Opus-MT, MADLAD400, Spleeter
```

Refresh from the Trackdub repo root:

```bash
python tools/docs-rag/sync_corpus.py --upload        # stage + fetch + upload
python tools/docs-rag/sync_corpus.py --reuse-staging --upload   # retry upload only
```

Windows-safe (calls wrangler via `node node_modules/wrangler/bin/wrangler.js`,
UTF-8 subprocess decode). After upload, reindex with
`npx wrangler ai-search jobs create trackdub-docs` or wait for the 6h cycle.

Extending: add a vendor entry to `corpus.v1.json` (sources) and, if it needs a
new scope, add it to `DOC_SCOPES` + `FOLDER` in
`api.trackdub/src/docs-rag/scopes.ts` plus a test.

## 4. MCP interface

Streamable HTTP at `POST /mcp` (stateless; fresh `McpServer` per request).
Plain JSON fallbacks: `POST /v1/search`, `POST /v1/ask`, `GET /health`.

All requests need `Authorization: Bearer <DOCS_RAG_TOKEN>`.

### Tools

| Tool | Arguments | Returns |
|---|---|---|
| `search_trackdub_docs` | `query` (required), `scope?`, `limit?` (1–20, default 8) | Ranked chunks: `score`, `key` (R2 key), `text` |
| `ask_trackdub_docs` | same | `answer` (Workers AI `@cf/meta/llama-3.3-70b-instruct-fp8-fast`), `hits`, `searchQuery` |
| `get_trackdub_doc` | `key` (R2 key from search hits), `max_chars?` (default 50000) | Full document text, `truncated` flag |

Agent guidance baked into server instructions: prefer `search` for facts;
`ask` is slower and tighter-limited; scope when you can (`trackdub`,
`trackdub-gated`, `api`, `nvidia`, `microsoft`, `onnxruntime`, …, or `all`).

Full scope list: `all, first-party, trackdub, trackdub-gated, api, vendor,
nvidia, microsoft, amd, intel, qualcomm, qwen, whisper, speech-models,
onnxruntime`.

### Rate limits (per client; clients keyed off a hash of their bearer token)

| Limiter | Window | Cap | Applies to |
|---|---|---|---|
| `burst` | 10 s | 30 | everything |
| `steady` | 60 s | 300 | everything |
| `ask` | 10 s | 5 | `ask_trackdub_docs` / `/v1/ask` only |

Exceeded → `429` with `Retry-After` and `limiter` name. AI Search upstream
backpressure (its own `code 2003`) also surfaces as a clean `429` with
`limiter: "upstream"`. Back off on `Retry-After`; do not retry immediately.

## 5. How to connect an agent

### Cursor

1. **Global** (`~/.cursor/mcp.json`), available in every workspace:

```json
"trackdub-docs-rag": {
  "type": "http",
  "url": "https://trackdub-docs-rag.trackdub.workers.dev/mcp",
  "headers": { "Authorization": "Bearer <DOCS_RAG_TOKEN>" }
}
```

2. **Per-project** (committed so every clone gets it):
   - `Trackdub/.mcp.json` and `Trackdub/.cursor/mcp.json`
   - `Trackdub-gated/.mcp.json`

Then reload MCP servers (Cursor Settings → MCP → reload, or restart).

### Claude Code

```bash
claude mcp add --transport http -s user trackdub-docs-rag \
  "https://trackdub-docs-rag.trackdub.workers.dev/mcp" \
  --header "Authorization: Bearer <DOCS_RAG_TOKEN>"
```

`-s user` = every session. Drop `-s user` while inside a repo to scope it to
that project. Verify with `claude mcp list`.

### Any other MCP client (raw)

Streamable HTTP POST to `/mcp` with the bearer header; standard MCP
`initialize` → `tools/list` → `tools/call`. Tool results are
`content[0].text` JSON with the shapes in section 4.

### Who has access today

Registered globally for Tony's Cursor and Claude Code, and committed into both
product repos for anyone cloning them. The bearer token is shared; if a person
or agent should lose access, rotate the token (section 7) and distribute the
new value to the configs you want to keep.

## 6. Operational notes

- **Deploy** (after code changes): `cd api.trackdub && npm run deploy:docs-rag`
- **Secrets** (`wrangler secret put X --config wrangler.docs-rag.jsonc`):
  `DOCS_RAG_TOKEN`, `AI_SEARCH_API_TOKEN`
- **Non-secret vars**: `AI_SEARCH_INSTANCE` (`trackdub-docs`),
  `AI_SEARCH_MODEL`, `CF_ACCOUNT_ID` (`21cac5947e11018d571c18792118b8b0`)
- **Typecheck/tests**: `npx tsc --noEmit`,
  `npx vitest run test/docs-rag.test.ts` (12 tests, includes rate-limiter and
  filter-boundary coverage)
- **Bugs fixed that are easy to regress**:
  - Folder range upper bound must be `<prefix-without-trailing-slash>0`
    (`vendor0`, not `vendor/0`) because `0` (0x30) sorts after `/` (0x2F);
    keeping the slash made every scope silently return zero hits.
  - Legacy `aiSearch` binding rejects Vectorize-style filters ("Invalid
    input"); scoped retrieval must use the REST endpoint.
  - npm peer-deps: `agents` requires `@modelcontextprotocol/client@2.0.0`,
    `@modelcontextprotocol/server@2.0.0`, `@modelcontextprotocol/sdk@1.30.0`
    (install with `--legacy-peer-deps`; cloudflare/agents#2088).

## 7. Token rotation

1. Generate: `openssl rand -hex 24`
2. Deploy: `npx wrangler secret put DOCS_RAG_TOKEN --config wrangler.docs-rag.jsonc`
3. Update the header value in: `~/.cursor/mcp.json`, `~/.claude.json`
   (`claude mcp remove` + `add`, or edit), `Trackdub/.mcp.json`,
   `Trackdub/.cursor/mcp.json`, `Trackdub-gated/.mcp.json`
4. Commit + push the repo configs.

`AI_SEARCH_API_TOKEN` rotates in the Cloudflare dashboard (AI Search
permissions) and only needs `wrangler secret put AI_SEARCH_API_TOKEN` — no
client config changes.

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `401 unauthorized` from Worker | Wrong/missing `DOCS_RAG_TOKEN` | Re-set secret; check header in client config |
| `429` with `limiter: burst/steady/ask` | Client over its tier | Wait `Retry-After` seconds |
| `429` with `limiter: upstream` | AI Search itself is saturated | Back off ~10s; the burst limiter should usually prevent this |
| Empty scoped hits but `scope=all` works | Filter boundary regression | Check `$lt` has no trailing slash (`vendor0` not `vendor/0`) |
| Everything empty | Indexing stale | `npx wrangler ai-search jobs create trackdub-docs`, check jobs list |
| `Invalid input` on scoped query | Legacy binding used with filters | Scoped retrieval must go through the REST path (`query.ts`) |
| Worker bundling fails on `@modelcontextprotocol/*` | Missing MCP peers | `npm i @modelcontextprotocol/client@2.0.0 @modelcontextprotocol/server@2.0.0 --legacy-peer-deps` |

## 9. Related

- `tools/docs-rag/README.md` — ingest usage
- `tools/mcp-trackdub-gpu-docs/README.md` — local offline MCP
- `docs/reference/tensorrt-rtx-ep-abi-plugin.md` — the TRT-RTX pin
- `docs/decisions/ADR-0002-windows-ml-provider-strategy.md` — provider strategy
- Linear: (issue create was blocked by workspace free-tier limit; reference this spec instead)
