# Docs corpus reconciliation — reusable agent prompt

Run context:
- Run from the Trackdub repo root (paths below are repo-relative).
- Prefer the `trackdub-docs-rag` MCP (`search_trackdub_docs`, `ask_trackdub_docs`, `get_trackdub_doc`). If it is not connected in-session, the same Worker is reachable over direct HTTPS (see "HTTP fallback" below) — do NOT substitute a smaller local corpus without saying so in the report.
- Read-only except for one report file. No doc edits, no corpus changes, no network fetches of vendor URLs.
- Best run after `verify_index.mjs` passes, so the retrieval smoke test (step 5) queries a fully indexed corpus.

R2 key rules (from `tools/docs-rag/sync_corpus.py`):
- Vendor keys: `<prefix>/<source-id>.md` — **the `.md` suffix is required** (e.g. `vendor/nvidia/trt-rtx-best-practices.md`, `vendor/olive/olive-landing.md`, `vendor/amd/migraphx-install.md`).
- First-party keys: `<prefix>/<repo-relative-path>` — real repo paths, extension as on disk (e.g. `first-party/trackdub/docs/reference/tensorrt-rtx-ep-abi-plugin.md`).
- `first-party/trackdub/docs/reference/docs-rag-pin.md` exists only in the corpus: `sync_corpus.py` synthesizes it at sync time. It is not repo drift.

HTTP fallback (when the MCP is not connected):
- Doc fetch: `POST https://trackdub-docs-rag.trackdub.workers.dev/mcp` with the `Authorization` header from `.mcp.json`, `Content-Type: application/json`, `Accept: application/json, text/event-stream`, body `{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_trackdub_doc","arguments":{"key":"<r2-key>","max_chars":200000}}}`. Stateless — no initialize needed. Parse the JSON after the `data: ` line; `result.content[0].text` is the doc payload (`found`/`size`/`truncated`/`text`). `max_chars` minimum 500.
- Retrieval: `POST /v1/search` with `{"query","scope","limit"}` — plain JSON with `hits[].key/text/score`.
- Rate limits: 30 req/10s, 300/min, `ask` 5/10s. Write the report incrementally so an interruption cannot lose completed work.

---

Task: Reconcile Trackdub first-party docs against the pinned vendor docs in the docs-rag corpus, and produce a triaged delta report. Do not modify any docs.

Ground rules
- Canon for upstream facts is the PINNED vendor docs as stored in R2 — not live vendor websites. Never fetch a vendor URL from the network; the corpus is pin-accurate by design.
- Canon for Trackdub decisions is runtime/trt-rtx-ep.manifest.json + docs/decisions/* + AGENTS.md; per AGENTS.md conflict order, source code outranks docs for Trackdub behavior. A deviation from vendor docs is only a defect if it contradicts those.
- Read full documents with get_trackdub_doc — it serves current bytes straight from R2. Never compare documents via search chunks.
- Use search / ask only for the verification pass at the end.

Step 1 — Build the pairing inventory
Read tools/docs-rag/corpus.v1.json and apply the key rules above. Seed mapping (extend if you find other overlapping first-party docs):
- docs/reference/tensorrt-rtx-ep-abi-plugin.md  <-> vendor/nvidia/ep-abi-v042.md, ep-abi-readme.md, trt-rtx-arch.md/how.md/porting.md/c-api.md/advanced.md/support-matrix.md/best-practices.md
- docs/reference/gpu-execution-providers.md     <-> vendor/onnxruntime/ep-overview.md, trt-rtx-ep.md, plugin-ep.md, cuda-ep.md, directml-ep.md, migraphx-ep.md, openvino-ep.md, qnn-ep.md
- docs/reference/windows-ml-*.md                <-> vendor/microsoft/winml-*.md
- docs/reference/migraphx-phase0-seams.md       <-> vendor/amd/migraphx-*.md
- docs/reference/olive-recipe-pilot.md           <-> vendor/olive/*.md
Read each first-party doc from the local repo (ground truth on disk), and the pin manifest.

Step 2 — Extract checkable claims
From each first-party doc, list every checkable factual claim: versions, API/type names and signatures, defaults, env vars, flags, hardware/OS requirements, support statements, version-pinned behavior.

Step 3 — Compare
For each pair, get the vendor doc via get_trackdub_doc and check every claim against it. If a get fails or truncates, mark the pair "blocked" and move on.

Step 4 — Classify every mismatch
- vendor-correct: first-party doc drifted from the pinned vendor doc -> propose exact replacement text.
- trackdub-deliberate: deviation matches the pin manifest or an ADR -> keep, and propose one added sentence stating "vendor default is X; Trackdub pins Y because <ADR/pin ref>" so either source alone carries the truth.
- unverifiable: vendor doc is silent -> propose hedging or removal.
- needs-human: cannot classify (including cases where Trackdub source code itself differs from the pinned vendor doc — flag the code location; do not assume the code is wrong).
Also record claims where first-party and vendor agree but retrieval might confuse (see step 5).

Step 5 — Verify against retrieval
For each vendor-correct and trackdub-deliberate delta, run one search call (scope "all", limit 8, query = the claim's key terms). Record whether the top-ranked chunks would lead an agent to the reconciled answer, and flag any pair where first-party and vendor chunks contradict each other in results. If the index has known-outdated items, flag suspected stale-chunk results as such rather than treating them as contradictions.

Step 6 — Report
Write tools/docs-rag/RECON-<today>.md with a table per pair: claim, first-party quote (file + line), vendor quote (R2 key), classification, proposed edit as a diff. End with: counts per classification, list of blocked pairs, and the top 5 highest-risk deltas. This report is the only file you create. Update it incrementally as pairs complete.
