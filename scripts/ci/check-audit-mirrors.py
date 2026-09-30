#!/usr/bin/env python3
"""Fail when the concatenated dead-code audit drifts from its standalone copies.

``docs/audits/audits.md`` is a concatenation that embeds the dead-code audit
review and its verification pass verbatim, while the same text also ships
standalone as ``Trackdub-dead-code-audit-review.md`` and
``Trackdub-dead-code-audit-verification.md``.

Nothing mechanically keeps those in sync, so an edit lands in one copy and the
others silently keep the old figures. That has already caused real damage: a
follow-up commit inside #318 edited only ``audits.md`` and left the standalone
review disagreeing, and a later fix restored a totals row in one file while a
third copy kept the stale number.

This asserts each standalone file appears verbatim and contiguously in the
concatenation. Comparison is line-based so CRLF/LF differences do not produce
false failures. On mismatch it reports the first divergent line and both
sides of it, which is where the fix belongs.
"""

from __future__ import annotations

from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

AGGREGATE = "docs/audits/audits.md"

# Standalone copies embedded verbatim in the aggregate above.
MIRRORS = (
    "docs/audits/Trackdub-dead-code-audit-review.md",
    "docs/audits/Trackdub-dead-code-audit-verification.md",
)


def read_lines(relative: str) -> list[str]:
    """Read a file as lines, normalizing line endings across platforms."""
    try:
        return (REPO_ROOT / relative).read_text(encoding="utf-8").splitlines()
    except FileNotFoundError:
        print(f"Error: File not found: {relative}")
        raise SystemExit(1)
    except Exception as e:
        print(f"Error reading {relative}: {e}")
        raise SystemExit(1)


def find_contiguous(haystack: list[str], needle: list[str]) -> int:
    """Return the index where needle starts inside haystack, or -1."""
    span = len(needle)
    if span == 0:
        return -1
    for start in range(len(haystack) - span + 1):
        if haystack[start : start + span] == needle:
            return start
    return -1


def describe_divergence(mirror_lines: list[str], aggregate_lines: list[str]) -> str:
    """Report the first line where a mirror stops matching the aggregate."""
    anchor = next((i for i, line in enumerate(mirror_lines) if line.strip()), None)
    if anchor is None:
        return "file is empty"

    heading = mirror_lines[anchor]

    # Anchor on the last occurrence of that heading in the aggregate: the
    # concatenation may repeat a heading from an unrelated audit earlier on.
    start = -1
    for i in range(len(aggregate_lines) - 1, -1, -1):
        if aggregate_lines[i] == heading:
            start = i
            break
    if start < 0:
        return f"heading {heading.strip()!r} does not appear in {AGGREGATE} at all"

    for offset, line in enumerate(mirror_lines[anchor:]):
        position = start + offset
        actual = (
            aggregate_lines[position]
            if position < len(aggregate_lines)
            else "<end of file>"
        )
        if actual != line:
            return (
                f"diverges at {AGGREGATE} line {position + 1}\n"
                f"        expected: {line.strip()!r}\n"
                f"        actual:   {actual.strip()!r}"
            )

    return "no divergence located; the copy is present but not contiguous"


def main() -> int:
    aggregate = read_lines(AGGREGATE)
    failures: list[str] = []

    for mirror in MIRRORS:
        mirror_lines = read_lines(mirror)
        if find_contiguous(aggregate, mirror_lines) >= 0:
            print(f"  in sync: {mirror} ({len(mirror_lines)} lines)")
            continue
        failures.append(f"{mirror}: {describe_divergence(mirror_lines, aggregate)}")

    if failures:
        print("Audit-mirror check failed:")
        for failure in failures:
            print(f"  {failure}")
        print()
        print(
            f"Each file listed in MIRRORS must appear verbatim and contiguously "
            f"in {AGGREGATE}. Edit one and re-sync the others, or re-cut the "
            "aggregate from its sources."
        )
        return 1

    print("Audit-mirror check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
