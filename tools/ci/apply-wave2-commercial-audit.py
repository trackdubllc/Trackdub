#!/usr/bin/env python3
"""Apply Wave 2 commercial-use audit outcomes to bundled-models.manifest.json."""

from __future__ import annotations

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"


def build_sources(model_id: str, revision: str, files: list[str], *, base_repo: str | None = None) -> dict[str, str]:
    repo = base_repo or model_id
    base = f"https://huggingface.co/{repo}/resolve/{revision}"
    return {path: f"{base}/{path}" for path in files}


def apply_flip(model: dict, file_hashes: dict[str, str], *, sources: dict[str, str] | None = None) -> None:
    model["commercial_use_verified"] = True
    model["download_file_hashes"] = file_hashes
    model["hash_verification"] = {"mode": "required"}
    if sources:
        model["download_file_sources"] = sources
    benchmark = model.get("benchmark_entry")
    if benchmark and benchmark in file_hashes:
        model["sha256"] = file_hashes[benchmark]
    if "notes" in model and isinstance(model["notes"], str) and "pending verification" in model["notes"].lower():
        del model["notes"]


SORTFORMER_HASHES = {
    "onnx/model.onnx": "82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00",
}

WHISPER_MEDIUM_HASHES = {
    "audio_processor_config.json": "d2720ef04446a78e8e134c3b3626f7d75e693bb9f66175239d842b3e1c9592c3",
    "decoder.onnx": "e0f07a8beab519e943962467d0ba23468c9bbce8ccb2378362f4214110c2be06",
    "decoder.onnx.data": "0c4ce05b8fdd494923968b4760832422fe10b99e3a9f687152cf8ec81d684694",
    "encoder.onnx": "b50c1851c7f1b838fa2a5df9505727b08679fcf3aaf785303acf3b9d1ac2cafd",
    "encoder.onnx.data": "2e26a9d4ac894f6f1163d04852094af32a24f3561f29c806d4929dc632d7eb67",
    "genai_config.json": "78a780e185837f6f7b9926bd0e79998f8389dc8192be3127e50305cc261489d6",
    "tokenizer.json": "7b469ff15eb7816315aa45eec391f5943d639b9d73d110f5c003df5192fd54e3",
    "tokenizer_config.json": "f622debfe8fa3ca299028f62dfb1642fc95ba217162b04b837a39fa92e2f4fc8",
}

WHISPER_LARGE_HASHES = {
    "audio_processor_config.json": "e953cc381bffb2a6d71f33ceaa26597659c0f2d586d97e31da4510bdaf4791c1",
    "decoder.onnx": "22e281991494678cd75cc5d3ea87fe00f55bd5e33b0bd28c77c262e0eb46196b",
    "decoder.onnx.data": "07d7b1a7672b529938a44a76c8747d5fee3d014f7427bb47a5e9493e35f5a07c",
    "encoder.onnx": "533891d99b036e5254581f345f3f1883cfc53e338d2568bffe06995111362da5",
    "encoder.onnx.data": "21a32452a493ac9c91fef24bfce7e83ba08b70512590a73bb3c223b2a251b337",
    "genai_config.json": "ca0d9d616f7124e3334ff349779b35e1079ff5349430aeb61f38162006c66110",
    "tokenizer.json": "5c1bf30c9e716e1477bedef846b01be0013daecb89e9e3ef7ab89b23c178df1b",
    "tokenizer_config.json": "e24e63287a0754b4d274a1e91ee8cb3e61ce5776f89586086c03b565230a5225",
}

QWEN3_ASR_06B_HASHES = {
    "added_tokens.json": "de40784677cbd1843cabe5fbee078c7e042cd0b62155f0810af5a13842e5722a",
    "config.json": "df31c4689abe9d782366fffd2454b546291d0205d082b3fd01b99fb76a45b11f",
    "decoder_init.onnx": "9d43df2bd332f039422bcf77b4ddfd09c8d15d334879d4ed316727b0eb06e57f",
    "decoder_step.onnx": "c5bb74a3758cd2e2e1bbb0c0b075b368fcca900a087cf688cf7909576b6c497a",
    "decoder_weights.data": "98ea738a3453fada779aa6c0f6e05489f6ca4da26f3861c8af74637d5d03a3b0",
    "embed_tokens.bin": "e80150119fa5f7e56e85aed64c3a02d5c78eb7a37cfdcb973d0987316f15bee2",
    "encoder.onnx": "3c027f880f677615de85e1f6934906e1d5d77624b724096d33accef99c753eed",
    "preprocessor_config.json": "9c7c558a05f326fe4365e5e59e486383ff127dfd93ce1cd5e6e23f883cd02281",
    "tokenizer.json": "bd2a97b55c8f7f9c328c73ee9b9178771037e9f566dfca8e238a063d41cbac92",
    "tokenizer_config.json": "784c92a0a81fbbc440afb89842f8c16c8da28dfb9ff381fcea585b84f4eed7d1",
    "vocab.json": "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
}

QWEN3_ASR_17B_HASHES = {
    "added_tokens.json": "de40784677cbd1843cabe5fbee078c7e042cd0b62155f0810af5a13842e5722a",
    "config.json": "d82437248b7c4674fe4091f98819a2a6167355b0fa66c0198f70622d1b778b49",
    "decoder_init.onnx": "209662efa6ddc74d2bef5dd1ce922409a3896f6fceddb15ab721393059818e39",
    "decoder_step.onnx": "45e90e376cb937d9c772d1c8d2b8330e34a94555b29730389e0fa8ff0ad2e1c0",
    "decoder_weights.data": "a46ff8cf2ef299a3737f7ebd78f39a8fb2ca273f2807f33f9b2a337ef0ebb4bb",
    "embed_tokens.bin": "1489075dbd08fcd6b87bc69c5feca278014d29fa5693bd4aa61e47c1a3a160d4",
    "encoder.onnx": "c19ff8d702707693877a509ba1867472b0ad964593b93751091891a143edffec",
    "preprocessor_config.json": "9c7c558a05f326fe4365e5e59e486383ff127dfd93ce1cd5e6e23f883cd02281",
    "tokenizer.json": "bd2a97b55c8f7f9c328c73ee9b9178771037e9f566dfca8e238a063d41cbac92",
    "tokenizer_config.json": "784c92a0a81fbbc440afb89842f8c16c8da28dfb9ff381fcea585b84f4eed7d1",
    "vocab.json": "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
}

DEEPFILTERNET_HASHES = {
    "enc.onnx": "7c5399d3da8a50ebef1c1a0ae421b33376aa5e45d0e92df16da7e83c9c131916",
    "erb_dec.onnx": "ab669a1d10afe20911728b33053a452071042317a90581092b325da7b2f9d895",
    "df_dec.onnx": "23114ce3b0f6464b763ee62f7bb8aab6b2a129a21eabd5bcfe59413db05f278a",
}

CHATTERBOX_TURBO_DEFAULT = {
    "tokenizer.json": "3f04e34bea22f9144d1a19151154095bc9ce0430bf421304f5797e716288a906",
    "onnx/conditional_decoder.onnx": "8c43f3a1d0ddb1a86e226a244d7cda5396c67f5c6412789c23900c646e3ffc50",
    "onnx/conditional_decoder.onnx_data": "05f162a519f3e9abaf0b7337ae037f4af8b2b30c4455d39b2c61ed3a9b2b5476",
    "onnx/embed_tokens.onnx": "27796e8252f36b463b0421cafdcc35b5f1e670ab0d96c9182f37ac6571c2f4bc",
    "onnx/embed_tokens.onnx_data": "a1c37edc6ec6adb655351f02e958da297221b50211c2c01b69312cb6f008a293",
    "onnx/language_model.onnx": "c12e31df78c74f9589b165c8d51e65171f5028b77b7fedb41900f55f7f410dc8",
    "onnx/language_model.onnx_data": "67db106868f5354b2e425651f1791aef36ae3e6f00ac5e1d91e32c985cad6b39",
    "onnx/speech_encoder.onnx": "4d66128037517dd51d370edc9b89ce36d42c75dcbd96e7216c7fb45dfae36045",
    "onnx/speech_encoder.onnx_data": "c9915ff6c529e7bb80983b525255e6744d6c39c7e35b12720925ba99ed0d0a2f",
}

CHATTERBOX_ONNX_DEFAULT = {
    "tokenizer.json": "ecddf6aaae85e271610a2743409f7e447886957d0b0836687c1eaec5ccac2891",
    "onnx/conditional_decoder.onnx": "1656d0d31332bae1854839959a3139300ebb67c178651dfa3f8c5fbfa5351351",
    "onnx/conditional_decoder.onnx_data": "51d58345a272747665ec9d5bb61e01835258a940e321a288582ac4c18cf01b5a",
    "onnx/embed_tokens.onnx": "160722ec14789f616abdb1e31916cbbf9223c03fde0ab546d64ca74fb72e430b",
    "onnx/embed_tokens.onnx_data": "898c563c3a5ca1b9ea10ce89b0cdcf252b0bb5ab460dfc4eadea003b56e5d2ee",
    "onnx/language_model.onnx": "861a34585605e8ad671051788afc495dcbeaee833a41523a1b33aded9c3babc7",
    "onnx/language_model.onnx_data": "efe9a1173c40d50bc651cb96ebff9f23d6f20d5b3a11b0685510e3a3facdbcf1",
    "onnx/speech_encoder.onnx": "8f1c8a0f89b77bf9cd5dd8f2e034eb2c79dc00fe70d41196b28c257643b00ccb",
    "onnx/speech_encoder.onnx_data": "04431dcef6325c54b02de2219845888b464bcd1f1ac2f8839c2fecd1ed2ef294",
}


def main() -> None:
    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model in catalog["models"]:
        model_id = model["model_id"]

        if model_id == "cgus/diar_streaming_sortformer_4spk-v2.1-onnx":
            sources = model.get("download_file_sources") or SORTFORMER_HASHES
            apply_flip(model, SORTFORMER_HASHES, sources=sources)
            continue

        if model_id == "openai/whisper-medium":
            rev = model["revision"]
            sources = build_sources("tonythethompson/whisper-medium-genai", rev, list(WHISPER_MEDIUM_HASHES))
            apply_flip(model, WHISPER_MEDIUM_HASHES, sources=sources)
            continue

        if model_id == "openai/whisper-large-v3":
            rev = model["revision"]
            sources = build_sources("tonythethompson/whisper-large-v3-genai", rev, list(WHISPER_LARGE_HASHES))
            apply_flip(model, WHISPER_LARGE_HASHES, sources=sources)
            continue

        if model_id == "tonythethompson/qwen3-asr-0.6b-onnx":
            rev = model["revision"]
            sources = build_sources(model_id, rev, list(QWEN3_ASR_06B_HASHES))
            apply_flip(model, QWEN3_ASR_06B_HASHES, sources=sources)
            continue

        if model_id == "tonythethompson/qwen3-asr-1.7b-onnx":
            rev = model["revision"]
            sources = build_sources(model_id, rev, list(QWEN3_ASR_17B_HASHES))
            apply_flip(model, QWEN3_ASR_17B_HASHES, sources=sources)
            continue

        if model_id == "Rikorose/DeepFilterNet3":
            rev = model["revision"]
            sources = build_sources("tonythethompson/deepfilternet3-onnx", rev, list(DEEPFILTERNET_HASHES))
            apply_flip(model, DEEPFILTERNET_HASHES, sources=sources)
            continue

        if model_id == "ResembleAI/chatterbox-turbo-ONNX":
            rev = model["revision"]
            sources = build_sources(model_id, rev, list(CHATTERBOX_TURBO_DEFAULT))
            apply_flip(model, CHATTERBOX_TURBO_DEFAULT, sources=sources)
            continue

        if model_id == "onnx-community/chatterbox-ONNX":
            rev = model["revision"]
            sources = build_sources(model_id, rev, list(CHATTERBOX_ONNX_DEFAULT))
            apply_flip(model, CHATTERBOX_ONNX_DEFAULT, sources=sources)
            continue

        if model_id == "onnx-community/chatterbox-multilingual-ONNX":
            model["commercial_use_verified"] = True
            model["hash_verification"] = {"mode": "required"}
            continue

    with MANIFEST_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, indent=2)
        handle.write("\n")

    print(f"Updated {MANIFEST_PATH}")


if __name__ == "__main__":
    main()
