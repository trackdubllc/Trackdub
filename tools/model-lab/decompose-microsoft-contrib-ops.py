"""Decompose com.microsoft SkipLayerNormalization / BiasGelu into standard ONNX ops.

TensorRT-RTX's ONNX parser is all-or-nothing (every op must be in its standard catalog).
Stock Olive/HF Whisper and similar exports ship fused contrib ops under the ``com.microsoft``
domain, so TRT-RTX rejects the graph node-by-node (``SkipLayerNormalization`` /
``BiasGelu`` ModelImporter errors) and Trackdub falls back to DirectML/CPU after a full
failed session init.

This pass rewrites those fusions to primitive ONNX that TRT-RTX can import:

    SkipLayerNormalization(input, skip, gamma, beta[, bias])
        ->  Add(input, skip) [+ Add bias] -> LayerNormalization(..., gamma, beta)

    BiasGelu(X, bias)
        ->  Add(X, bias) -> Gelu (Erf form, opset-safe)

MultiHeadAttention is intentionally NOT rewritten here; use
``decompose-whisper-cross-attention.py`` for decoder cross-attention MHA (and re-export or
extend that script for self-attention). Run this pass first so residual MHA is the only
remaining contrib surface.

Usage:
    python decompose-microsoft-contrib-ops.py path/to/model.onnx [--dry-run]
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import numpy as np
import onnx
from onnx import helper, numpy_helper


CONTRIB_DOMAIN = "com.microsoft"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Replace com.microsoft SkipLayerNormalization/BiasGelu with standard ONNX ops."
    )
    parser.add_argument("model", help="Path to a .onnx file (rewritten in place unless --dry-run)")
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report what would change without writing the model",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    model_path = Path(args.model).resolve()
    if not model_path.is_file():
        print(f"model not found: {model_path}", file=sys.stderr)
        return 2

    model = onnx.load(str(model_path), load_external_data=True)

    graph = model.graph
    existing_names = {initializer.name for initializer in graph.initializer}

    def unique(name: str) -> str:
        candidate = name
        suffix = 0
        while candidate in existing_names:
            suffix += 1
            candidate = f"{name}_{suffix}"
        existing_names.add(candidate)
        return candidate

    def add_initializer(name: str, array: np.ndarray) -> str:
        key = unique(name)
        graph.initializer.append(numpy_helper.from_array(array, key))
        return key

    def _erf_gelu_nodes(main_input: str, output: str, prefix: str) -> list:
        # Gelu(x) = 0.5 * x * (1 + Erf(x / sqrt(2)))
        half = add_initializer(f"{prefix}/half", np.array(0.5, dtype=np.float32))
        one = add_initializer(f"{prefix}/one", np.array(1.0, dtype=np.float32))
        inv_sqrt2 = add_initializer(f"{prefix}/inv_sqrt2", np.array(1.0 / np.sqrt(2.0), dtype=np.float32))
        scaled = f"{prefix}/scaled"
        erf_out = f"{prefix}/erf"
        plus_one = f"{prefix}/plus_one"
        half_x = f"{prefix}/half_x"
        return [
            helper.make_node("Mul", [main_input, inv_sqrt2], [scaled], name=f"{prefix}/ScaleSqrt2"),
            helper.make_node("Erf", [scaled], [erf_out], name=f"{prefix}/Erf"),
            helper.make_node("Add", [erf_out, one], [plus_one], name=f"{prefix}/AddOne"),
            helper.make_node("Mul", [main_input, half], [half_x], name=f"{prefix}/HalfX"),
            helper.make_node("Mul", [half_x, plus_one], [output], name=f"{prefix}/Mul"),
        ]

    new_nodes = []
    replaced_skiplayernorm = 0
    replaced_biasgelu = 0

    for node in graph.node:
        is_contrib = node.domain == CONTRIB_DOMAIN

        if is_contrib and node.op_type == "SkipLayerNormalization" and len(node.input) >= 3:
            # input, skip, gamma, beta[, bias[, mask_index]]
            # Outputs: Y (LayerNorm), optional mean, optional inv_std_var, optional residual
            # (input+skip+bias) used as the next block's skip — qwen3-asr encoders use that
            # 4th output (e.g. add_208). Mean/inv_std_var are training-only and may be empty.
            inputs = list(node.input)
            main_input, skip, gamma, beta = inputs[:4]
            bias = inputs[4] if len(inputs) >= 5 and inputs[4] else None
            outputs = list(node.output)
            y_out = outputs[0] if outputs else None
            attrs = {attr.name: helper.get_attribute_value(attr) for attr in node.attribute}
            epsilon = float(attrs.get("epsilon", 1e-12))
            axis = int(attrs.get("axis", -1))

            prefix = f"/trackdub/decomposed_skip_ln_{replaced_skiplayernorm}"
            residual = f"{prefix}/residual"
            if bias:
                residual_with_bias = f"{prefix}/residual_bias"
                new_nodes.append(
                    helper.make_node("Add", [main_input, skip], [residual], name=f"{prefix}/AddSkip")
                )
                new_nodes.append(
                    helper.make_node("Add", [residual, bias], [residual_with_bias], name=f"{prefix}/AddBias")
                )
                ln_input = residual_with_bias
            else:
                new_nodes.append(
                    helper.make_node("Add", [main_input, skip], [residual], name=f"{prefix}/AddSkip")
                )
                ln_input = residual

            if y_out:
                # Extra SkipLayerNorm outputs (mean / inv_std_var) are training-only; residual
                # outputs must keep the pre-LN sum for the next block.
                new_nodes.append(
                    helper.make_node(
                        "LayerNormalization",
                        [ln_input, gamma, beta],
                        [y_out],
                        name=f"{prefix}/LayerNorm",
                        axis=axis,
                        epsilon=epsilon,
                    )
                )
            for extra_index, extra_out in enumerate(outputs[1:], start=1):
                if not extra_out:
                    continue
                # Indices 1–2: mean / inv_std_var (unused in inference exports). Index 3+:
                # pre-LN residual (input+skip+bias).
                new_nodes.append(
                    helper.make_node(
                        "Identity",
                        [ln_input],
                        [extra_out],
                        name=f"{prefix}/ExtraOut{extra_index}",
                    )
                )
            replaced_skiplayernorm += 1
            continue

        if is_contrib and node.op_type == "BiasGelu" and len(node.input) >= 2:
            main_input, bias = node.input[:2]
            output = node.output[0]
            prefix = f"/trackdub/decomposed_bias_gelu_{replaced_biasgelu}"
            summed = f"{prefix}/summed"
            new_nodes.append(
                helper.make_node("Add", [main_input, bias], [summed], name=f"{prefix}/AddBias")
            )
            new_nodes.extend(_erf_gelu_nodes(summed, output, prefix))
            replaced_biasgelu += 1
            continue

        if is_contrib and node.op_type == "Gelu" and len(node.input) >= 1:
            # com.microsoft::Gelu (optionally approximate=1 tanh). Erf form is the default
            # exact Gelu and matches BiasGelu without the bias add.
            main_input = node.input[0]
            output = node.output[0]
            prefix = f"/trackdub/decomposed_contrib_gelu_{replaced_biasgelu}"
            new_nodes.extend(_erf_gelu_nodes(main_input, output, prefix))
            replaced_biasgelu += 1
            continue

        new_nodes.append(node)

    residual_contrib = sorted(
        {
            node.op_type
            for node in new_nodes
            if node.domain == CONTRIB_DOMAIN
        }
    )
    print(f"decomposed_skip_layer_normalization={replaced_skiplayernorm}")
    print(f"decomposed_bias_gelu_or_contrib_gelu={replaced_biasgelu}")
    print(f"remaining_com_microsoft_ops={','.join(residual_contrib) if residual_contrib else ''}")

    if args.dry_run:
        print("dry_run=1 (model not written)")
        return 0

    if replaced_skiplayernorm == 0 and replaced_biasgelu == 0:
        print("noop=1 (no SkipLayerNormalization/BiasGelu/Gelu found)")
        return 0

    def topo_sort(nodes):
        produced = {init.name for init in graph.initializer}
        produced |= {v.name for v in graph.input}
        produced |= {n for n in (None,) if n}
        remaining = list(nodes)
        ordered = []
        while remaining:
            progressed = False
            for index, node in enumerate(remaining):
                required = [i for i in node.input if i]
                if all(name in produced for name in required):
                    ordered.append(node)
                    produced.update(o for o in node.output if o)
                    remaining.pop(index)
                    progressed = True
                    break
            if not progressed:
                # Missing producer or cycle: keep original relative order for the rest.
                ordered.extend(remaining)
                break
        return ordered

    graph.ClearField("node")
    graph.node.extend(topo_sort(new_nodes))
    onnx.checker.check_model(model)

    external_data_path = model_path.with_suffix(model_path.suffix + ".data")
    # Prefer the sibling external-data name used by decompose-whisper-cross-attention.py.
    legacy_external = model_path.parent / f"{model_path.name}.data"
    target_external = legacy_external if legacy_external.exists() else (
        external_data_path if external_data_path.exists() else None
    )
    if target_external is not None and target_external.exists():
        os.remove(target_external)
        onnx.save_model(
            model,
            str(model_path),
            save_as_external_data=True,
            all_tensors_to_one_file=True,
            location=target_external.name,
            size_threshold=1024,
        )
    else:
        onnx.save_model(model, str(model_path))

    print(f"wrote={model_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
