"""Decompose com.microsoft MultiHeadAttention (Q/K/V form) into standard ONNX.

Handles the chatterbox / speech-encoder pattern used across ResembleAI/chatterbox-turbo-ONNX:

    MultiHeadAttention(Q, K, V, '', '', [mask]) -> Y
    attrs: num_heads, scale

Q/K/V are already projected ([B, S, H] or [B, S, N, D] after Reshape). Input 5 is an
optional attention mask (Where/Expand add-mask). No KV-cache / present outputs.

``decompose-whisper-mha.py`` covers Whisper's fused-QKV ``Attention`` and cache MHA.
``decompose-microsoft-contrib-ops.py`` covers SkipLayerNormalization / BiasGelu / Gelu.

Usage:
    python decompose-mha-qkv.py path/to/model.onnx [--dry-run]
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper, shape_inference


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Decompose Q/K/V MultiHeadAttention nodes.")
    p.add_argument("model")
    p.add_argument("--dry-run", action="store_true")
    return p.parse_args()


def main() -> int:
    args = parse_args()
    path = Path(args.model).resolve()
    if not path.is_file():
        print(f"model not found: {path}", file=sys.stderr)
        return 2

    model = onnx.load(str(path), load_external_data=True)
    try:
        inferred = shape_inference.infer_shapes(model)
    except Exception:
        inferred = model

    infos = {
        v.name: v
        for v in [*inferred.graph.value_info, *inferred.graph.input, *inferred.graph.output]
    }

    def elem_type(name: str) -> int:
        vi = infos.get(name)
        if vi is not None and vi.type.HasField("tensor_type"):
            return vi.type.tensor_type.elem_type
        return TensorProto.FLOAT

    graph = model.graph
    existing = {init.name for init in graph.initializer}

    def unique(name: str) -> str:
        candidate, suffix = name, 0
        while candidate in existing:
            suffix += 1
            candidate = f"{name}_{suffix}"
        existing.add(candidate)
        return candidate

    def add_init(name: str, array: np.ndarray) -> str:
        key = unique(name)
        graph.initializer.append(numpy_helper.from_array(array, key))
        return key

    new_nodes = []
    replaced = 0

    for node in graph.node:
        is_mha = (
            node.domain == "com.microsoft"
            and node.op_type == "MultiHeadAttention"
            and len(node.input) >= 3
            and node.input[0] and node.input[1] and node.input[2]
            and node.output and node.output[0]
        )
        # Skip cache form (present outputs / past inputs) — use decompose-whisper-mha.py.
        has_cache = (len(node.output) > 1 and any(node.output[1:])) or (
            len(node.input) > 6 and any(node.input[6:])
        )
        if not is_mha or has_cache:
            new_nodes.append(node)
            continue

        attrs = {a.name: helper.get_attribute_value(a) for a in node.attribute}
        num_heads = int(attrs.get("num_heads", 8))
        scale = float(attrs.get("scale", 0.125))
        head_size = int(round((1.0 / scale) ** 2)) if scale > 0 else 64
        hidden = num_heads * head_size
        query, key, value = node.input[0], node.input[1], node.input[2]
        mask = node.input[5] if len(node.input) > 5 and node.input[5] else None
        output = node.output[0]
        dtype16 = elem_type(query) == TensorProto.FLOAT16
        scale_np = np.float16(scale) if dtype16 else np.float32(scale)

        prefix = f"/trackdub/decomposed_qkv_mha_{replaced}"
        q_shape = add_init(f"{prefix}/q_shape", np.array([0, 0, num_heads, head_size], dtype=np.int64))
        k_shape = add_init(f"{prefix}/k_shape", np.array([0, 0, num_heads, head_size], dtype=np.int64))
        v_shape = add_init(f"{prefix}/v_shape", np.array([0, 0, num_heads, head_size], dtype=np.int64))
        out_shape = add_init(f"{prefix}/out_shape", np.array([0, 0, hidden], dtype=np.int64))
        scale_i = add_init(f"{prefix}/scale", scale_np)

        q_r, q_t = f"{prefix}/q_r", f"{prefix}/q_t"
        k_r, k_t = f"{prefix}/k_r", f"{prefix}/k_t"
        v_r, v_t = f"{prefix}/v_r", f"{prefix}/v_t"
        k_tt = f"{prefix}/k_tt"
        scores = f"{prefix}/scores"
        scaled = f"{prefix}/scaled"

        new_nodes += [
            helper.make_node("Reshape", [query, q_shape], [q_r], name=f"{prefix}/ReshapeQ"),
            helper.make_node("Transpose", [q_r], [q_t], name=f"{prefix}/TransposeQ", perm=[0, 2, 1, 3]),
            helper.make_node("Reshape", [key, k_shape], [k_r], name=f"{prefix}/ReshapeK"),
            helper.make_node("Transpose", [k_r], [k_t], name=f"{prefix}/TransposeK", perm=[0, 2, 1, 3]),
            helper.make_node("Reshape", [value, v_shape], [v_r], name=f"{prefix}/ReshapeV"),
            helper.make_node("Transpose", [v_r], [v_t], name=f"{prefix}/TransposeV", perm=[0, 2, 1, 3]),
            helper.make_node("Transpose", [k_t], [k_tt], name=f"{prefix}/TransposeKT", perm=[0, 1, 3, 2]),
            helper.make_node("MatMul", [q_t, k_tt], [scores], name=f"{prefix}/Scores"),
            helper.make_node("Mul", [scores, scale_i], [scaled], name=f"{prefix}/Scale"),
        ]

        probs_in = scaled
        if mask:
            # ORT MHA mask input is already an additive float mask (Where/Expand).
            masked = f"{prefix}/masked"
            new_nodes.append(helper.make_node("Add", [scaled, mask], [masked], name=f"{prefix}/AddMask"))
            probs_in = masked

        probs = f"{prefix}/probs"
        context = f"{prefix}/context"
        ctx_t = f"{prefix}/ctx_t"
        new_nodes += [
            helper.make_node("Softmax", [probs_in], [probs], name=f"{prefix}/Softmax", axis=-1),
            helper.make_node("MatMul", [probs, v_t], [context], name=f"{prefix}/Context"),
            helper.make_node("Transpose", [context], [ctx_t], name=f"{prefix}/TransposeCtx", perm=[0, 2, 1, 3]),
            helper.make_node("Reshape", [ctx_t, out_shape], [output], name=f"{prefix}/ReshapeOut"),
        ]
        replaced += 1

    remaining = sorted({n.op_type for n in new_nodes if n.domain == "com.microsoft"})
    print(f"decomposed_qkv_mha={replaced}")
    print(f"remaining_com_microsoft_ops={','.join(remaining) if remaining else ''}")

    if args.dry_run:
        print("dry_run=1 (model not written)")
        return 0
    if replaced == 0:
        print("noop=1")
        return 0

    def topo_sort(nodes):
        produced = {init.name for init in graph.initializer}
        produced |= {v.name for v in graph.input}
        remaining = list(nodes)
        ordered = []
        while remaining:
            progressed = False
            for index, node in enumerate(remaining):
                if all((not i) or (i in produced) for i in node.input):
                    ordered.append(node)
                    produced.update(o for o in node.output if o)
                    remaining.pop(index)
                    progressed = True
                    break
            if not progressed:
                ordered.extend(remaining)
                break
        return ordered

    graph.ClearField("node")
    graph.node.extend(topo_sort(new_nodes))
    onnx.checker.check_model(model)

    legacy_external = path.parent / f"{path.name}.data"
    if legacy_external.exists():
        os.remove(legacy_external)
        onnx.save_model(
            model,
            str(path),
            save_as_external_data=True,
            all_tensors_to_one_file=True,
            location=legacy_external.name,
            size_threshold=1024,
        )
    else:
        onnx.save_model(model, str(path))
    print(f"wrote={path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
