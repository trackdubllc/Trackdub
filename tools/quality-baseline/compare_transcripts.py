"""WER/CER matching Trackdub.Benchmarks.TextErrorRate semantics.

Usage:
  python compare_transcripts.py --reference ref.json --candidate cand.json
  python compare_transcripts.py --ref-text "..." --cand-text "..."

Accepts Trackdub raw-asr JSON (segments[].text) or plain text / .txt files.
Exit code 0 always prints metrics; use --max-wer to fail when exceeded.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def normalize(text: str) -> str:
    chars = [c.lower() if c.isalnum() else " " for c in text]
    return " ".join("".join(chars).split())


def tokenize_words(text: str) -> list[str]:
    return normalize(text).split(" ")


def edit_distance(a: list, b: list) -> int:
    if not a:
        return len(b)
    if not b:
        return len(a)
    prev = list(range(len(b) + 1))
    for i, ai in enumerate(a, 1):
        cur = [i] + [0] * len(b)
        for j, bj in enumerate(b, 1):
            cost = 0 if ai == bj else 1
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
        prev = cur
    return prev[-1]


def wer(reference: str, candidate: str) -> float:
    ref = tokenize_words(reference)
    cand = tokenize_words(candidate)
    if not ref:
        return 0.0 if not cand else 1.0
    return edit_distance(ref, cand) / len(ref)


def cer(reference: str, candidate: str) -> float:
    ref = normalize(reference).replace(" ", "")
    cand = normalize(candidate).replace(" ", "")
    if not ref:
        return 0.0 if not cand else 1.0
    return edit_distance(list(ref), list(cand)) / len(ref)


def load_text(path: str | None = None, text: str | None = None) -> str:
    if text is not None:
        return text
    assert path
    p = Path(path)
    raw = p.read_text(encoding="utf-8-sig")
    if p.suffix.lower() == ".json":
        data = json.loads(raw)
        if isinstance(data, dict) and "segments" in data:
            return " ".join(s.get("text") or "" for s in data["segments"])
        if isinstance(data, dict) and "text" in data:
            return str(data["text"])
        if isinstance(data, list):
            return " ".join(str(x.get("text") or x) if isinstance(x, dict) else str(x) for x in data)
    return raw


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--reference")
    ap.add_argument("--candidate")
    ap.add_argument("--ref-text")
    ap.add_argument("--cand-text")
    ap.add_argument("--max-wer", type=float, default=None, help="Exit 1 if WER exceeds this")
    ap.add_argument("--max-cer", type=float, default=None)
    args = ap.parse_args()

    ref = load_text(args.reference, args.ref_text)
    cand = load_text(args.candidate, args.cand_text)
    w = wer(ref, cand)
    c = cer(ref, cand)
    print(f"wer={w:.4f}")
    print(f"cer={c:.4f}")
    print(f"reference_chars={len(ref)} candidate_chars={len(cand)}")
    print(f"reference_words={len(tokenize_words(ref))} candidate_words={len(tokenize_words(cand))}")
    failed = False
    if args.max_wer is not None and w > args.max_wer:
        print(f"FAIL wer {w:.4f} > {args.max_wer}", file=sys.stderr)
        failed = True
    if args.max_cer is not None and c > args.max_cer:
        print(f"FAIL cer {c:.4f} > {args.max_cer}", file=sys.stderr)
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
