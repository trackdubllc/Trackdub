import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { verifyIndex } from "./verify_index.mjs";

const options = { account: "account", bucket: "corpus", instance: "docs" };
const key = "first-party/trackdub/docs/reference/tensorrt-rtx-ep-abi-plugin.md";
const sources = [
  { key, size: 100, etag: "abc", last_modified: "2026-09-21T18:11:35.919Z", custom_metadata: { is_first_party: "true" } },
  { key: "vendor/nvidia/guide.md", size: 200, etag: "def", last_modified: "2026-09-21T18:11:35.919Z", custom_metadata: {} },
];
const items = [
  { id: "first", key, status: "completed", next_action: null, checksum: "index-abc", namespace: "default", chunks_count: 4,
    file_size: 100, metadata: { filename: "tensorrt-rtx-ep-abi-plugin.md", folder: "first-party/trackdub/docs/reference/", timestamp: 1789971060000, is_first_party: true },
    source_id: "r2:corpus", last_seen_at: "2026-09-22 03:45:04", created_at: "2026-09-21 06:22:56", error: null },
  { id: "vendor", key: "vendor/nvidia/guide.md", status: "completed", next_action: null, checksum: "index-def", namespace: "default", chunks_count: 2,
    file_size: 200, metadata: { filename: "guide.md", folder: "vendor/nvidia/", timestamp: 1789971060000 },
    source_id: "r2:corpus", last_seen_at: "2026-09-22 03:45:04", created_at: "2026-09-21 06:22:56", error: null },
];
const job = { id: "job", source: "user", started_at: "2026-09-22 03:44:48", ended_at: "2026-09-22 04:01:09", last_seen_at: "2026-09-22 04:01:09", end_reason: null };
function envelope(result, result_info) { return { success: true, result, result_info, errors: [], messages: [] }; }
function fixture(overrides = {}) {
  const state = { sources: structuredClone(sources), items: structuredClone(items), job: { ...job }, ...overrides };
  const calls = [];
  const api = async (path, query = {}, body) => {
    calls.push({ path, query, body });
    if (path.endsWith("/jobs")) return envelope([state.job], { page: 1, count: 1, total_count: 1, per_page: 50 });
    if (path.endsWith("/objects")) return envelope(state.sources, { is_truncated: false });
    if (path.endsWith("/items")) return envelope(state.items, { page: 1, count: state.items.length, total_count: state.items.length, per_page: 50 });
    if (path.endsWith("/search")) return envelope({ chunks: [{ text: "RegisterExecutionProviderLibrary loads NvTensorRTRTXExecutionProvider.", score: 0.8, item: { key } }] });
    assert.fail(`Unexpected endpoint: ${path}`);
  };
  return { state, calls, api };
}

test("verifies matching source/index inventory, metadata, sizes and retrieved content", async () => {
  const { api, calls } = fixture();
  const result = await verifyIndex(api, options);
  assert.equal(result.ok, true);
  assert.equal(result.sourceObjects, 2);
  assert.equal(result.indexedItems, 2);
  assert.equal(result.firstPartyFlags, 1);
  assert.deepEqual(result.issues, []);
  const search = calls.find(call => call.path.endsWith("/search"));
  assert.equal(search.body.ai_search_options.cache.enabled, false);
  assert.equal(search.body.ai_search_options.retrieval.filters.folder, "first-party/trackdub/docs/reference/");
  assert.equal(calls.filter(call => call.path.endsWith("/objects")).length, 2);
  assert.equal(calls.filter(call => call.path.endsWith("/jobs")).length, 2);
});

for (const [name, mutate, issue] of [
  ["completed item missing first-party flag", s => { delete s.items[0].metadata.is_first_party; }, "first-party metadata"],
  ["string instead of indexed boolean", s => { s.items[0].metadata.is_first_party = "true"; }, "first-party metadata"],
  ["false vendor flag still boosts existence", s => { s.items[1].metadata.is_first_party = false; }, "vendor metadata"],
  ["missing indexed scope folder", s => { delete s.items[0].metadata.folder; }, "folder metadata"],
  ["incorrect indexed scope folder", s => { s.items[1].metadata.folder = "vendor/amd/"; }, "folder metadata"],
  ["indexed folder without trailing slash", s => { s.items[1].metadata.folder = "vendor/nvidia"; }, "folder metadata"],
  ["source missing metadata", s => { s.sources[0].custom_metadata = {}; }, "source metadata"],
  ["outdated capacity failure", s => { s.items[0].status = "outdated"; s.items[0].error = "workers_ai_out_of_capacity_error"; }, "outdated"],
  ["pending action despite completed status", s => { s.items[0].next_action = "INDEX"; }, "pending"],
  ["zero chunks despite completed status", s => { s.items[0].chunks_count = 0; }, "chunks"],
  ["indexed content size differs", s => { s.items[0].file_size = 99; }, "size"],
  ["same-sized document not seen since upload", s => { s.items[0].last_seen_at = "2026-09-21 18:10:00"; }, "last seen"],
  ["missing key hidden by equal aggregate counts", s => { s.items[0].key = "first-party/trackdub/wrong.md"; }, "missing indexed"],
  ["duplicate item IDs", s => { s.items[1].id = "first"; }, "duplicate"],
  ["duplicate indexed keys", s => { s.items[1].key = key; }, "duplicate"],
  ["empty source inventory", s => { s.sources = []; s.items = []; }, "empty source"],
]) {
  test(`rejects ${name}`, async () => {
    const { api, state } = fixture();
    mutate(state);
    const result = await verifyIndex(api, options);
    assert.equal(result.ok, false);
    assert.ok(result.issues.some(message => message.includes(issue)), JSON.stringify(result));
  });
}

test("refuses an active job without scanning a moving index or creating another job", async () => {
  const { api, state, calls } = fixture();
  state.job.ended_at = null;
  const result = await verifyIndex(api, options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /active/);
  assert.equal(calls.length, 1);
});

test("paginates source cursors and index pages without accepting duplicated rows", async () => {
  const { api } = fixture();
  const pages = [];
  const result = await verifyIndex(async (path, query, body) => {
    if (path.endsWith("/objects")) {
      return query.cursor ? envelope([sources[1]], { is_truncated: false }) : envelope([sources[0]], { is_truncated: true, cursor: "next" });
    }
    if (path.endsWith("/items")) {
      pages.push(query.page);
      return envelope([items[query.page - 1]], { page: query.page, per_page: 1, count: 1, total_count: 2 });
    }
    return api(path, query, body);
  }, options);
  assert.equal(result.ok, true);
  assert.deepEqual(pages, [1, 2]);
});

test("accepts R2 terminal pages that omit result_info", async () => {
  const { api } = fixture();
  const result = await verifyIndex(async (path, query, body) => path.endsWith("/objects")
    ? { success: true, errors: [], messages: [], result: sources }
    : api(path, query, body), options);
  assert.equal(result.ok, true, JSON.stringify(result));
  assert.equal(result.sourceObjects, 2);
});

test("follows R2 cursors until a terminal page without result_info", async () => {
  const { api } = fixture();
  const result = await verifyIndex(async (path, query, body) => {
    if (path.endsWith("/objects")) return query.cursor
      ? { success: true, errors: [], messages: [], result: [sources[1]] }
      : envelope([sources[0]], { is_truncated: true, cursor: "next", per_page: 1 });
    return api(path, query, body);
  }, options);
  assert.equal(result.ok, true, JSON.stringify(result));
  assert.equal(result.sourceObjects, 2);
});

test("rejects a truncated R2 page without a continuation cursor", async () => {
  const { api } = fixture();
  const result = await verifyIndex(async (path, query, body) => path.endsWith("/objects")
    ? envelope(sources, { is_truncated: true, per_page: 1000 }) : api(path, query, body), options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /cursor/);
});

test("rejects pagination totals changing mid-scan", async () => {
  const { api } = fixture();
  const result = await verifyIndex(async (path, query, body) => path.endsWith("/items")
    ? envelope([items[query.page - 1]], { page: query.page, per_page: 1, count: 1, total_count: query.page === 1 ? 2 : 3 })
    : api(path, query, body), options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /pagination/);
});

test("fails a repeated R2 cursor instead of looping indefinitely", async () => {
  const { api } = fixture();
  const result = await verifyIndex(async (path, query, body) => path.endsWith("/objects")
    ? envelope(sources, { is_truncated: true, cursor: "same" }) : api(path, query, body), options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /cursor/);
});

test("rejects source mutation during verification even when size stays unchanged", async () => {
  const { api } = fixture();
  let reads = 0;
  const result = await verifyIndex(async (path, query, body) => {
    const response = await api(path, query, body);
    if (path.endsWith("/objects") && ++reads === 2) return envelope([{ ...sources[0], etag: "changed" }, sources[1]], { is_truncated: false });
    return response;
  }, options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /source changed/);
});

test("rejects a job starting during verification", async () => {
  const { api } = fixture();
  let reads = 0;
  const result = await verifyIndex(async (path, query, body) => path.endsWith("/jobs") && ++reads === 2
    ? envelope([{ ...job, id: "new", ended_at: null }], { page: 1, count: 1, total_count: 1 })
    : api(path, query, body), options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /job changed/);
});

test("completed metadata alone does not pass when retrieval cannot find canary content", async () => {
  const { api } = fixture();
  const result = await verifyIndex(async (path, query, body) => path.endsWith("/search")
    ? envelope({ chunks: [{ text: "Unrelated content", item: { key } }] }) : api(path, query, body), options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /canary/);
});

test("upstream errors never become a successful empty inventory", async () => {
  const result = await verifyIndex(async () => { throw new Error("HTTP 503"); }, options);
  assert.equal(result.ok, false);
  assert.match(result.issues.join(), /HTTP 503/);
});

test("CLI uses the versioned Cloudflare URL and fails active jobs without writing", () => {
  const preload = `globalThis.fetch = async (url, init) => {
    if (init.method !== 'GET' || init.headers.Authorization !== 'Bearer test-only') throw new Error('Unexpected request');
    if (url.pathname !== '/client/v4/accounts/account/ai-search/namespaces/default/instances/trackdub-docs/jobs') return new Response('', {status:404});
    return Response.json({success:true,result:[{id:'running',source:'user',ended_at:null}],errors:[]});
  };`;
  let output;
  try {
    execFileSync(process.execPath, ["--import", `data:text/javascript,${encodeURIComponent(preload)}`,
      fileURLToPath(new URL("./verify_index.mjs", import.meta.url)), "--account", "account"],
    { encoding: "utf8", env: { ...process.env, CLOUDFLARE_API_TOKEN: "test-only" }, stdio: ["ignore", "pipe", "pipe"] });
    assert.fail("active job must exit nonzero");
  } catch (error) {
    assert.equal(error.status, 1);
    output = JSON.parse(error.stdout);
  }
  assert.equal(output.ok, false);
  assert.match(output.issues.join(), /active/);
});
