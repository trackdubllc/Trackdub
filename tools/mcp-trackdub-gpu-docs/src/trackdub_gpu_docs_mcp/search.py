from __future__ import annotations

import math
import re
from collections import Counter

from .corpus import Chunk, load_chunks


_TOKEN_RE = re.compile(r"[a-z0-9][a-z0-9+._/-]{1,}", re.IGNORECASE)


def tokenize(text: str) -> list[str]:
    return [t.lower() for t in _TOKEN_RE.findall(text)]


def search_chunks(query: str, *, limit: int = 8, tag: str | None = None) -> list[dict]:
    query_tokens = tokenize(query)
    if not query_tokens:
        return []

    chunks = load_chunks()
    if tag:
        tag_l = tag.lower()
        chunks = [c for c in chunks if any(tag_l == t.lower() for t in c.tags)]

    query_counts = Counter(query_tokens)
    scored: list[tuple[float, Chunk]] = []
    for chunk in chunks:
        text_l = chunk.text.lower()
        title_l = chunk.title.lower()
        token_counts = Counter(tokenize(chunk.text))
        overlap = 0.0
        for token, q_count in query_counts.items():
            tf = token_counts.get(token, 0)
            if tf:
                overlap += q_count * (1.0 + math.log(1 + tf))
            if token in title_l:
                overlap += 2.0 * q_count
            if token in text_l:
                overlap += 0.15 * q_count
        if overlap <= 0:
            continue
        score = overlap + (chunk.priority / 100.0)
        scored.append((score, chunk))

    scored.sort(key=lambda item: (-item[0], -item[1].priority, item[1].source_id, item[1].chunk_index))
    results: list[dict] = []
    for score, chunk in scored[: max(1, min(limit, 20))]:
        results.append(
            {
                "score": round(score, 3),
                "sourceId": chunk.source_id,
                "title": chunk.title,
                "origin": chunk.origin,
                "tags": list(chunk.tags),
                "chunkIndex": chunk.chunk_index,
                "excerpt": chunk.text[:1200],
            }
        )
    return results


def get_source_text(source_id: str, *, max_chars: int = 12000) -> dict:
    chunks = [c for c in load_chunks() if c.source_id == source_id]
    if not chunks:
        remote_hint = (
            "Source not loaded. For remote URLs, run: uv run trackdub-gpu-docs-ingest"
        )
        return {"found": False, "sourceId": source_id, "message": remote_hint}

    text = "\n\n".join(c.text for c in sorted(chunks, key=lambda c: c.chunk_index))
    truncated = len(text) > max_chars
    return {
        "found": True,
        "sourceId": source_id,
        "title": chunks[0].title,
        "origin": chunks[0].origin,
        "tags": list(chunks[0].tags),
        "truncated": truncated,
        "text": text[:max_chars],
    }
