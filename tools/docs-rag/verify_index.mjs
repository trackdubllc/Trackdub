import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";

const toolRoot = path.dirname(fileURLToPath(import.meta.url));
const canaryKey = "first-party/trackdub/docs/reference/tensorrt-rtx-ep-abi-plugin.md";

function rows(response) {
  if (response?.success !== true || !Array.isArray(response.result) || response.errors?.length) {
    throw new Error("Invalid list response from Cloudflare");
  }
  return response.result;
}

async function sourceObjects(api, endpoint) {
  const objects = [];
  const cursors = new Set();
  let cursor;
  for (let page = 0; page < 100; page++) {
    const response = await api(endpoint, { per_page: 1000, include: "customMetadata,httpMetadata", ...(cursor ? { cursor } : {}) });
    objects.push(...rows(response));
    // R2 omits result_info entirely on terminal pages.
    if (response.result_info === undefined || response.result_info?.is_truncated === false) return objects;
    cursor = response.result_info?.cursor;
    if (!cursor || cursors.has(cursor)) throw new Error("Invalid or repeated R2 cursor");
    cursors.add(cursor);
  }
  throw new Error("R2 pagination limit exceeded");
}

async function indexItems(api, endpoint, bucket) {
  const items = [];
  let total;
  for (let page = 1; page <= 100; page++) {
    const response = await api(endpoint, { page, per_page: 50, sort_by: "modified_at", source: `r2:${bucket}` });
    const batch = rows(response);
    const info = response.result_info;
    if (!Number.isInteger(info?.total_count) || info.total_count < 0 || info.page !== page ||
        info.count !== batch.length || (total !== undefined && total !== info.total_count)) {
      throw new Error("Index pagination changed or is invalid; retry after indexing settles");
    }
    total = info.total_count;
    items.push(...batch);
    if (items.length === total) return items;
    if (!batch.length || items.length > total) throw new Error("Incomplete index pagination");
  }
  throw new Error("Index pagination limit exceeded");
}

function sourceSignature(objects) {
  return JSON.stringify(objects.map(o => [o.key, o.etag, o.size, o.last_modified, o.custom_metadata])
    .sort((a, b) => a[0].localeCompare(b[0])));
}

export async function verifyIndex(api, { account, bucket, instance }) {
  const result = { ok: false, sourceObjects: 0, indexedItems: 0, firstPartyFlags: 0, issues: [] };
  const issues = result.issues;
  const base = `/accounts/${encodeURIComponent(account)}/ai-search/namespaces/default/instances/${encodeURIComponent(instance)}`;
  const sourcePath = `/accounts/${encodeURIComponent(account)}/r2/buckets/${encodeURIComponent(bucket)}/objects`;
  try {
    const before = rows(await api(`${base}/jobs`, { per_page: 50 }));
    if (before.some(job => !job.ended_at)) {
      issues.push("Indexing job active; verification deferred, no new job requested");
      return result;
    }
    const objects = await sourceObjects(api, sourcePath);
    const items = await indexItems(api, `${base}/items`, bucket);
    result.sourceObjects = objects.length;
    result.indexedItems = items.length;
    if (!objects.length) issues.push("empty source inventory");
    const sourceKeys = new Set();
    const indexed = new Map();
    const ids = new Set();
    for (const item of items) {
      if (typeof item.id !== "string" || typeof item.key !== "string") throw new Error("Invalid indexed item identity");
      if (ids.has(item.id) || indexed.has(item.key)) issues.push(`duplicate indexed item: ${item.key}`);
      ids.add(item.id);
      indexed.set(item.key, item);
      if (item.source_id !== `r2:${bucket}`) issues.push(`wrong source: ${item.key}`);
      if (item.status !== "completed" || item.error) issues.push(`${item.key}: ${item.status} ${item.error ?? ""}`.trim());
      if (item.next_action) issues.push(`pending ${item.next_action}: ${item.key}`);
      if (!Number.isInteger(item.chunks_count) || item.chunks_count < 1) issues.push(`no indexed chunks: ${item.key}`);
      if (item.metadata?.folder !== item.key.slice(0, item.key.lastIndexOf("/") + 1)) {
        issues.push(`folder metadata missing or incorrect: ${item.key}`);
      }
      if (item.key.startsWith("first-party/")) {
        if (item.metadata?.is_first_party === true) result.firstPartyFlags++;
        else issues.push(`first-party metadata missing or invalid: ${item.key}`);
      } else if (Object.hasOwn(item.metadata ?? {}, "is_first_party")) {
        issues.push(`vendor metadata must omit is_first_party: ${item.key}`);
      }
    }
    for (const object of objects) {
      if (typeof object.key !== "string") throw new Error("Invalid source object key");
      if (sourceKeys.has(object.key)) issues.push(`duplicate source key: ${object.key}`);
      sourceKeys.add(object.key);
      if (object.key.startsWith("first-party/")
        ? object.custom_metadata?.is_first_party !== "true"
        : Object.hasOwn(object.custom_metadata ?? {}, "is_first_party")) issues.push(`source metadata incorrect: ${object.key}`);
      const item = indexed.get(object.key);
      if (!item) { issues.push(`missing indexed key: ${object.key}`); continue; }
      if (!Number.isInteger(object.size) || object.size !== item.file_size) issues.push(`indexed size mismatch: ${object.key}`);
      const lastSeen = String(item.last_seen_at);
      const seen = Date.parse(lastSeen.includes("T") ? lastSeen : lastSeen.replace(" ", "T") + "Z");
      const modified = Date.parse(object.last_modified);
      if (!Number.isFinite(seen) || !Number.isFinite(modified) || seen < Math.floor(modified / 1000) * 1000) {
        issues.push(`indexed last seen predates source upload: ${object.key}`);
      }
    }
    for (const item of items) if (!sourceKeys.has(item.key)) issues.push(`extra indexed key: ${item.key}`);
    if (!issues.length) {
      const search = await api(`/accounts/${encodeURIComponent(account)}/ai-search/instances/${encodeURIComponent(instance)}/search`, {}, {
        query: "NvTensorRTRTXExecutionProvider RegisterExecutionProviderLibrary",
        ai_search_options: { cache: { enabled: false }, retrieval: {
          max_num_results: 3, keyword_match_mode: "and", filters: { folder: "first-party/trackdub/docs/reference/" },
        } },
      });
      if (search?.success !== true || search.errors?.length || !Array.isArray(search.result?.chunks) ||
          !search.result.chunks.some(chunk => chunk.item?.key === canaryKey && typeof chunk.text === "string" &&
            chunk.text.includes("NvTensorRTRTXExecutionProvider") && chunk.text.includes("RegisterExecutionProviderLibrary"))) {
        issues.push("retrieval canary missing expected key/content");
      }
    }
    if (sourceSignature(objects) !== sourceSignature(await sourceObjects(api, sourcePath))) issues.push("source changed during verification");
    const after = rows(await api(`${base}/jobs`, { per_page: 50 }));
    if (JSON.stringify(before) !== JSON.stringify(after)) issues.push("indexing job changed during verification");
    result.ok = issues.length === 0;
  } catch (error) {
    issues.push(error.message);
  }
  return result;
}

async function main() {
  const { values } = parseArgs({ options: { account: { type: "string" }, "api-root": { type: "string" } } });
  const account = values.account ?? process.env.CLOUDFLARE_ACCOUNT_ID;
  if (!account) throw new Error("Provide --account or CLOUDFLARE_ACCOUNT_ID");
  const apiRoot = path.resolve(values["api-root"] ?? process.env.API_TRACKDUB_ROOT ?? path.join(toolRoot, "../../../api.trackdub"));
  const manifest = JSON.parse(await readFile(path.join(toolRoot, "corpus.v1.json"), "utf8"));
  const token = process.env.CLOUDFLARE_API_TOKEN || JSON.parse(execFileSync(process.execPath,
    [path.join(apiRoot, "node_modules/wrangler/bin/wrangler.js"), "auth", "token", "--json"],
    { cwd: apiRoot, encoding: "utf8", timeout: 20000, stdio: ["ignore", "pipe", "pipe"] })).token;
  if (typeof token !== "string" || !token) throw new Error("Wrangler authentication token unavailable");
  const signal = AbortSignal.timeout(90000);
  const api = async (endpoint, query = {}, body) => {
    const url = new URL(`/client/v4${endpoint}`, "https://api.cloudflare.com");
    url.search = new URLSearchParams(query).toString();
    const response = await fetch(url, { method: body ? "POST" : "GET", signal,
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      ...(body ? { body: JSON.stringify(body) } : {}),
    });
    if (!response.ok) throw new Error(`Cloudflare HTTP ${response.status} at ${endpoint}`);
    return response.json();
  };
  const report = await verifyIndex(api, { account, bucket: manifest.bucket, instance: manifest.aiSearchInstance });
  console.log(JSON.stringify(report, null, 2));
  process.exitCode = report.ok ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(() => { console.error("Index verification failed; check authentication, arguments and connectivity. No indexing job requested."); process.exitCode = 1; });
}
