#!/usr/bin/env python3
"""Check that an item list can supply every mixgen recipe in both splits.

Works on candidate lists from discover.py and on ingested manifests. Splits come from the same
deterministic group hash ingest.py uses, so the answer is the one the corpus will actually get.
Exit status is 1 when any recipe lacks sources in a split.

    python recipe_coverage.py --items items.v1.jsonl [--min-groups 3]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import ingest
import mixgen

SPLITS = ("dev", "test")
RIR_RT60_NOTE = "RIR candidates are only checked for RT60 (0.4 to 2.0 s) after download"


def load_items(path: Path) -> list[dict]:
    text = path.read_text(encoding="utf-8-sig")
    if path.suffix == ".json":
        return json.loads(text)["items"]
    return ingest.read_jsonl(path)


def needs(recipe: mixgen.Recipe) -> list[tuple[str, tuple[str, ...], int]]:
    """(role, required tags, distinct groups needed) for everything a recipe draws on."""
    out: list[tuple[str, tuple[str, ...], int]] = []
    if recipe.dialogue:
        out.append(("dialogue", recipe.dialogue_tags, 2 if recipe.overlap else 1))
    out += [(layer.role, layer.tags, 1) for layer in recipe.layers]
    if recipe.reverb:
        out.append(("rir", (), 1))
    return out


def count(items: list[dict], split: str, role: str, tags: tuple[str, ...]) -> tuple[int, int]:
    """(items, distinct groups) in `split` with the role and every tag."""
    matching = [i for i in items
                if i["role"] == role and set(tags) <= set(i.get("tags", []))
                and ingest.assign_split(i["group"]) == split]
    return len(matching), len({i["group"] for i in matching})


def check(items: list[dict], min_groups: int) -> tuple[list[dict], list[str]]:
    rows, gaps = [], []
    for recipe in mixgen.RECIPES.values():
        for role, tags, groups_needed in needs(recipe):
            row = {"recipe": recipe.recipe_id, "source": role + (f"+{','.join(tags)}" if tags else "")}
            for split in SPLITS:
                n_items, n_groups = count(items, split, role, tags)
                row[split] = (n_items, n_groups)
                if n_groups < max(min_groups, groups_needed):
                    gaps.append(f"{recipe.recipe_id} {row['source']} [{split}]: {n_groups} group(s), "
                                f"need {max(min_groups, groups_needed)}")
            rows.append(row)
    return rows, gaps


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--items", type=Path, required=True)
    p.add_argument("--min-groups", type=int, default=3, help="distinct groups required per source and split")
    args = p.parse_args(argv)
    items = load_items(args.items)
    rows, gaps = check(items, args.min_groups)
    print(f"{len(items)} items; counts are items/groups in dev and test")
    for r in rows:
        print(f"  {r['recipe']:4} {r['source']:28} dev {r['dev'][0]:3}/{r['dev'][1]:<3} test {r['test'][0]:3}/{r['test'][1]:<3}")
    unverified = sum(1 for i in items if i.get("tags") and i.get("tag_basis") == "search-query")
    if unverified:
        print(f"{unverified} tagged items rely on search-query tags that have not been listened to")
    if any(i["role"] == "rir" for i in items):
        print(RIR_RT60_NOTE)
    for g in gaps:
        print(f"GAP: {g}", file=sys.stderr)
    return 1 if gaps else 0


if __name__ == "__main__":
    raise SystemExit(main())
