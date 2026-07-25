#!/usr/bin/env python3
"""Apply commercial-use audit outcomes to bundled-models.manifest.json."""

from __future__ import annotations

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
DECODER_HASHES_PATH = REPO_ROOT / "tools/ci/hashes-opus-decoder.json"

# Verified 2026-06-13 via tools/ci/verify-manifest-hashes.py --structural and HF resolve downloads.


def build_opus_sources(model_id: str, revision: str, files: list[str]) -> dict[str, str]:
    base = f"https://huggingface.co/{model_id}/resolve/{revision}"
    return {path: f"{base}/{path}" for path in files}


def load_decoder_hashes() -> dict[str, str]:
    if not DECODER_HASHES_PATH.is_file():
        return {}
    with DECODER_HASHES_PATH.open(encoding="utf-8") as handle:
        return json.load(handle)


def merge_opus_hashes(base_hashes: dict[str, str], model_id: str) -> dict[str, str]:
    merged = dict(base_hashes)
    decoder_hashes = load_decoder_hashes()
    decoder_hash = decoder_hashes.get(model_id)
    if decoder_hash:
        merged["onnx/decoder_model_merged.onnx"] = decoder_hash
    return merged


OPUS_ONNX_COMMUNITY_HASHES: dict[str, dict[str, str]] = {
    "onnx-community/opus-mt-en-es": merge_opus_hashes(
        {
            "source.spm": "4dd547c24816a335e7b0b2e63376a8f1b3cbfc671eda5ab808dd44fdadaa8791",
            "target.spm": "e236ee6d866b635c0142114f8647f39831f9d92534aa2aad75c942f6a78ad0e3",
            "vocab.json": "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163",
            "onnx/encoder_model.onnx": "6db5233c7d899fb25fb10b32f4824dea3210062e714cd1265148a5c6eaabfe39",
        },
        "onnx-community/opus-mt-en-es",
    ),
    "onnx-community/opus-mt-es-en": merge_opus_hashes(
        {
            "source.spm": "e236ee6d866b635c0142114f8647f39831f9d92534aa2aad75c942f6a78ad0e3",
            "target.spm": "4dd547c24816a335e7b0b2e63376a8f1b3cbfc671eda5ab808dd44fdadaa8791",
            "vocab.json": "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163",
            "onnx/encoder_model.onnx": "fee8396bc1558afbcbee3f7c35a8fc4c92a6423e22b2d8e29f9371fe9e0a0bb0",
        },
        "onnx-community/opus-mt-es-en",
    ),
    "onnx-community/opus-mt-en-fr": merge_opus_hashes(
        {
            "source.spm": "173e9f493a668fe396d599e28d414a201193094e6ffd7a4678e5aab0f6d3d838",
            "target.spm": "78d0e717c77053f1c4b856d8661d9cb87c64f083a35418c087b9146300e4f585",
            "vocab.json": "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d",
            "onnx/encoder_model.onnx": "15f3560a9982e2172d5fd64dd7221a7c8d7e46fb7ac2c02c9c151f386f62c312",
        },
        "onnx-community/opus-mt-en-fr",
    ),
    "onnx-community/opus-mt-en-de": merge_opus_hashes(
        {
            "source.spm": "678f2a1177d8389f67b66299762dcc4fc567e89b07e212ba91b0c56daecf47ce",
            "target.spm": "bbd1f495eea99c8e21ae086d9146e0fa7b096c3dfdd9ba07ab8b631889df5c9b",
            "vocab.json": "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44",
            "onnx/encoder_model.onnx": "c61f4102d27f484d7e38e03d7ce94c574e189df0c6cbe38a1508858ea989b1d8",
        },
        "onnx-community/opus-mt-en-de",
    ),
    "onnx-community/opus-mt-en-it": merge_opus_hashes(
        {
            "source.spm": "2efff2c37e51842c2817a6770280979b5dd803cebbe2928e576d42a888f89537",
            "target.spm": "ef5b179cd5933782af78595099807b5c0156bd67665e4c3af425c7aec6d02d34",
            "vocab.json": "40c98ffc0013f2f09ed9127418131738ad9c2de4d8f7f52d52532332bb2bf78c",
            "onnx/encoder_model.onnx": "281e14cc8380cad3ec946079810f4a80c349d8a287973d258b948ced797937f8",
        },
        "onnx-community/opus-mt-en-it",
    ),
    "onnx-community/opus-mt-en-ROMANCE": merge_opus_hashes(
        {
            "config.json": "465e48b2254f81906f48034756255597693d8d9a12dfe322a6e0d3a9705d1542",
            "generation_config.json": "9af519fb778df0d27579991a3cc759e9ce1dacbd635d9e26833b010ddf67bd11",
            "source.spm": "b1f89e7b05828846baf19109a61029a36838f1c4f3defc218e6aee0ee2787864",
            "target.spm": "5e54632fe5ce5ef4108a827daccc121b7cd4ea589fbb4bd856b8dd06086166b2",
            "vocab.json": "d9c3b6b01d3a9c9403befa60fd17e4a50dc4929698e3d6b9c2e7f066a9d1f041",
            "onnx/encoder_model.onnx": "fde3634a40d5a3681b58b595267c7264e69f67db80fa8c41a98a11190cd52404",
        },
        "onnx-community/opus-mt-en-ROMANCE",
    ),
}

OPUS_XENOVA_HASHES: dict[str, dict[str, str]] = {
    "Xenova/opus-mt-es-fr": merge_opus_hashes(
        {
            "source.spm": "0807c7b3dabe387a083700920a4b406c7656db09922bf934c25c2fab24706c4d",
            "target.spm": "74ffc930c4af87e3bdec6dde479eb36f05152e1a657ec65dfb923baf4b5d27a9",
            "vocab.json": "00298f6b835a284fc55779eb3af674807e71984169682d5156ed92184b3b0f0a",
            "onnx/encoder_model.onnx": "ddb496e558d1baecc4a5c80b1a2521bea46f3fe33b6a1e279e2493c1d2e9f010",
        },
        "Xenova/opus-mt-es-fr",
    ),
    "Xenova/opus-mt-es-de": merge_opus_hashes(
        {
            "source.spm": "d15fb47311fe1d385213350041a25dcb2f2b0ceb7d7102b7853b1405b33fd794",
            "target.spm": "4c6cf9cacbfbf82db361e3e07df76172cce7d1ef683d4e7d090349c84246caf0",
            "vocab.json": "d5789816a8a925181ad70fd1c452cd48d6658040e74208512ce18eee55e8e1f9",
            "onnx/encoder_model.onnx": "c4fa10ea19c4f85105a203dd80b78ab3467737f7d57d7c5cc73de5f53dcb27ff",
        },
        "Xenova/opus-mt-es-de",
    ),
    "Xenova/opus-mt-es-it": merge_opus_hashes(
        {
            "source.spm": "65513650404974598f0fb0a3156a243c12ad57c38fcc3367c2c6aef7ed4d3865",
            "target.spm": "03264679b2a1831b20d3ef2cc946ac99e3acab74ad965d77c637e8253ea30cd3",
            "vocab.json": "38a184f18ecef75da7e18105aeaeb0e3286cbfc66ab4c03a5238938c3fd1ee86",
            "onnx/encoder_model.onnx": "07ee91d250f235d4e04a76d8e11a5704474fc19576c42485d87f6c794fac9880",
        },
        "Xenova/opus-mt-es-it",
    ),
}

QWEN25_HASHES = {
    "genai_config.json": "b1fabffd833cfdd244a06ea3db3ddfc5eaaaafa360ec6d8c5704f1a97d0b8a0f",
    "model.onnx": "8232b31cf8f70f2cda1eaa39b020b58e7c074e223ab3a1ae155bfe4b8ce7fb8b",
    "model.onnx.data": "a02623aae6e74951548dc0db6c20525347d28e9632472b3ee99e7e5a76244581",
    "tokenizer.json": "3fd169731d2cbde95e10bf356d66d5997fd885dd8dbb6fb4684da3f23b2585d8",
    "tokenizer_config.json": "af8f860ca95ed086e596b865476e5312dc043355df6b8c4ae83f437199358535",
    "config.json": "98d2ff8cc47488d08a2b0b3acf4eb99ef210779b42bd48605f6b8e36acdbf670",
    "chat_template.jinja": "8aa40ce145adb73cb3a75194dc0224702a95850ec5275cabb728496bbd749fc6",
}

MADLAD_REVISION = "67037ad42f58d6c0fc3dafaa45f3ec97a46e7eb9"
MADLAD_HASHES = {
    "config.json": "a7f208db9a60aad30bdbbdd6beb1de5c92a14e1a4c85ce37de7201ea6c3712d9",
    "spiece.model": "ef11ac9a22c7503492f56d48dce53be20e339b63605983e9f27d2cd0e0f3922c",
    "tokenizer_config.json": "21de89977129392c6f079c6fe3f8f616dd09c7c85d468084aec278111dcb1a",
    "encoder_model_quantized.onnx": "3ab4690aab2174ca29a9ce30cdb8152d9b639606cf2cf48e77112314e064e393",
    "decoder_model_quantized.onnx": "1894294807c530df6a17e773fe8753833866838fcd41084bdd21cad17e0b50c1",
}


def apply_flip(model: dict, file_hashes: dict[str, str]) -> None:
    model["commercial_use_verified"] = True

    merged_hashes = dict(model.get("download_file_hashes") or {})
    merged_hashes.update(file_hashes)
    model["download_file_hashes"] = merged_hashes
    model["hash_verification"] = {"mode": "required"}

    download_files = set(model.get("download_files") or [])
    download_files.update(file_hashes.keys())
    model["download_files"] = sorted(download_files)

    benchmark = model.get("benchmark_entry")
    if benchmark and benchmark in merged_hashes:
        model["sha256"] = merged_hashes[benchmark]


def main() -> None:
    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model in catalog["models"]:
        model_id = model["model_id"]

        if model_id in OPUS_ONNX_COMMUNITY_HASHES:
            hashes = OPUS_ONNX_COMMUNITY_HASHES[model_id]
            apply_flip(model, hashes)
            revision = model["revision"]
            sources = build_opus_sources(model_id, revision, list(hashes.keys()))
            merged_sources = dict(model.get("download_file_sources") or {})
            merged_sources.update(sources)
            model["download_file_sources"] = merged_sources
            continue

        if model_id in OPUS_XENOVA_HASHES:
            hashes = OPUS_XENOVA_HASHES[model_id]
            apply_flip(model, hashes)
            revision = model["revision"]
            sources = build_opus_sources(model_id, revision, list(hashes.keys()))
            merged_sources = dict(model.get("download_file_sources") or {})
            merged_sources.update(sources)
            model["download_file_sources"] = merged_sources
            continue

        if model_id == "tonythethompson/Qwen2.5-1.5B-Instruct":
            apply_flip(model, QWEN25_HASHES)
            revision = model["revision"]
            base = f"https://huggingface.co/{model_id}/resolve/{revision}"
            sources = {path: f"{base}/{path}" for path in QWEN25_HASHES}
            merged_sources = dict(model.get("download_file_sources") or {})
            merged_sources.update(sources)
            model["download_file_sources"] = merged_sources
            continue

        if model_id == "google/madlad400-3b-mt":
            apply_flip(model, MADLAD_HASHES)
            model["revision"] = MADLAD_REVISION
            model["source_url"] = "https://huggingface.co/tonythethompson/madlad400-3b-mt-onnx"
            base = f"https://huggingface.co/tonythethompson/madlad400-3b-mt-onnx/resolve/{MADLAD_REVISION}"
            sources = {path: f"{base}/{path}" for path in MADLAD_HASHES}
            merged_sources = dict(model.get("download_file_sources") or {})
            merged_sources.update(sources)
            model["download_file_sources"] = merged_sources

    with MANIFEST_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, indent=2)
        handle.write("\n")

    print(f"Updated {MANIFEST_PATH}")


if __name__ == "__main__":
    main()
