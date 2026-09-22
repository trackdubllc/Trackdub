import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

const { uploadFiles } = await import("./upload_corpus.mjs");

const md5 = (bytes) => createHash("md5").update(bytes).digest("hex").toUpperCase();
const emptyList = async () => ({ objects: [], delimitedPrefixes: [], truncated: false });

async function staged(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), "docs-rag-test-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const key of ["first-party/trackdub/doc.md", "first-party/trackdub/manifest.json", "vendor/nvidia/doc.md"]) {
    await mkdir(path.dirname(path.join(root, key)), { recursive: true });
    await writeFile(path.join(root, key), `# ${key}\n`);
  }
  return root;
}

test("uploads source bytes and marks only first-party metadata", async (t) => {
  const root = await staged(t);
  const stored = new Map();
  const bucket = {
    put: async (key, value, options) => stored.set(key, { bytes: await new Response(value).text(), options }),
    list: emptyList,
    head: async () => null,
  };
  assert.equal(await uploadFiles(bucket, root, 2), 0);
  assert.equal(stored.size, 3);
  const first = stored.get("first-party/trackdub/doc.md");
  assert.equal(first.bytes, await readFile(path.join(root, "first-party/trackdub/doc.md"), "utf8"));
  assert.deepEqual(first.options.customMetadata, { is_first_party: "true" });
  assert.equal(first.options.httpMetadata.contentType, "text/markdown; charset=utf-8");
  const manifest = stored.get("first-party/trackdub/manifest.json");
  assert.equal(manifest.bytes, await readFile(path.join(root, "first-party/trackdub/manifest.json"), "utf8"));
  assert.deepEqual(manifest.options.customMetadata, { is_first_party: "true" });
  assert.equal(manifest.options.httpMetadata.contentType, "application/json");
  assert.deepEqual(stored.get("vendor/nvidia/doc.md").options.customMetadata, {});
});

// workerd refuses a body whose length it cannot determine before the write starts.
function lengthCheckingBucket() {
  const knownLength = (value) => {
    if (typeof value === "string") return Buffer.byteLength(value);
    if (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) return value.byteLength;
  };
  const stored = new Map();
  return {
    stored,
    list: emptyList,
    head: async () => null,
    put: async (key, value) => {
      const length = knownLength(value);
      if (length === undefined) {
        throw new TypeError("Provided readable stream must have a known length (request/response body or readable half of FixedLengthStream)");
      }
      const bytes = value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
      stored.set(key, bytes);
    },
    list: emptyList,
    head: async () => null,
  };
}

test("hands the bucket a body with a known length", async (t) => {
  const root = await staged(t);
  const bucket = lengthCheckingBucket();
  assert.equal(await uploadFiles(bucket, root, 2), 0);
  assert.equal(bucket.stored.size, 3);
  assert.deepEqual(bucket.stored.get("first-party/trackdub/doc.md"),
    new Uint8Array(await readFile(path.join(root, "first-party/trackdub/doc.md"))));
});

test("reports partial upload failure instead of success", async (t) => {
  const root = await staged(t);
  const uploaded = [];
  const bucket = { list: emptyList, head: async () => null, put: async (key) => {
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

function bucketWith(existing, pageSize = 2) {
  const stored = new Set(existing);
  const deleted = [];
  return {
    stored,
    deleted,
    put: async (key) => { stored.add(key); },
    list: async ({ cursor } = {}) => {
      const all = [...stored].sort();
      const rest = cursor ? all.slice(all.indexOf(cursor) + 1) : all;
      const page = rest.slice(0, pageSize);
      const truncated = rest.length > pageSize;
      return {
        objects: page.map((key) => ({ key })),
        delimitedPrefixes: [],
        truncated,
        ...(truncated ? { cursor: page[page.length - 1] } : {}),
      };
    },
    delete: async (key) => { stored.delete(key); deleted.push(key); },
  };
}

test("prunes objects missing from staging across list pages", async (t) => {
  const root = await staged(t);
  const bucket = bucketWith([
    "first-party/trackdub/doc.md",
    "first-party/trackdub/manifest.json",
    "vendor/nvidia/doc.md",
    "first-party/trackdub/docs/specs/specs.md",
    "vendor/nvidia/old.md",
  ]);
  assert.equal(await uploadFiles(bucket, root, 2, true), 0);
  assert.deepEqual(bucket.deleted, ["first-party/trackdub/docs/specs/specs.md", "vendor/nvidia/old.md"]);
  assert.equal(bucket.stored.size, 3);
});

test("keeps stale objects unless pruning is requested", async (t) => {
  const root = await staged(t);
  const bucket = bucketWith(["first-party/trackdub/doc.md", "first-party/trackdub/manifest.json", "vendor/nvidia/doc.md", "vendor/nvidia/old.md"]);
  assert.equal(await uploadFiles(bucket, root, 2), 0);
  assert.equal(bucket.stored.size, 4);
  assert.deepEqual(bucket.deleted, []);
});

test("does not prune after a failed upload", async (t) => {
  const root = await staged(t);
  const bucket = bucketWith(["first-party/trackdub/doc.md", "first-party/trackdub/manifest.json", "vendor/nvidia/doc.md", "vendor/nvidia/old.md"]);
  bucket.put = async (key) => {
    if (key.endsWith("manifest.json")) throw new Error("upload rejected");
  };
  assert.equal(await uploadFiles(bucket, root, 1, true), 1);
  assert.deepEqual(bucket.deleted, []);
  assert.equal(bucket.stored.size, 4);
});

function corpusEntry(key, bytes, customMetadata) {
  return {
    bytes,
    contentType: key.endsWith(".json") ? "application/json" : "text/markdown; charset=utf-8",
    customMetadata,
  };
}

async function syncedBucket(t, mutate = () => {}) {
  const root = await staged(t);
  const remote = new Map();
  for (const key of ["first-party/trackdub/doc.md", "first-party/trackdub/manifest.json", "vendor/nvidia/doc.md"]) {
    remote.set(key, corpusEntry(key, new Uint8Array(await readFile(path.join(root, key))),
      key.startsWith("first-party/") ? { is_first_party: "true" } : {}));
  }
  mutate(remote);
  const puts = [];
  return {
    root,
    puts,
    list: async () => ({
      objects: [...remote].map(([key, object]) => ({ key, size: object.bytes.byteLength, etag: object.etag ?? md5(object.bytes) })),
      delimitedPrefixes: [],
      truncated: false,
    }),
    head: async (key) => (remote.has(key) ? {
      httpMetadata: { contentType: remote.get(key).contentType },
      customMetadata: remote.get(key).customMetadata,
    } : null),
    put: async (key, value, options) => {
      puts.push(key);
      remote.set(key, corpusEntry(key, new Uint8Array(await new Response(value).arrayBuffer()), options.customMetadata));
    },
    delete: async (key) => { remote.delete(key); },
  };
}

test("skips bucket objects whose bytes and metadata already match", async (t) => {
  const bucket = await syncedBucket(t);
  assert.equal(await uploadFiles(bucket, bucket.root, 2), 0);
  assert.deepEqual(bucket.puts, []);
});

test("re-uploads only the object whose bytes changed", async (t) => {
  const bucket = await syncedBucket(t, (remote) => {
    remote.set("vendor/nvidia/doc.md", corpusEntry("vendor/nvidia/doc.md", new TextEncoder().encode("stale body\n"), {}));
  });
  assert.equal(await uploadFiles(bucket, bucket.root, 2), 0);
  assert.deepEqual(bucket.puts, ["vendor/nvidia/doc.md"]);
});

test("re-uploads when stored first-party metadata is missing", async (t) => {
  const bucket = await syncedBucket(t, (remote) => {
    remote.get("first-party/trackdub/doc.md").customMetadata = {};
  });
  assert.equal(await uploadFiles(bucket, bucket.root, 2), 0);
  assert.deepEqual(bucket.puts, ["first-party/trackdub/doc.md"]);
});

test("matches an etag that arrives quoted and lowercase", async (t) => {
  const bucket = await syncedBucket(t, (remote) => {
    const entry = remote.get("first-party/trackdub/manifest.json");
    entry.etag = `"${md5(entry.bytes).toLowerCase()}"`;
  });
  assert.equal(await uploadFiles(bucket, bucket.root, 2), 0);
  assert.deepEqual(bucket.puts, []);
});
