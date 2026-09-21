"""Stage first-party and vendor docs, then upload the tree to R2 for AI Search."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from html.parser import HTMLParser
from pathlib import Path


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
            print(f"skip repo {repo['id']}: root missing")
            continue
        prefix = repo["prefix"]
        seen: set[Path] = set()
        for pattern in repo["include"]:
            for path in root.glob(pattern):
                if not path.is_file() or path in seen:
                    continue
                if any(part in {".git", "node_modules", ".venv"} for part in path.parts):
                    continue
                seen.add(path)
                if path.stat().st_size > max_bytes:
                    print(f"skip large {path}")
                    skipped += 1
                    continue
                rel = path.relative_to(root).as_posix()
                dest = staging / prefix / rel
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(path.read_bytes())
                copied += 1
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


def upload(staging: Path, bucket: str, api_root: Path, workers: int = 8) -> int:
    files = sorted(path for path in staging.rglob("*") if path.is_file())
    command_prefix = wrangler_prefix(api_root)
    config = api_root / "wrangler.docs-rag.jsonc"

    def put_one(path: Path) -> tuple[str, bool, str]:
        key = path.relative_to(staging).as_posix()
        cmd = [
            *command_prefix,
            "r2",
            "object",
            "put",
            f"{bucket}/{key}",
            "--file",
            str(path),
            "--remote",
            "--config",
            str(config),
        ]
        result = subprocess.run(
            cmd,
            cwd=api_root,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        if result.returncode != 0:
            return key, False, result.stderr.strip() or result.stdout.strip()
        return key, True, ""

    failed = 0
    done = 0
    with ThreadPoolExecutor(max_workers=max(1, workers)) as pool:
        futures = [pool.submit(put_one, path) for path in files]
        for future in as_completed(futures):
            key, ok, message = future.result()
            done += 1
            if ok:
                print(f"put ({done}/{len(files)}) {key}", flush=True)
            else:
                failed += 1
                print(f"FAIL ({done}/{len(files)}) {key}: {message}", flush=True)
    print(f"upload done: {len(files) - failed}/{len(files)}", flush=True)
    return 0 if failed == 0 else 1


def clear_staging(staging: Path) -> None:
    if not staging.exists():
        return
    for child in sorted(staging.rglob("*"), reverse=True):
        if child.is_file():
            child.unlink()
        elif child.is_dir():
            child.rmdir()


def main() -> None:
    parser = argparse.ArgumentParser(description="Stage and optionally upload the Trackdub docs corpus")
    parser.add_argument("--upload", action="store_true", help="Put staged objects into R2")
    parser.add_argument("--skip-fetch", action="store_true", help="Stage repo files only")
    parser.add_argument(
        "--reuse-staging",
        action="store_true",
        help="Upload existing .staging without re-fetching or clearing",
    )
    parser.add_argument("--workers", type=int, default=8, help="Parallel R2 upload workers")
    args = parser.parse_args()
    manifest = load_manifest()
    if not args.reuse_staging:
        clear_staging(STAGING)
        STAGING.mkdir(parents=True, exist_ok=True)
        copied, skipped = stage_repos(manifest, STAGING)
        stage_pin(STAGING)
        vendor_ok, vendor_fail = (0, 0)
        if not args.skip_fetch:
            vendor_ok, vendor_fail = stage_vendors(manifest, STAGING)
        print(f"staged repo files={copied} skipped={skipped} vendor_ok={vendor_ok} vendor_fail={vendor_fail}")
    else:
        if not STAGING.is_dir() or not any(STAGING.rglob("*")):
            raise SystemExit(f"no staging tree at {STAGING}; run without --reuse-staging first")
        print(f"reusing staging at {STAGING}")
    if not args.upload:
        print(f"dry-run staging at {STAGING}")
        return
    api_root = Path(os.environ.get("API_TRACKDUB_ROOT", TRACKDUB_ROOT.parent / "api.trackdub")).resolve()
    raise SystemExit(upload(STAGING, manifest["bucket"], api_root, workers=args.workers))


if __name__ == "__main__":
    main()
