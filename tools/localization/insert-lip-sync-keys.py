#!/usr/bin/env python3
"""Insert PR #400 lip-sync resource keys into locale resx files without re-encoding."""

from __future__ import annotations

import json
import os
import urllib.request
from xml.sax.saxutils import escape

SOURCE_STRINGS = [
    "Align and stretch dubbed audio to lip timing using the forced-alignment model selected above.",
    (
        "Experimental video lip repair (LatentSync ~6.7 GB + face models). "
        "Requires video, dubbed mix, and speaker turns. Download models before Run."
    ),
    "REPAIR",
    "Lip-sync runtime",
]

LOCALES = [
    ("App.de.resx", "DE"),
    ("App.es.resx", "ES"),
    ("App.fr.resx", "FR"),
    ("App.he.resx", "HE"),
    ("App.hi.resx", "HI"),
    ("App.it.resx", "IT"),
    ("App.ja.resx", "JA"),
    ("App.ko.resx", "KO"),
    ("App.nl.resx", "NL"),
    ("App.pt-BR.resx", "PT-BR"),
    ("App.ru.resx", "RU"),
    ("App.sv.resx", "SV"),
    ("App.tr.resx", "TR"),
    ("App.zh-hans.resx", "ZH"),
]


def deepl(texts: list[str], target_lang: str, api_key: str) -> list[str]:
    body = json.dumps(
        {
            "text": texts,
            "source_lang": "EN",
            "target_lang": target_lang,
            "formality": "prefer_more",
        }
    ).encode("utf-8")
    req = urllib.request.Request(
        "https://api-free.deepl.com/v2/translate",
        data=body,
        headers={
            "Authorization": f"DeepL-Auth-Key {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req) as resp:
        data = json.load(resp)
    return [item["text"] for item in data["translations"]]


def insert_after(lines: list[str], anchor: str, new_lines: list[str]) -> list[str]:
    for index, line in enumerate(lines):
        if anchor in line:
            return lines[: index + 1] + new_lines + lines[index + 1 :]
    raise RuntimeError(f"Anchor not found: {anchor}")


def main() -> None:
    api_key = os.environ.get("DEEPL_API_KEY")
    if not api_key:
        raise SystemExit("DEEPL_API_KEY is required")

    base = "src/Trackdub.App.Avalonia/Resources"
    for filename, lang in LOCALES:
        path = os.path.join(base, filename)
        raw = open(path, "rb").read()
        newline = b"\r\n" if b"\r\n" in raw else b"\n"
        text = raw.decode("utf-8-sig")
        if "Pipeline.LipSyncHint" in text:
            print(f"Skip {filename}")
            continue

        translated = deepl(SOURCE_STRINGS, lang, api_key)
        entries = [
            f'  <data name="Pipeline.LipSyncHint" xml:space="preserve"><value>{escape(translated[0])}</value></data>',
            f'  <data name="Pipeline.LipSynthesisHint" xml:space="preserve"><value>{escape(translated[1])}</value></data>',
            f'  <data name="Segment.LipSynthesisRepair" xml:space="preserve"><value>{escape(translated[2])}</value></data>',
            f'  <data name="Segment.Runtime.Alignment" xml:space="preserve"><value>{escape(translated[3])}</value></data>',
        ]

        lines = text.splitlines()
        lines = insert_after(lines, 'name="Pipeline.TtsHint"', entries[:2])
        lines = insert_after(lines, 'name="Segment.LipSyncAlign"', [entries[2]])
        lines = insert_after(lines, 'name="Segment.Runtime.Transcription"', [entries[3]])

        out = newline.join(line.encode("utf-8") for line in lines)
        if raw.endswith(newline):
            out += newline
        open(path, "wb").write(out)
        print(f"Updated {filename}")


if __name__ == "__main__":
    main()
