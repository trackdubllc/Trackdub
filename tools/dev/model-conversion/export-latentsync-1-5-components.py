"""Export the LatentSync 1.5 VAE and Whisper encoder to CPU ONNX graphs.

This is a local conversion probe. The produced components are not a shippable
bundle until the runtime, license, and end-to-end quality gates pass.
"""

import argparse
import hashlib
import importlib.metadata
import importlib.util
import json
import platform
import shutil
import sys
import tempfile
import types
from pathlib import Path


def export_component(model, args, output_name, component_name, inputs, input_names, output_names):
    import onnx
    import torch

    with tempfile.TemporaryDirectory(prefix=f"{component_name}-") as temporary:
        temporary_path = Path(temporary)
        temporary_model = temporary_path / output_name
        with torch.inference_mode():
            torch.onnx.export(
                model,
                inputs,
                str(temporary_model),
                input_names=input_names,
                output_names=output_names,
                opset_version=17,
                dynamo=False,
                do_constant_folding=False,
                external_data=True,
            )

        graph = onnx.load(str(temporary_model), load_external_data=False)
        external_locations = {
            item.value
            for initializer in graph.graph.initializer
            for item in initializer.external_data
            if item.key == "location"
        }
        for location in sorted(external_locations):
            original = temporary_path / location
            if not original.is_file():
                raise FileNotFoundError(f"Missing external data for {component_name}: {original}")
            target_name = f"{component_name}__{Path(location).name}"
            target = args.output / target_name
            shutil.copyfile(original, target)
            for initializer in graph.graph.initializer:
                for item in initializer.external_data:
                    if item.key == "location" and item.value == location:
                        item.value = target_name

        destination = args.output / output_name
        onnx.save(graph, str(destination))
        onnx.checker.check_model(str(destination))
        return destination


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def tensor_shape(value):
    return [
        dimension.dim_value or dimension.dim_param
        for dimension in value.type.tensor_type.shape.dim
    ]


def package_version(name):
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=Path, required=True)
    parser.add_argument("--vae-root", type=Path, required=True)
    parser.add_argument("--whisper-checkpoint", type=Path, required=True)
    parser.add_argument("--mask-image", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--upstream-revision", required=True)
    parser.add_argument("--weights-revision", required=True)
    parser.add_argument("--vae-revision", required=True)
    args = parser.parse_args()

    import torch
    from diffusers import AutoencoderKL
    from omegaconf import OmegaConf

    args.output.mkdir(parents=True, exist_ok=True)

    vae = AutoencoderKL.from_pretrained(
        str(args.vae_root), local_files_only=True, use_safetensors=True
    ).eval()
    vae.config.scaling_factor = 0.18215
    vae.config.shift_factor = 0.0

    class VaeEncoder(torch.nn.Module):
        def __init__(self, model):
            super().__init__()
            self.model = model

        def forward(self, sample):
            distribution = self.model.encode(sample).latent_dist
            return distribution.mean, distribution.logvar

    class VaeDecoder(torch.nn.Module):
        def __init__(self, model):
            super().__init__()
            self.model = model

        def forward(self, latent_sample):
            decoded = self.model.decode(latent_sample / 0.18215).sample
            return decoded

    image = torch.zeros(1, 3, 256, 256)
    encoder_path = export_component(
        VaeEncoder(vae), args, "vae_encoder.onnx", "vae_encoder",
        (image,), ["sample"], ["latent_mean", "latent_logvar"],
    )
    latent = torch.zeros(1, 4, 32, 32)
    decoder_path = export_component(
        VaeDecoder(vae), args, "vae_decoder.onnx", "vae_decoder",
        (latent,), ["latent_sample"], ["sample"],
    )
    del vae

    # Import only the upstream Whisper model definition. Its package __init__
    # pulls in ffmpeg and media helpers that are unrelated to encoder export.
    package_root = args.upstream / "latentsync"
    for name, package_path in (
        ("latentsync", package_root),
        ("latentsync.whisper", package_root / "whisper"),
        ("latentsync.whisper.whisper", package_root / "whisper" / "whisper"),
    ):
        package = types.ModuleType(name)
        package.__path__ = [str(package_path)]
        sys.modules[name] = package
    transcribe = types.ModuleType("latentsync.whisper.whisper.transcribe")
    transcribe.transcribe = lambda *_args, **_kwargs: None
    decoding = types.ModuleType("latentsync.whisper.whisper.decoding")
    decoding.detect_language = lambda *_args, **_kwargs: None
    decoding.decode = lambda *_args, **_kwargs: None
    sys.modules[transcribe.__name__] = transcribe
    sys.modules[decoding.__name__] = decoding
    model_path = package_root / "whisper" / "whisper" / "model.py"
    specification = importlib.util.spec_from_file_location(
        "latentsync.whisper.whisper.model", model_path
    )
    if specification is None or specification.loader is None:
        raise RuntimeError(f"Could not load upstream Whisper model code: {model_path}")
    upstream_model = importlib.util.module_from_spec(specification)
    sys.modules[specification.name] = upstream_model
    specification.loader.exec_module(upstream_model)
    checkpoint = torch.load(args.whisper_checkpoint, map_location="cpu", weights_only=True)
    dimensions = upstream_model.ModelDimensions(**checkpoint["dims"])
    whisper = upstream_model.Whisper(dimensions)
    whisper.load_state_dict(checkpoint["model_state_dict"])
    whisper.eval()
    del checkpoint

    class WhisperAudioEncoder(torch.nn.Module):
        def __init__(self, model):
            super().__init__()
            self.encoder = model.encoder

        def forward(self, input_features):
            return self.encoder(input_features)

    mel = torch.zeros(1, 80, 3000)
    whisper_path = export_component(
        WhisperAudioEncoder(whisper), args, "whisper_encoder.onnx", "whisper_encoder",
        (mel,), ["input_features"], ["last_hidden_state"],
    )

    import onnx
    upstream_config_path = args.upstream / "configs" / "unet" / "stage2_efficient.yaml"
    upstream_scheduler_path = args.upstream / "configs" / "scheduler_config.json"
    upstream_config = OmegaConf.load(upstream_config_path)
    scheduler_config = json.loads(upstream_scheduler_path.read_text(encoding="utf-8"))
    model_config = {
        "schema_version": 1,
        "model_id": "ByteDance/LatentSync-1.5",
        "face_resolution": int(upstream_config.data.resolution),
        "video_frame_rate": int(upstream_config.data.video_fps),
        "frames_per_batch": int(upstream_config.data.num_frames),
        "latent_channels": int(upstream_config.model.out_channels),
        "latent_resolution": int(upstream_config.data.resolution) // 8,
        "whisper_mel_bins": 80,
        "whisper_mel_frames": 3000,
        "whisper_feature_rate_hz": 50,
        "audio_context_frames_before": int(upstream_config.data.audio_feat_length[0]),
        "audio_context_frames_after": int(upstream_config.data.audio_feat_length[1]),
        "audio_sample_rate_hz": int(upstream_config.data.audio_sample_rate),
        "vae_scaling_factor": 0.18215,
        "num_inference_steps": int(upstream_config.run.inference_steps),
        "guidance_scale": float(upstream_config.run.guidance_scale),
        "scheduler": scheduler_config,
    }
    config_path = args.output / "model-config.json"
    config_path.write_text(json.dumps(model_config, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    mask_path = args.output / "mask.png"
    shutil.copyfile(args.mask_image, mask_path)

    report = {}
    for path in (encoder_path, decoder_path, whisper_path):
        graph = onnx.load(str(path), load_external_data=False)
        external_locations = sorted({
            item.value
            for initializer in graph.graph.initializer
            for item in initializer.external_data
            if item.key == "location"
        })
        report[path.name] = {
            "sha256": sha256(path),
            "inputs": [
                {
                    "name": tensor.name,
                    "shape": tensor_shape(tensor),
                }
                for tensor in graph.graph.input
            ],
            "outputs": [
                {
                    "name": tensor.name,
                    "shape": tensor_shape(tensor),
                }
                for tensor in graph.graph.output
            ],
            "operators": sorted({
                f"{node.domain or 'ai.onnx'}::{node.op_type}"
                for node in graph.graph.node
            }),
            "external_data": {
                location: sha256(args.output / location)
                for location in external_locations
            },
        }

    source_files = {
        "vae/config.json": args.vae_root / "config.json",
        "vae/diffusion_pytorch_model.safetensors": (
            args.vae_root / "diffusion_pytorch_model.safetensors"
        ),
        "whisper/tiny.pt": args.whisper_checkpoint,
        "whisper/model.py": package_root / "whisper" / "whisper" / "model.py",
        "upstream/configs/unet/stage2_efficient.yaml": upstream_config_path,
        "upstream/configs/scheduler_config.json": upstream_scheduler_path,
        "upstream/latentsync/utils/mask.png": args.mask_image,
    }
    manifest = {
        "sources": {
            "latentsync_code": {
                "repository": "https://github.com/bytedance/LatentSync",
                "revision": args.upstream_revision,
            },
            "latentsync_weights": {
                "repository": "https://huggingface.co/ByteDance/LatentSync-1.5",
                "revision": args.weights_revision,
            },
            "vae_weights": {
                "repository": "https://huggingface.co/stabilityai/sd-vae-ft-mse",
                "revision": args.vae_revision,
            },
            "files": {
                name: sha256(path)
                for name, path in source_files.items()
            },
        },
        "export_environment": {
            "python": platform.python_version(),
            "torch": package_version("torch"),
            "diffusers": package_version("diffusers"),
            "onnx": package_version("onnx"),
            "onnxruntime": package_version("onnxruntime") or package_version("onnxruntime-directml"),
        },
        "bundle_files": [
            "unet.onnx",
            "vae_encoder.onnx",
            "vae_decoder.onnx",
            "whisper_encoder.onnx",
            "mask.png",
            "model-config.json",
            "all external-data files referenced by unet.onnx",
        ],
        "model_config_sha256": sha256(config_path),
        "mask_sha256": sha256(mask_path),
        "components": report,
        "bundle_status": "local_probe_not_audited_or_publishable",
    }
    report_path = args.output / "components.report.json"
    report_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2), flush=True)


if __name__ == "__main__":
    main()
