"""Stage first-party and vendor docs, then upload the tree to R2 for AI Search."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import tempfile
import urllib.error
import urllib.request
from html.parser import HTMLParser
from pathlib import Path, PurePosixPath


TOOL_ROOT = Path(__file__).resolve().parent
TRACKDUB_ROOT = TOOL_ROOT.parents[1]
STAGING = TOOL_ROOT / ".staging"
MANIFEST_PATH = TOOL_ROOT / "corpus.v1.json"
USER_AGENT = "TrackdubDocsRag/0.1 (corpus sync)"


class _TextExtractor(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self._skip = 0
        self._parts: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag in {"script", "style", "noscript"}:
            self._skip += 1

    def handle_endtag(self, tag: str) -> None:
        if tag in {"script", "style", "noscript"} and self._skip:
            self._skip -= 1
        if tag in {"p", "div", "h1", "h2", "h3", "li", "br", "tr"}:
            self._parts.append("\n")

    def handle_data(self, data: str) -> None:
        if self._skip:
            return
        text = data.strip()
        if text:
            self._parts.append(text + " ")

    def text(self) -> str:
        raw = "".join(self._parts)
        lines = [" ".join(line.split()) for line in raw.splitlines()]
        return "\n".join(line for line in lines if line).strip()


def load_manifest() -> dict:
    return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))


def repo_root(entry: dict) -> Path | None:
    override = os.environ.get(entry["rootEnv"])
    if override:
        path = Path(override)
    else:
        path = (TRACKDUB_ROOT / entry["defaultFromTrackdub"]).resolve()
    return path if path.is_dir() else None


def stage_repos(manifest: dict, staging: Path) -> tuple[int, int]:
    copied = 0
    skipped = 0
    max_bytes = int(manifest["maxBytes"])
    for repo in manifest["repos"]:
        root = repo_root(repo)
        if root is None:
            raise RuntimeError(f"repo {repo['id']}: root missing")
        prefix = repo["prefix"]
        exclude = repo.get("exclude", [])
        matched_excludes: set[str] = set()
        repo_copied = copied
        seen: set[Path] = set()
        for pattern in repo["include"]:
            for path in root.glob(pattern):
                if not path.is_file() or path in seen:
                    continue
                if any(part in {".git", "node_modules", ".venv"} for part in path.parts):
                    continue
                seen.add(path)
                rel = path.relative_to(root).as_posix()
                excluded = [glob for glob in exclude if PurePosixPath(rel).full_match(glob)]
                if excluded:
                    matched_excludes.update(excluded)
                    continue
                if path.stat().st_size > max_bytes:
                    print(f"skip large {path}")
                    skipped += 1
                    continue
                dest = staging / prefix / rel
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(path.read_bytes())
                copied += 1
        if copied == repo_copied:
            raise RuntimeError(f"repo {repo['id']}: no eligible documents")
        unmatched = sorted(set(exclude) - matched_excludes)
        if unmatched:
            raise RuntimeError(f"repo {repo['id']}: exclude patterns matched no document: {unmatched}")
    return copied, skipped


def fetch_url(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "text/html,text/plain"})
    with urllib.request.urlopen(request, timeout=45) as response:
        body = response.read()
        content_type = response.headers.get("Content-Type", "")
    text = body.decode("utf-8", errors="replace")
    if "html" in content_type or text.lstrip().lower().startswith("<!doctype html") or text.lstrip().lower().startswith("<html"):
        parser = _TextExtractor()
        parser.feed(text)
        text = parser.text()
    return text.strip()


def stage_vendors(manifest: dict, staging: Path) -> tuple[int, int]:
    ok = 0
    fail = 0
    max_bytes = int(manifest["maxBytes"])
    for vendor in manifest["vendors"]:
        for source_id, url in vendor["sources"]:
            dest = staging / vendor["prefix"] / f"{source_id}.md"
            try:
                text = fetch_url(url)
            except (urllib.error.URLError, TimeoutError, OSError) as exc:
                print(f"FAIL {vendor['id']}/{source_id}: {exc}")
                fail += 1
                continue
            if not text:
                print(f"FAIL {vendor['id']}/{source_id}: empty")
                fail += 1
                continue
            payload = f"Source: {url}\n\n{text}\n"
            if len(payload.encode("utf-8")) > max_bytes:
                payload = payload.encode("utf-8")[:max_bytes].decode("utf-8", errors="ignore")
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text(payload, encoding="utf-8")
            print(f"ok {vendor['id']}/{source_id}")
            ok += 1
    return ok, fail


def stage_pin(staging: Path) -> None:
    pin = staging / "first-party" / "trackdub" / "docs" / "reference" / "docs-rag-pin.md"
    pin.parent.mkdir(parents=True, exist_ok=True)
    if pin.exists():
        return
    pin.write_text(
        "\n".join(
            [
                "# Docs RAG pin",
                "",
                "Trackdub ships TensorRT-RTX as the standalone ONNX Runtime EP ABI plugin, version 0.3.0, CUDA cu12.",
                "Windows bundle includes tensorrt_rtx_1_5.dll. Do not treat Windows ML catalog NvTensorRtRtxExecutionProvider as the primary route.",
                "Vendor /latest/ pages may describe a newer TensorRT-RTX than this pin. First-party docs win on disagreement.",
                "",
            ]
        ),
        encoding="utf-8",
    )


def wrangler_prefix(api_root: Path) -> list[str]:
    """Resolve a Win32-safe wrangler invocation (`.bin/wrangler` is a POSIX shim)."""
    js = api_root / "node_modules" / "wrangler" / "bin" / "wrangler.js"
    if js.is_file():
        return ["node", str(js)]
    cmd = api_root / "node_modules" / ".bin" / "wrangler.cmd"
    if cmd.is_file():
        return [str(cmd)]
    return ["npx", "wrangler"]


def upload(staging: Path, bucket: str, api_root: Path, workers: int = 8, prune: bool = False) -> int:
    # Wrangler's object CLI cannot set custom metadata; use its remote R2 binding.
    command = ["node", str(TOOL_ROOT / "upload_corpus.mjs"), str(api_root), str(staging), bucket, str(max(1, workers))]
    if prune:
        command.append("--prune")
    result = subprocess.run(command, cwd=api_root, check=False)
    return result.returncode


def reindex(api_root: Path, instance: str) -> int:
    result = subprocess.run(
        [*wrangler_prefix(api_root), "ai-search", "jobs", "create", instance, "--json"],
        cwd=api_root,
        check=False,
    )
    if result.returncode:
        print("Upload succeeded but reindex request failed; check ai-search jobs list before retrying because the job may have started.", flush=True)
    else:
        print("Reindex requested; check ai-search jobs list for completion.", flush=True)
    return result.returncode


def refresh_staging(manifest: dict, staging: Path, skip_fetch: bool) -> None:
    previous = staging.with_name(staging.name + ".previous")
    if previous.exists():
        raise RuntimeError(f"recover previous staging at {previous} before refreshing")
    with tempfile.TemporaryDirectory(prefix=".corpus-refresh-", dir=staging.parent) as temporary:
        candidate = Path(temporary) / "candidate"
        candidate.mkdir()
        copied, skipped = stage_repos(manifest, candidate)
        stage_pin(candidate)
        vendor_ok, vendor_fail = (0, 0) if skip_fetch else stage_vendors(manifest, candidate)
        print(f"staged repo files={copied} skipped={skipped} vendor_ok={vendor_ok} vendor_fail={vendor_fail}")
        if vendor_fail:
            raise RuntimeError(f"{vendor_fail} vendor documents failed")
        if staging.exists():
            staging.rename(previous)
        try:
            candidate.rename(staging)
        except OSError:
            if previous.exists():
                previous.rename(staging)
            raise
        if previous.exists():
            shutil.rmtree(previous)


def main() -> None:
    parser = argparse.ArgumentParser(description="Stage and optionally upload and reindex the Trackdub docs corpus")
    parser.add_argument("--upload", action="store_true", help="Put staged objects with metadata into R2, then request reindex")
    parser.add_argument("--skip-fetch", action="store_true", help="Stage repo files only")
    parser.add_argument(
        "--reuse-staging",
        action="store_true",
        help="Upload existing .staging without re-fetching or clearing",
    )
    parser.add_argument("--workers", type=int, default=8, help="Parallel R2 upload workers")
    parser.add_argument(
        "--prune",
        action="store_true",
        help="After a successful upload, delete bucket objects missing from staging; requires a full refresh",
    )
    args = parser.parse_args()
    blocking = [
        name
        for name, refused in (
            ("--skip-fetch", args.skip_fetch),
            ("--reuse-staging", args.reuse_staging),
            ("missing --upload", not args.upload),
        )
        if refused
    ]
    if args.prune and blocking:
        print(f"--prune deletes bucket objects missing from staging; refusing because of {', '.join(blocking)}", flush=True)
        raise SystemExit(2)
    manifest = load_manifest()
    if not args.reuse_staging:
        try:
            refresh_staging(manifest, STAGING, args.skip_fetch)
        except (OSError, RuntimeError) as exc:
            print(f"Refresh failed; previous staging preserved; nothing uploaded: {exc}", flush=True)
            raise SystemExit(1) from exc
    else:
        if not STAGING.is_dir() or not any(path.is_file() for path in STAGING.rglob("*")):
            raise SystemExit(f"no staging tree at {STAGING}; run without --reuse-staging first")
        print(f"reusing staging at {STAGING}")
    if not args.upload:
        print(f"dry-run staging at {STAGING}")
        return
    api_root = Path(os.environ.get("API_TRACKDUB_ROOT", TRACKDUB_ROOT.parent / "api.trackdub")).resolve()
    uploaded = upload(STAGING, manifest["bucket"], api_root, workers=args.workers, prune=args.prune)
    if uploaded:
        raise SystemExit(uploaded)
    raise SystemExit(reindex(api_root, manifest["aiSearchInstance"]))


if __name__ == "__main__":
    main()
