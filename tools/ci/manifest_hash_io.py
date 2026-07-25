"""Unbuffered manifest hash helpers (HF resolve downloads)."""

from __future__ import annotations

import hashlib
import sys
import urllib.request
from pathlib import Path
from typing import IO, TextIO

CHUNK_BYTES = 8 * 1024 * 1024
PROGRESS_MIB = 100


def configure_unbuffered_stdout() -> None:
    try:
        sys.stdout.reconfigure(line_buffering=True)  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass


def emit(message: str, *, log: TextIO | None = None) -> None:
    print(message, flush=True)
    if log is not None:
        log.write(message + "\n")
        log.flush()


def sha256_url(
    url: str,
    *,
    timeout: int = 900,
    label: str | None = None,
    log: TextIO | None = None,
) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "Trackdub-manifest-hash-audit/1.0"})
    digest = hashlib.sha256()
    total = 0
    last_report_mib = -1
    display = label or url
    emit(f"  -> {display}", log=log)

    with urllib.request.urlopen(request, timeout=timeout) as response:
        while True:
            chunk = response.read(CHUNK_BYTES)
            if not chunk:
                break
            digest.update(chunk)
            total += len(chunk)
            report_mib = total // (1024 * 1024) // PROGRESS_MIB
            if report_mib > last_report_mib:
                last_report_mib = report_mib
                emit(f"     {display}: {total // (1024 * 1024)} MiB", log=log)

    emit(f"     {display}: done ({total // (1024 * 1024)} MiB)", log=log)
    return digest.hexdigest()


def open_log(path: Path) -> IO[str]:
    path.parent.mkdir(parents=True, exist_ok=True)
    return path.open("a", encoding="utf-8", newline="\n")
