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

Each standalone copy is a top-level section of the concatenation. This asserts
the aggregate section that starts at the copy's own heading is identical to the
standalone file. The section is bounded by the next top-level heading, the
heading must appear exactly once (a duplicate would hide a stale copy), and an
empty standalone is a failure rather than a match. Comparison is line-based so
CRLF/LF differences do not produce false failures. On mismatch it reports the
first divergent line and both sides of it, which is where the fix belongs.
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


def is_top_level_heading(line: str) -> bool:
    """Return whether a line is a top-level (single ``#``) markdown heading."""
    return line == "#" or line.startswith("# ")


def trim_trailing_blank(lines: list[str]) -> list[str]:
    """Drop blank lines from the end of a line list."""
    while lines and not lines[-1].strip():
        lines.pop()
    return lines


def find_section(
    aggregate: list[str], heading: str
) -> tuple[bool, tuple[int, int] | str]:
    """Locate the aggregate section that starts at ``heading``.

    Returns ``(True, (start, end))`` with ``aggregate[start:end]`` the section
    (end exclusive) when the heading appears exactly once, or
    ``(False, reason)`` when the heading is missing or duplicated.
    """
    occurrences = [i for i, line in enumerate(aggregate) if line == heading]
    if not occurrences:
        return False, f"heading {heading!r} does not appear in {AGGREGATE} at all"
    if len(occurrences) > 1:
        return False, (
            f"heading {heading!r} appears {len(occurrences)} times in "
            f"{AGGREGATE}; a duplicate section hides a stale copy"
        )

    start = occurrences[0]
    end = len(aggregate)
    for i in range(start + 1, len(aggregate)):
        if is_top_level_heading(aggregate[i]):
            end = i
            break
    return True, (start, end)


def describe_divergence(
    mirror_lines: list[str], section: list[str], start: int
) -> str:
    """Report the first line where a mirror stops matching its aggregate section."""
    for offset in range(max(len(mirror_lines), len(section))):
        expected = (
            mirror_lines[offset]
            if offset < len(mirror_lines)
            else "<missing from standalone>"
        )
        actual = (
            section[offset] if offset < len(section) else "<end of file>"
        )
        if expected != actual:
            return (
                f"diverges at {AGGREGATE} line {start + offset + 1}\n"
                f"        expected: {expected.strip()!r}\n"
                f"        actual:   {actual.strip()!r}"
            )
    return "no divergence located"


def compare_mirror(aggregate: list[str], mirror: str) -> str | None:
    """Return a failure description for one mirror, or None when it is in sync."""
    mirror_lines = trim_trailing_blank(read_lines(mirror))
    content = [line for line in mirror_lines if line.strip()]
    if not content:
        return "file is empty"

    heading = content[0]
    found, section_info = find_section(aggregate, heading)
    if not found:
        return str(section_info)

    start, end = section_info
    section = trim_trailing_blank(aggregate[start:end])
    if section == mirror_lines:
        return None
    return describe_divergence(mirror_lines, section, start)


def main() -> int:
    """Check every mirror against the aggregate and return the exit code."""
    aggregate = read_lines(AGGREGATE)
    failures: list[str] = []

    for mirror in MIRRORS:
        failure = compare_mirror(aggregate, mirror)
        if failure is None:
            print(f"  in sync: {mirror}")
            continue
        failures.append(f"{mirror}: {failure}")

    if failures:
        print("Audit-mirror check failed:")
        for failure in failures:
            print(f"  {failure}")
        print()
        print(
            f"Each file listed in MIRRORS must appear verbatim as a top-level "
            f"section of {AGGREGATE}. Edit one and re-sync the others, or "
            "re-cut the aggregate from its sources."
        )
        return 1

    print("Audit-mirror check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
