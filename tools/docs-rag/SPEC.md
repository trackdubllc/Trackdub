# Trackdub Docs RAG — Spec

Version: 1.3 (2026-09-21)
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
R2 bucket trackdub-docs-corpus
```

Two credentials, different jobs:

| Secret | What it is | Who presents it | Rotate by |
|---|---|---|---|
| `DOCS_RAG_TOKEN` | Random string (>=16 chars), no Cloudflare meaning | Agents -> Worker (in `mcp.json` / `.claude.json`) | `wrangler secret put DOCS_RAG_TOKEN` + update agent configs |
| `AI_SEARCH_API_TOKEN` | Real Cloudflare API token (Account > AI Search Edit + Run) | Worker -> AI Search REST API, server-side only | Rotate in dashboard, `wrangler secret put AI_SEARCH_API_TOKEN` |

## 3. The corpus

R2 bucket `trackdub-docs-corpus` contains **197 uploaded documents**. Instance
`trackdub-docs` indexes them with `@cf/qwen/qwen3-embedding-0.6b`, hybrid search
and reranking, 24h scheduled sync, and a job request after successful uploads.
An uploaded object or ended job does not prove the index is current; inspect
item status and metadata as well. See [HANDOFF.md](HANDOFF.md) for dated verification.

### Instance settings

| Setting | Value |
|---|---|
| Scheduled sync | `sync_interval: 86400` (24h) |
| Keyword tokenizer | `trigram`, for code identifier substrings |
| Keyword match mode | `and`; Worker retries `or` once only on an empty successful search |
| Custom metadata | `is_first_party`, boolean |
| Relevance boost | `is_first_party`, direction `exists` |
| Chunking | 1024 tokens, 10% overlap (unchanged) |
| Similarity cache | `close_enough`, 172800 seconds (48h, unchanged) |

First-party objects carry R2 custom metadata `is_first_party: "true"`;
vendors omit that field entirely. Setting `false` would still match the
existence boost. Boosting biases candidates before reranking; it is not a
hard guarantee that every first-party hit outranks every vendor hit. Changing
the tokenizer or metadata schema requires full reindexing.

| Folder | Contents |
|---|---|
| `first-party/trackdub/` | Public core docs, AGENTS.md, TRT-RTX pin manifest, olive-recipe NvTensorRtRtx READMEs |
| `first-party/trackdub-gated/` | Gated repo docs |
| `first-party/api/` | api.trackdub docs |
| `vendor/nvidia/` | TRT-RTX docs (arch, AOT/JIT, support matrix, best practices, troubleshooting, APIs) + EP ABI releases + Sortformer/Nemotron model cards |
| `vendor/microsoft/` | Windows ML (overview, EP selection, initialization), DirectML |
| `vendor/onnxruntime/` | EP docs (TRT-RTX, plugin EP, CUDA, DirectML, OpenVINO, QNN, MIGraphX), ORT GenAI, performance tuning, quantization |
| `vendor/olive/` | Microsoft Olive docs (the pinned 0.3.0-cu12 recipe engine) |
| `vendor/amd/` | MIGraphX (install, driver, C++ API, operators, quantization) |
| `vendor/intel/` | OpenVINO (get started, workflow, generative) |
| `vendor/qualcomm/` | QNN (overview, integration guide, backends) |
| `vendor/qwen/` | Qwen2.5-1.5B and Qwen3-Embedding model cards |
| `vendor/whisper/` | OpenAI Whisper + faster-whisper |
| `vendor/speech-models/` | Kokoro-82M, Opus-MT, MADLAD400, Spleeter |

### Model-family coverage check (v1.2)

All `bundled-models.manifest.json` engine families that ship vendor docs:
whisper (genai+onnx), qwen (asr/instruct/tts), phi-genai, kokoro, chatterbox,
cosyvoice, opus-mt, madlad, nemotron-asr, sortformer, spleeter, silero-vad,
kokoro/qwen3-tts (HF cards), olive recipes. **Known thin spots**: Silero VAD
upstream wiki, DeepFilterNet3, SepFormer, CosyVoice/Chatterbox HF cards
(401-anonymous), LatentSync. Add to `corpus.v1.json` `vendors` + sync.

### Keeping it fresh

From the Trackdub repo root:

```bash
python tools/docs-rag/sync_corpus.py --upload          # fetch + upload + request reindex
python tools/docs-rag/sync_corpus.py --skip-fetch --upload  # repo files only + request reindex
python tools/docs-rag/sync_corpus.py --reuse-staging --upload  # cached tree + request reindex
```

Uploads use a temporary remote R2 binding through installed Wrangler's
`getPlatformProxy`, with metadata attached through the R2 Workers API.
Wrangler 4.114.0 does not implement the `r2 object put --header` option
shown in AI Search documentation. The helper disposes its proxy and temporary
configuration after each run; no persistent service or new credentials are needed.

Automatic reindex runs only after all staged objects upload successfully.
Missing repository roots and vendor-fetch failures do not block uploading the
remaining staged files or a successful exit. Check the staging summary for
skips and failures; upload success alone does not establish a complete refresh.
Upload or job-request failure exits nonzero. Job creation is asynchronous;
verify completion separately. A timeout can occur after the job has started,
so inspect jobs before retrying the request:

```bash
cd ../api.trackdub
npx wrangler ai-search jobs list trackdub-docs
# Only if no job was created:
npx wrangler ai-search jobs create trackdub-docs
```

Wrangler uses `CLOUDFLARE_API_TOKEN` or its existing OAuth login, not
`AI_SEARCH_API_TOKEN`. Unset stale `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_API_KEY`
overrides in the calling shell when using OAuth. No environment settings or
credentials are changed by the sync script.

Adding a vendor page: add `[id, url]` under the vendor in
`corpus.v1.json`, run sync. Adding a new scope (agent-visible filter): update
`DOC_SCOPES`, `FOLDER`, and the `filterForScope` switch in
`api.trackdub/src/docs-rag/scopes.ts`, then add a scope test row.

Windows notes: the script invokes Wrangler via
`node node_modules/wrangler/bin/wrangler.js` (the `.bin` shim is POSIX-only).
Child output streams directly to the terminal without Python decoding;
use `PYTHONUTF8=1` for Python's own output.

## 4. Agent tools

Stateless agents-SDK MCP server (`createMcpHandler`, streamable HTTP at
`POST /mcp`). Fresh `McpServer` per request (SDK >= 1.26 CVE guidance).

| Tool | Input | Returns |
|---|---|---|
| `search_trackdub_docs` | `query`, `scope?`, `limit?` (1-20, default 8) | Ranked chunks: `score`, `key` (R2 path), `text` (<=4000 chars). Hybrid vector+keyword, reranked. |
| `ask_trackdub_docs` | `query`, `scope?`, `limit?` | AI answer + scope-enforced source chunks. Prompt prefers first-party over vendor; refuses to invent readiness/APIs. |
| `get_trackdub_doc` | `key`, `max_chars?` (default 50000) | Full document text straight from R2 (4MB cap). Use when chunks are truncated. |

`scope` values: `all`, `first-party`, `trackdub`, `trackdub-gated`, `api`,
`vendor`, `nvidia`, `microsoft`, `amd`, `intel`, `qualcomm`, `qwen`,
`whisper`, `speech-models`, `onnxruntime`. Olive documents are available through
`vendor` or `all`; a dedicated Olive scope is not implemented.

Scope enforcement: filters are ASCII range queries on the AI Search `folder`
metadata (`$gte: "vendor/nvidia/", $lt: "vendor/nvidia0"` — note the stripped
trailing slash; `"vendor/nvidia/0"` would sort above every real value and
match nothing). Retrieval always goes through the REST API for this; the
legacy `aiSearch` binding rejects folder range filters.

`search` explicitly requests keyword mode `and`, then retries once with `or`
only if the successful response contains no chunks. Both requests keep the
same scope and result limit. Responses include `matchMode: "and"` or
`"or-fallback"`. Upstream errors and rate limits are propagated, never retried
as empty results. With hybrid search, this fallback relaxes only the keyword
candidate requirement, not vector similarity thresholds.

`ask` makes one REST chat-completions call with system/user `messages`,
`ai_search_options.query_rewrite.enabled: true`, and scope/limit inside
`ai_search_options.retrieval`. Returned hits are the same chunks supplied to
generation, not a separate search's results. `searchQuery` is null when the
upstream chat response does not expose `search_query`.

**Query rewriting limitation:** Cloudflare rewrites follow-up messages, not
the first user message. This stateless, single-query tool does not supply
conversation history, so enabling the option does not establish that a rewrite
occurred. Do not fabricate history or report the original query as evidence of
rewriting. See [Cloudflare query rewriting](https://developers.cloudflare.com/ai-search/configuration/retrieval/query-rewriting/).

## 5. Rate limits

Per client (key = SHA-256-ish hash of bearer token, never the raw value):

| Binding | Window | Cap | Applies |
|---|---|---|---|
| `RL_BURST` | 10s | 30 | all endpoints |
| `RL_STEADY` | 60s | 300 | all endpoints |
| `RL_ASK` | 10s | 5 | `/v1/ask` + `ask` tool only |

Responses: HTTP 429 with `Retry-After` and `{"error":"rate_limited",
"limiter":"burst|steady|ask|upstream"}`. AI Search's own backpressure
(code 2003) is re-mapped to the same clean 429. Note the binding counters are
per-Cloudflare-location (eventual consistency), so these are abuse caps, not
exact quotas.

## 6. How to connect an agent

Endpoint: `https://trackdub-docs-rag.trackdub.workers.dev/mcp`
Auth: `Authorization: Bearer <DOCS_RAG_TOKEN>` on every request.

### Cursor (project, committed)

`Trackdub/.mcp.json` and `Trackdub-gated/.mcp.json` already contain:

```json
"trackdub-docs-rag": {
  "type": "http",
  "url": "https://trackdub-docs-rag.trackdub.workers.dev/mcp",
  "headers": { "Authorization": "Bearer <DOCS_RAG_TOKEN>" }
}
```

### Cursor (global)

`~/.cursor/mcp.json`, same shape.

### Claude Code

Already registered in user scope:

```bash
claude mcp add -s user --transport http trackdub-docs-rag \
  "https://trackdub-docs-rag.trackdub.workers.dev/mcp" \
  --header "Authorization: Bearer <DOCS_RAG_TOKEN>"
```

### Other MCP clients (generic)

Streamable HTTP at `/mcp`; initialize handshake returns serverInfo
`trackdub-docs-rag` v0.2.0. Any client that speaks
`Authorization` headers + streamable HTTP works. Raw HTTP alternative:
`POST /v1/search` and `POST /v1/ask` (`{"query","scope","limit"}`).

### Adding a *new agent identity* (per-agent tokens)

Currently one shared token. To give an agent its own quota/revoke key:
generate a token per agent, store an allowlist mapping
`hash(token) -> name` (env var or KV), key the rate limiter on that hash, and
revoke by removal. Not built yet; the limiter key derivation already avoids
storing raw tokens so the extension is small (`auth.ts`).

## 7. Operations quick reference

```bash
# Deploy the Worker
cd api.trackdub && npm run deploy:docs-rag

# Health check
curl https://trackdub-docs-rag.trackdub.workers.dev/health

# Refresh corpus + automatically request reindex (from Trackdub)
python tools/docs-rag/sync_corpus.py --upload

# Inspect index state (from api.trackdub)
npx wrangler ai-search get trackdub-docs
npx wrangler ai-search jobs list trackdub-docs

# Force-sync without CLI
# Dashboard -> AI Search -> trackdub-docs -> Sync
```

## 8. If something breaks

| Symptom | Likely cause | Fix |
|---|---|---|
| `unauthorized` from Worker | Wrong `DOCS_RAG_TOKEN` header | Re-set secret + update agent config |
| 429 `limiter: upstream` often | Corpus too hot / shared key | Space out; or add per-agent tokens (section 6) |
| Empty scoped results but `all` works | Indexing lag after adding docs | Trigger `jobs create`; check `jobs list` |
| `AI Search REST 401` in Worker logs | `AI_SEARCH_API_TOKEN` rotated/expired | Create token w/ AI Search Edit+Run, `wrangler secret put` |
| `AutoRAGNotFoundError` | Instance name mismatch | `AI_SEARCH_INSTANCE` var must equal dashboard name |
| Model 404 on `ask` | Model retired | `npx wrangler ai models`, update `AI_SEARCH_MODEL` |
| Upload `WinError 193` | POSIX `.bin` shim | Fixed in script (uses `node wrangler.js`); if regressed, see section 3 |
| Upload `UnicodeDecodeError` | cp1252 locale | Child output is inherited; run with `PYTHONUTF8=1` for Python output |
| Reindex request HTTP 524 | Cloudflare timed out before acknowledging the request | Check `jobs list` and job logs first; the job may already have started |

## 9. Related docs in the repos

- `Trackdub/tools/docs-rag/README.md` — short setup/usage
- `Trackdub/tools/mcp-trackdub-gpu-docs/README.md` — the local v0 MCP
- `Trackdub/docs/reference/tensorrt-rtx-ep-abi-plugin.md` — the pin this corpus is keyed to (EP ABI 0.3.0 / cu12)
- `Trackdub-gated/docs/audits/2026-09-19-four-repo-audit/` — corpus includes this audit set
