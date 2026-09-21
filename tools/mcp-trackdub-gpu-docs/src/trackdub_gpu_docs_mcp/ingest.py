from __future__ import annotations

import argparse
import re
from html import unescape
from pathlib import Path

import httpx
from bs4 import BeautifulSoup

from .corpus import CHUNKS_DIR, DATA_DIR, load_sources


USER_AGENT = (
    "TrackdubGpuDocsMcp/0.1 (+https://github.com/tonythethompson/Trackdub; "
    "curated offline corpus ingest for agent MCP)"
)


def html_to_text(html: str) -> str:
    soup = BeautifulSoup(html, "lxml")
    for tag in soup(["script", "style", "noscript", "nav", "footer", "header"]):
        tag.decompose()
    text = soup.get_text("\n")
    text = unescape(text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def ingest_url(url: str, dest: Path, *, timeout: float = 45.0) -> tuple[bool, str]:
    try:
        with httpx.Client(
            follow_redirects=True,
            timeout=timeout,
            headers={"User-Agent": USER_AGENT, "Accept": "text/html,application/xhtml+xml"},
        ) as client:
            response = client.get(url)
            response.raise_for_status()
            content_type = response.headers.get("content-type", "")
            body = response.text
            if "html" in content_type or body.lstrip().startswith("<"):
                body = html_to_text(body)
            if not body.strip():
                return False, "empty body after extract"
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text(body, encoding="utf-8")
            return True, f"wrote {dest} ({len(body)} chars)"
    except Exception as exc:  # noqa: BLE001 - report per-URL failures to CLI
        return False, f"{type(exc).__name__}: {exc}"


def run_ingest(*, only_id: str | None = None, force: bool = False) -> int:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    CHUNKS_DIR.mkdir(parents=True, exist_ok=True)
    ok = 0
    fail = 0
    skip = 0
    for source in load_sources():
        if source.kind != "url":
            continue
        if only_id and source.id != only_id:
            continue
        dest = CHUNKS_DIR / f"{source.id}.txt"
        if dest.is_file() and not force:
            print(f"skip {source.id} (cached)")
            skip += 1
            continue
        assert source.url
        success, message = ingest_url(source.url, dest)
        status = "ok" if success else "FAIL"
        print(f"{status} {source.id}: {message}")
        if success:
            ok += 1
        else:
            fail += 1
    print(f"done: ok={ok} fail={fail} skip={skip}")
    return 0 if fail == 0 else 1


def main() -> None:
    parser = argparse.ArgumentParser(description="Ingest allowlisted remote docs into .data/chunks")
    parser.add_argument("--id", dest="only_id", help="Ingest a single source id")
    parser.add_argument("--force", action="store_true", help="Re-fetch even if cached")
    args = parser.parse_args()
    raise SystemExit(run_ingest(only_id=args.only_id, force=args.force))


if __name__ == "__main__":
    main()
