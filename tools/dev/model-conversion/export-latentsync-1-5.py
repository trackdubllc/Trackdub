"""Developer-only LatentSync 1.5 U-Net ONNX export feasibility probe.

Requires a checkout of ByteDance/LatentSync and its Python dependencies. The
result is intentionally not a downloadable Trackdub model bundle until its
graph, external data, runtime, output quality, and license have been verified.
"""

import argparse
import hashlib
import json
import sys
import types
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=1)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--upstream-revision", required=True)
    parser.add_argument("--weights-revision", required=True)
    args = parser.parse_args()
    if args.frames < 1 or args.frames > 16:
        parser.error("--frames must be between 1 and 16")
    if args.batch_size not in (1, 2):
        parser.error("--batch-size must be 1 or 2 (two is required for guidance_scale > 1)")

    import torch
    from omegaconf import OmegaConf

    # The model class imports zero_rank_log from a video utility module whose
    # optional video/image dependencies are unnecessary for this export.
    utility = types.ModuleType("latentsync.utils.util")
    utility.zero_rank_log = lambda *_: None
    sys.modules["latentsync.utils.util"] = utility
    sys.path.insert(0, str(args.upstream))
    from latentsync.models.unet import UNet3DConditionModel

    config = OmegaConf.load(args.upstream / "configs/unet/stage2_efficient.yaml")
    model = UNet3DConditionModel.from_config(
        OmegaConf.to_container(config.model, resolve=True)
    )
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    missing, unexpected = model.load_state_dict(checkpoint["state_dict"], strict=False)
    if missing or unexpected:
        raise RuntimeError(
            f"Checkpoint mismatch: {len(missing)} missing, {len(unexpected)} unexpected; "
            f"first missing={missing[:3]}, first unexpected={unexpected[:3]}"
        )
    del checkpoint
    model.eval()

    class SampleOutput(torch.nn.Module):
        def __init__(self, unet):
            super().__init__()
            self.unet = unet

        def forward(self, sample, timestep, encoder_hidden_states):
            return self.unet(
                sample, timestep, encoder_hidden_states=encoder_hidden_states,
                return_dict=False
            )[0]

    args.output.parent.mkdir(parents=True, exist_ok=True)
    print(f"Exporting {args.frames} frame(s) to {args.output}", flush=True)
    with torch.inference_mode():
        torch.onnx.export(
            SampleOutput(model),
            (
                torch.zeros(args.batch_size, 13, args.frames, 32, 32),
                torch.ones(args.batch_size, dtype=torch.int64),
                torch.zeros(args.batch_size * args.frames, 10, 384),
            ),
            str(args.output),
            input_names=["sample", "timestep", "encoder_hidden_states"],
            output_names=["out_sample"],
            opset_version=17,
            dynamo=False,
            do_constant_folding=False,
            external_data=True,
        )

    import onnx
    graph = onnx.load(str(args.output), load_external_data=False)
    onnx.checker.check_model(str(args.output))
    tensor_shape = lambda value: [
        dimension.dim_value or dimension.dim_param
        for dimension in value.type.tensor_type.shape.dim
    ]
    input_report = [
        {"name": value.name, "shape": tensor_shape(value)}
        for value in graph.graph.input
    ]
    output_report = [
        {"name": value.name, "shape": tensor_shape(value)}
        for value in graph.graph.output
    ]
    operators = sorted({
        f"{node.domain or 'ai.onnx'}::{node.op_type}"
        for node in graph.graph.node
    })
    print("Inputs:", [(value["name"], value["shape"]) for value in input_report], flush=True)
    print("Operators:", operators, flush=True)
    external = {
        item.value
        for initializer in graph.graph.initializer
        for item in initializer.external_data
        if item.key == "location"
    }
    hashes = {}
    for location in sorted(external):
        path = args.output.parent / location
        if not path.is_file():
            raise FileNotFoundError(f"Missing external data: {path}")
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
                digest.update(block)
        hashes[location] = digest.hexdigest()
    (args.output.parent / "external-data.sha256.json").write_text(
        json.dumps(hashes, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    report = {
        "sources": {
            "latentsync_code": {
                "repository": "https://github.com/bytedance/LatentSync",
                "revision": args.upstream_revision,
            },
            "latentsync_weights": {
                "repository": "https://huggingface.co/ByteDance/LatentSync-1.5",
                "revision": args.weights_revision,
                "checkpoint_sha256": sha256_file(args.checkpoint),
            },
        },
        "graph": {
            "sha256": sha256_file(args.output),
            "opset_imports": [
                {"domain": item.domain or "ai.onnx", "version": item.version}
                for item in graph.opset_import
            ],
            "inputs": input_report,
            "outputs": output_report,
            "operators": operators,
            "external_data_file_count": len(external),
            "external_data_manifest": "external-data.sha256.json",
        },
        "export_parameters": {
            "frames": args.frames,
            "batch_size": args.batch_size,
            "guidance_branches": args.batch_size,
        },
        "bundle_status": "local_probe_not_audited_or_publishable",
    }
    (args.output.parent / "unet-probe.report.json").write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    print(f"External data files: {len(hashes)}; hashes saved next to graph.", flush=True)
    print("Graph hash, operator contract, and source revisions saved to unet-probe.report.json.", flush=True)
    print("Graph probe complete; runtime and visual acceptance still required.", flush=True)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


if __name__ == "__main__":
    main()
