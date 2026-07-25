#!/usr/bin/env python3
"""Verify .csproj ProjectReference edges match the canonical dependency graph."""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# Canonical allowed dependencies (short project names).
ALLOWED: dict[str, list[str]] = {
    "Trackdub.App.Avalonia": [
        "Trackdub.Application",
        "Trackdub.Composition",
        "Trackdub.Domain",
        "Trackdub.Media.Playback",
        "Trackdub.Sdk",
    ],
    "Trackdub.Api": [
        "Trackdub.Sdk",
        "Trackdub.Application",
        "Trackdub.Inference",
        "Trackdub.Media",
    ],
    "Trackdub.Cli": ["Trackdub.Sdk"],
    "Trackdub.Application": ["Trackdub.Contracts", "Trackdub.Domain", "Trackdub.Licensing"],
    "Trackdub.Infrastructure": [
        "Trackdub.Application",
        "Trackdub.Contracts",
        "Trackdub.Domain",
    ],
    "Trackdub.Media": ["Trackdub.Application", "Trackdub.Contracts", "Trackdub.Domain"],
    "Trackdub.Media.Playback": ["Trackdub.Application", "Trackdub.Domain"],
    "Trackdub.Inference": ["Trackdub.Contracts", "Trackdub.Domain"],
    "Trackdub.Inference.Onnx": [
        "Trackdub.Inference",
        "Trackdub.Contracts",
        "Trackdub.Domain",
    ],
    "Trackdub.Benchmarks": [
        "Trackdub.Application",
        "Trackdub.Composition",
        "Trackdub.Domain",
        "Trackdub.Inference",
        "Trackdub.Inference.Onnx",
        "Trackdub.Infrastructure",
    ],
    "Trackdub.Sdk": ["Trackdub.Composition", "Trackdub.Application", "Trackdub.Licensing"],
    "Trackdub.Composition": [
        "Trackdub.Application",
        "Trackdub.Inference",
        "Trackdub.Inference.Onnx",
        "Trackdub.Infrastructure",
        "Trackdub.Licensing",
        "Trackdub.Media",
        "Trackdub.Media.Playback",
    ],
    "Trackdub.Tools": [
        "Trackdub.Application",
        "Trackdub.Domain",
        "Trackdub.Infrastructure",
        "Trackdub.Media",
    ],
    # ADR-0011: Contracts may reference Domain only (intentional coupling).
    "Trackdub.Contracts": ["Trackdub.Domain"],
    "Trackdub.Domain": [],
    "Trackdub.Api.Tests": ["Trackdub.Api"],
    "Trackdub.App.Avalonia.Tests": [
        "Trackdub.Application",
        "Trackdub.Composition",
        "Trackdub.Contracts",
        "Trackdub.Domain",
    ],
    "Trackdub.Application.Tests": [
        "Trackdub.Api",
        "Trackdub.Application",
        "Trackdub.Domain",
        "Trackdub.Infrastructure",
        "Trackdub.Inference",
        "Trackdub.WebhookDelivery",
    ],
    "Trackdub.Architecture.Tests": [],
    "Trackdub.Benchmarks.Tests": [
        "Trackdub.Benchmarks",
        "Trackdub.Contracts",
        "Trackdub.Domain",
        "Trackdub.Inference",
        "Trackdub.Inference.Onnx",
        "Trackdub.Tools",
    ],
    "Trackdub.Composition.Tests": [
        "Trackdub.Application",
        "Trackdub.Composition",
        "Trackdub.Contracts",
        "Trackdub.Infrastructure",
        "Trackdub.Inference.Onnx",
        "Trackdub.Media",
    ],
    "Trackdub.Domain.Tests": ["Trackdub.Domain"],
    "Trackdub.Inference.Tests": [
        "Trackdub.Application",
        "Trackdub.Inference",
        "Trackdub.Inference.Onnx",
        "Trackdub.Infrastructure",
        "Trackdub.Domain",
        "Trackdub.Composition",
    ],
    "Trackdub.Infrastructure.Tests": [
        "Trackdub.Application",
        "Trackdub.Domain",
        "Trackdub.Infrastructure",
    ],
    "Trackdub.Media.Tests": ["Trackdub.Media", "Trackdub.Media.Playback", "Trackdub.Contracts"],
    "Trackdub.Inference.Onnx.Tests": ["Trackdub.Inference.Onnx", "Trackdub.Contracts"],
    "Trackdub.Sdk.Tests": [
        "Trackdub.Sdk",
        "Trackdub.Cli",
        "Trackdub.Application",
        "Trackdub.Composition",
    ],
    "Trackdub.UI.Tests": ["Trackdub.App.Avalonia"],
}

PROJECT_REF_PATTERN = re.compile(
    r'ProjectReference\s+Include="[^"]*[\\/ ]([^"\\/]+)\.csproj"',
    re.IGNORECASE,
)


def project_references(csproj_path: Path) -> list[str]:
    text = csproj_path.read_text(encoding="utf-8")
    return PROJECT_REF_PATTERN.findall(text)


def append_step_summary(line: str) -> None:
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary_path:
        return
    with open(summary_path, "a", encoding="utf-8") as handle:
        handle.write(line)
        if not line.endswith("\n"):
            handle.write("\n")


def main() -> int:
    append_step_summary("## Dependency Graph Audit")
    append_step_summary("")

    violations = 0
    csproj_paths = sorted(
        list((REPO_ROOT / "src").rglob("*.csproj"))
        + list((REPO_ROOT / "tests").rglob("*.csproj"))
    )

    for csproj_path in csproj_paths:
        project_name = csproj_path.parent.name
        refs = project_references(csproj_path)
        if not refs:
            continue

        allowed = ALLOWED.get(project_name)
        if allowed is None:
            print(f"::warning::Unknown project {project_name} — not in canonical graph.")
            continue

        allowed_set = set(allowed)
        for ref in refs:
            if ref in allowed_set:
                continue
            print(
                f"::error::Undocumented dependency: {project_name} → {ref} "
                "(not in canonical graph)"
            )
            append_step_summary(f"| {project_name} → {ref} | **VIOLATION** |")
            violations += 1

    if violations > 0:
        append_step_summary("")
        append_step_summary(
            f"**{violations} undocumented dependency edge(s) found.** "
            "Update AGENTS.md, CONTRIBUTING.md, CLAUDE.md, and architecture tests."
        )
        return 1

    message = "All project references match the canonical dependency graph — PASS"
    append_step_summary(message)
    print(message)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
