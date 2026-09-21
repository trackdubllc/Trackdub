import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

const { uploadFiles } = await import("./upload_corpus.mjs");

async function staged(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), "docs-rag-test-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const key of ["first-party/trackdub/doc.md", "vendor/nvidia/doc.md"]) {
    await mkdir(path.dirname(path.join(root, key)), { recursive: true });
    await writeFile(path.join(root, key), `# ${key}\n`);
  }
  return root;
}

test("uploads source bytes and marks only first-party metadata", async (t) => {
  const root = await staged(t);
  const stored = new Map();
  const bucket = { put: async (key, bytes, options) => stored.set(key, { bytes, options }) };
  assert.equal(await uploadFiles(bucket, root, 2), 0);
  assert.equal(stored.size, 2);
  const first = stored.get("first-party/trackdub/doc.md");
  assert.deepEqual(first.bytes, await readFile(path.join(root, "first-party/trackdub/doc.md")));
  assert.deepEqual(first.options.customMetadata, { is_first_party: "true" });
  assert.equal(first.options.httpMetadata.contentType, "text/markdown; charset=utf-8");
  assert.deepEqual(stored.get("vendor/nvidia/doc.md").options.customMetadata, {});
});

test("reports partial upload failure instead of success", async (t) => {
  const root = await staged(t);
  const uploaded = [];
  const bucket = { put: async (key) => {
    if (key.startsWith("first-party/")) throw new Error("upload rejected");
    uploaded.push(key);
  } };
  assert.equal(await uploadFiles(bucket, root, 1), 1);
  assert.deepEqual(uploaded, ["vendor/nvidia/doc.md"]);
});

test("rejects an empty staging tree", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "docs-rag-empty-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  assert.equal(await uploadFiles({ put: () => assert.fail("must not upload") }, root, 1), 1);
});
