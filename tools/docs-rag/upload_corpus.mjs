import { createReadStream } from "node:fs";
import { mkdtemp, readdir, rm, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import { Readable } from "node:stream";
import { fileURLToPath } from "node:url";

export async function uploadFiles(bucket, staging, workers) {
  const entries = await readdir(staging, { recursive: true, withFileTypes: true });
  const files = entries.filter((entry) => entry.isFile())
    .map((entry) => path.join(entry.parentPath, entry.name)).sort();
  if (!files.length) {
    console.error("No staged files to upload");
    return 1;
  }
  let next = 0;
  let done = 0;
  let failed = 0;
  await Promise.all(Array.from({ length: Math.min(Math.max(1, workers), files.length) }, async () => {
    while (next < files.length) {
      const file = files[next++];
      const key = path.relative(staging, file).split(path.sep).join("/");
      try {
        await bucket.put(key, Readable.toWeb(createReadStream(file)), {
          httpMetadata: { contentType: key.endsWith(".json") ? "application/json" : "text/markdown; charset=utf-8" },
          customMetadata: key.startsWith("first-party/") ? { is_first_party: "true" } : {},
        });
        console.log(`put (${++done}/${files.length}) ${key}`);
      } catch (error) {
        failed++;
        console.error(`FAIL (${++done}/${files.length}) ${key}: ${error.message}`);
      }
    }
  }));
  console.log(`upload done: ${files.length - failed}/${files.length}`);
  return failed ? 1 : 0;
}

async function main() {
  const [apiRoot, staging, bucket, workers] = process.argv.slice(2);
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
    process.exitCode = await uploadFiles(proxy.env.CORPUS, staging, Number(workers));
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
