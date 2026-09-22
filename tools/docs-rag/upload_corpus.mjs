import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const KEY_LIST_PAGE = 1000;

const contentTypeFor = (key) =>
  key.endsWith(".json") ? "application/json" : "text/markdown; charset=utf-8";
const customMetadataFor = (key) =>
  key.startsWith("first-party/") ? { is_first_party: "true" } : {};

function metadataMatches(head, key) {
  if (!head || head.httpMetadata?.contentType !== contentTypeFor(key)) return false;
  const want = customMetadataFor(key);
  const have = head.customMetadata ?? {};
  const wantKeys = Object.keys(want);
  return wantKeys.length === Object.keys(have).length
    && wantKeys.every((name) => have[name] === want[name]);
}

function contentMatches(object, bytes) {
  return object?.size === bytes.byteLength
    && String(object.etag).replace(/"/g, "").toUpperCase()
      === createHash("md5").update(bytes).digest("hex").toUpperCase();
}

async function listRemote(bucket) {
  const objects = new Map();
  let cursor;
  do {
    const page = await bucket.list({ limit: KEY_LIST_PAGE, ...(cursor ? { cursor } : {}) });
    for (const object of page.objects) objects.set(object.key, object);
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return objects;
}

export async function uploadFiles(bucket, staging, workers, prune = false) {
  const entries = await readdir(staging, { recursive: true, withFileTypes: true });
  const files = entries.filter((entry) => entry.isFile())
    .map((entry) => path.join(entry.parentPath, entry.name)).sort();
  if (!files.length) {
    console.error("No staged files to upload");
    return 1;
  }
  const toKey = (file) => path.relative(staging, file).split(path.sep).join("/");
  // A re-put refreshes last_modified, so uploading unchanged documents makes AI
  // Search re-embed the whole corpus at once and overrun Workers AI capacity.
  const remote = await listRemote(bucket);
  let next = 0;
  let done = 0;
  let putCount = 0;
  let skipped = 0;
  let failed = 0;
  await Promise.all(Array.from({ length: Math.min(Math.max(1, workers), files.length) }, async () => {
    while (next < files.length) {
      const file = files[next++];
      const key = toKey(file);
      try {
        const bytes = new Uint8Array(await readFile(file));
        if (contentMatches(remote.get(key), bytes) && metadataMatches(await bucket.head(key), key)) {
          skipped++;
          console.log(`skip (${++done}/${files.length}) ${key}`);
          continue;
        }
        await bucket.put(key, bytes, {
          httpMetadata: { contentType: contentTypeFor(key) },
          customMetadata: customMetadataFor(key),
        });
        putCount++;
        console.log(`put (${++done}/${files.length}) ${key}`);
      } catch (error) {
        failed++;
        console.error(`FAIL (${++done}/${files.length}) ${key}: ${error.message}`);
      }
    }
  }));
  console.log(`upload done: put=${putCount} skipped=${skipped} failed=${failed} of ${files.length}`);
  if (failed) return 1;
  // Deletion after a complete upload keeps a failed sync from shrinking the corpus.
  if (prune) await pruneStale(bucket, new Set(files.map(toKey)), remote);
  return 0;
}

async function pruneStale(bucket, stagedKeys, remote) {
  const stale = [...remote.keys()].filter((key) => !stagedKeys.has(key)).sort();
  for (const key of stale) {
    await bucket.delete(key);
    console.log(`deleted ${key}`);
  }
  console.log(`prune done: ${stale.length}`);
}

async function main() {
  const [apiRoot, staging, bucket, workers, mode] = process.argv.slice(2);
  const require = createRequire(path.join(apiRoot, "package.json"));
  const { getPlatformProxy } = require("wrangler");
  const temporary = await mkdtemp(path.join(os.tmpdir(), "trackdub-docs-ingest-"));
  let proxy;
  try {
    const configPath = path.join(temporary, "wrangler.json");
    await writeFile(configPath, JSON.stringify({
      name: "trackdub-docs-ingest",
      compatibility_date: "2026-09-01",
      r2_buckets: [{ binding: "CORPUS", bucket_name: bucket, remote: true }],
    }));
    proxy = await getPlatformProxy({ configPath, persist: false, remoteBindings: true });
    if (mode && mode !== "--prune") throw new Error(`Unknown mode "${mode}"; expected --prune`);
    process.exitCode = await uploadFiles(proxy.env.CORPUS, staging, Number(workers), mode === "--prune");
  }
  finally {
    try {
      await proxy?.dispose();
    } finally {
      await rm(temporary, { recursive: true, force: true });
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
