"""Decompose Whisper-onnx com.microsoft Attention / MultiHeadAttention into standard ONNX.

openai/whisper-* exports (and similar) use three contrib patterns that TensorRT-RTX
cannot import:

1. Encoder ``Attention`` — fused QKV MatMul+Add, multi-head self-attention
   (``qkv_hidden_sizes``, ``num_heads``, ``scale``, ``unidirectional=0``).
2. Decoder self ``MultiHeadAttention`` — Q/K/V already projected, causal, with
   past/present KV cache (inputs 6/7, outputs 1/2).
3. Decoder cross ``MultiHeadAttention`` — Q projected; K/V are the encoder cache
   (``past_key_cross_*`` / ``past_value_cross_*``); no present outputs.

``SkipLayerNormalization`` is handled by ``decompose-microsoft-contrib-ops.py``.
Run that first or after; this script leaves SLN nodes untouched.

Usage:
    python decompose-whisper-mha.py encoder.onnx
    python decompose-whisper-mha.py decoder.onnx
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
    p = argparse.ArgumentParser(description="Decompose Whisper com.microsoft Attention/MHA nodes.")
    p.add_argument("model", help="Path to encoder.onnx or decoder.onnx")
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

    def infer_head_size(node) -> tuple[int, int, float]:
        attrs = {a.name: helper.get_attribute_value(a) for a in node.attribute}
        num_heads = int(attrs.get("num_heads", 12))
        scale = float(attrs.get("scale", 0.125))
        qkv = attrs.get("qkv_hidden_sizes")
        if qkv is not None and len(qkv) >= 1 and int(qkv[0]) > 0 and num_heads > 0:
            head_size = int(qkv[0]) // num_heads
        else:
            head_size = int(round((1.0 / scale) ** 2)) if scale > 0 else 64
        return num_heads, head_size, scale

    def make_attention_body(
        query: str,
        key: str,
        value: str,
        output: str,
        prefix: str,
        num_heads: int,
        head_size: int,
        scale: float,
        causal: bool,
        past_seq_init_name: str | None,
        present_key: str | None,
        present_value: str | None,
    ) -> list:
        """Shared multi-head attention body.

        ``key``/``value`` are already full-sequence K/V (after any Concat with past).
        ``causal`` adds a lower-triangular mask assuming query index i maps to
        absolute position ``past_seq + i`` (a scalar/past-length initializer is optional).
        """
        nodes: list = []
        hidden = num_heads * head_size
        dtype_ok = elem_type(query) == TensorProto.FLOAT16
        scale_np = np.float16(scale) if dtype_ok else np.float32(scale)

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

        nodes += [
            helper.make_node("Reshape", [query, q_shape], [q_r], name=f"{prefix}/ReshapeQ"),
            helper.make_node("Transpose", [q_r], [q_t], name=f"{prefix}/TransposeQ", perm=[0, 2, 1, 3]),
            helper.make_node("Reshape", [key, k_shape], [k_r], name=f"{prefix}/ReshapeK"),
            helper.make_node("Transpose", [k_r], [k_t], name=f"{prefix}/TransposeK", perm=[0, 2, 1, 3]),
            helper.make_node("Reshape", [value, v_shape], [v_r], name=f"{prefix}/ReshapeV"),
            helper.make_node("Transpose", [v_r], [v_t], name=f"{prefix}/TransposeV", perm=[0, 2, 1, 3]),
        ]

        if present_key:
            nodes.append(helper.make_node("Identity", [k_t], [present_key], name=f"{prefix}/PresentK"))
        if present_value:
            nodes.append(helper.make_node("Identity", [v_t], [present_value], name=f"{prefix}/PresentV"))

        nodes += [
            helper.make_node("Transpose", [k_t], [k_tt], name=f"{prefix}/TransposeKT", perm=[0, 1, 3, 2]),
            helper.make_node("MatMul", [q_t, k_tt], [scores], name=f"{prefix}/Scores"),
            helper.make_node("Mul", [scores, scale_i], [scaled], name=f"{prefix}/Scale"),
        ]

        probs_in = scaled
        if causal:
            # mask[i, j] = 0 if j <= past + i else mask_filter_value
            # Build via Shape arithmetic on K so dynamic seq lengths work.
            k_shape_op = f"{prefix}/k_total"
            q_shape_op = f"{prefix}/q_len"
            past_len = f"{prefix}/past_len"
            k_range = f"{prefix}/k_range"
            q_idx = f"{prefix}/q_idx"
            q_pos = f"{prefix}/q_pos"
            mask = f"{prefix}/mask"
            neg_inf = add_init(
                f"{prefix}/mask_fill",
                np.array(-10000.0, dtype=np.float16 if dtype_ok else np.float32),
            )
            one_i = add_init(f"{prefix}/one_i64", np.array(1, dtype=np.int64))
            two_i = add_init(f"{prefix}/two_i64", np.array(2, dtype=np.int64))
            zero_f = add_init(f"{prefix}/zero_f", np.array(0.0, dtype=np.float16 if dtype_ok else np.float32))

            nodes += [
                helper.make_node("Shape", [k_t], [k_shape_op], name=f"{prefix}/ShapeK"),
                helper.make_node("Shape", [q_t], [q_shape_op], name=f"{prefix}/ShapeQ"),
                # k_total = K seq dim (axis 2 of [B,H,S,D])
                helper.make_node("Gather", [k_shape_op, two_i], [f"{prefix}/k_seq"], name=f"{prefix}/GatherKSeq", axis=0),
                # q_len = Q seq dim
                helper.make_node("Gather", [q_shape_op, two_i], [f"{prefix}/q_seq"], name=f"{prefix}/GatherQSeq", axis=0),
                helper.make_node(
                    "Sub", [f"{prefix}/k_seq", f"{prefix}/q_seq"], [past_len], name=f"{prefix}/PastLen"
                ),
                helper.make_node("Range", [add_init(f"{prefix}/zero_i", np.array(0, dtype=np.int64)),
                                          f"{prefix}/k_seq", one_i], [k_range], name=f"{prefix}/RangeK"),
                helper.make_node("Range", [add_init(f"{prefix}/zero_i2", np.array(0, dtype=np.int64)),
                                          f"{prefix}/q_seq", one_i], [q_idx], name=f"{prefix}/RangeQ"),
                helper.make_node("Add", [q_idx, past_len], [f"{prefix}/q_idx_off"], name=f"{prefix}/AddQOff"),
                helper.make_node("Unsqueeze", [f"{prefix}/q_idx_off", one_i], [q_pos], name=f"{prefix}/UnsqQ"),
                helper.make_node("Unsqueeze", [k_range, one_i], [f"{prefix}/k_pos"], name=f"{prefix}/UnsqK"),
                helper.make_node("Greater", [f"{prefix}/k_pos", q_pos], [mask], name=f"{prefix}/CausalMask"),
                helper.make_node("Where", [mask, neg_inf, zero_f], [f"{prefix}/mask_add"], name=f"{prefix}/WhereMask"),
                helper.make_node("Add", [scaled, f"{prefix}/mask_add"], [f"{prefix}/masked"], name=f"{prefix}/AddMask"),
            ]
            probs_in = f"{prefix}/masked"

        probs = f"{prefix}/probs"
        context = f"{prefix}/context"
        ctx_t = f"{prefix}/ctx_t"
        nodes += [
            helper.make_node("Softmax", [probs_in], [probs], name=f"{prefix}/Softmax", axis=-1),
            helper.make_node("MatMul", [probs, v_t], [context], name=f"{prefix}/Context"),
            helper.make_node("Transpose", [context], [ctx_t], name=f"{prefix}/TransposeCtx", perm=[0, 2, 1, 3]),
            helper.make_node("Reshape", [ctx_t, out_shape], [output], name=f"{prefix}/ReshapeOut"),
        ]
        return nodes

    new_nodes = []
    replaced_attention = 0
    replaced_mha = 0

    for node in graph.node:
        is_ms_attn = node.domain == "com.microsoft" and node.op_type == "Attention"
        is_ms_mha = node.domain == "com.microsoft" and node.op_type == "MultiHeadAttention"

        if is_ms_attn and len(node.input) >= 3 and node.output and node.output[0]:
            # Fused QKV self-attention (encoder): input, W_qkv, bias[, mask]
            src, w_qkv, b_qkv = node.input[0], node.input[1], node.input[2]
            out = node.output[0]
            num_heads, head_size, scale = infer_head_size(node)
            attrs = {a.name: helper.get_attribute_value(a) for a in node.attribute}
            qkv_sizes = attrs.get("qkv_hidden_sizes")
            if qkv_sizes:
                q_h, k_h, v_h = (int(x) for x in list(qkv_sizes)[:3])
            else:
                q_h = k_h = v_h = num_heads * head_size
            prefix = f"/trackdub/decomposed_attention_{replaced_attention}"
            qkv = f"{prefix}/qkv"
            q_s, k_s, v_s = f"{prefix}/q", f"{prefix}/k", f"{prefix}/v"
            split_sizes = add_init(f"{prefix}/split_sizes", np.array([q_h, k_h, v_h], dtype=np.int64))
            new_nodes += [
                helper.make_node("MatMul", [src, w_qkv], [f"{prefix}/qkv_mm"], name=f"{prefix}/QkvMatMul"),
            ]
            if b_qkv:
                new_nodes.append(
                    helper.make_node("Add", [f"{prefix}/qkv_mm", b_qkv], [qkv], name=f"{prefix}/QkvBias")
                )
            else:
                new_nodes.append(helper.make_node("Identity", [f"{prefix}/qkv_mm"], [qkv], name=f"{prefix}/Qkv"))
            new_nodes.append(
                helper.make_node("Split", [qkv, split_sizes], [q_s, k_s, v_s], name=f"{prefix}/SplitQkv", axis=-1)
            )
            new_nodes += make_attention_body(
                q_s, k_s, v_s, out, prefix, num_heads, head_size, scale,
                causal=bool(int(attrs.get("unidirectional", 0))),
                past_seq_init_name=None,
                present_key=None,
                present_value=None,
            )
            replaced_attention += 1
            continue

        if is_ms_mha and len(node.input) >= 3 and node.output and node.output[0]:
            q = node.input[0]
            k = node.input[1]
            v = node.input[2]
            past_key = node.input[6] if len(node.input) > 6 and node.input[6] else None
            past_value = node.input[7] if len(node.input) > 7 and node.input[7] else None
            out = node.output[0]
            present_k = node.output[1] if len(node.output) > 1 and node.output[1] else None
            present_v = node.output[2] if len(node.output) > 2 and node.output[2] else None
            num_heads, head_size, scale = infer_head_size(node)
            attrs = {a.name: helper.get_attribute_value(a) for a in node.attribute}
            causal = bool(int(attrs.get("unidirectional", 0)))
            prefix = f"/trackdub/decomposed_mha_{replaced_mha}"

            # Cross-attn: K/V are encoder cache (full sequence) — no concat with past.
            # Self-attn: Concat(past, current) on the sequence axis of [B, N, S, D] (axis=2).
            is_cross = present_k is None and present_v is None and past_key and past_value
            if is_cross:
                full_k, full_v = past_key, past_value
            else:
                full_k, full_v = f"{prefix}/full_k", f"{prefix}/full_v"
                if past_key and past_value:
                    new_nodes += [
                        helper.make_node("Concat", [past_key, k], [full_k], name=f"{prefix}/ConcatK", axis=2),
                        helper.make_node("Concat", [past_value, v], [full_v], name=f"{prefix}/ConcatV", axis=2),
                    ]
                else:
                    new_nodes += [
                        helper.make_node("Identity", [k], [full_k], name=f"{prefix}/IdK"),
                        helper.make_node("Identity", [v], [full_v], name=f"{prefix}/IdV"),
                    ]

            # MHA K/V may already be [B, N, S, D]; Q is [B, S, H].
            # make_attention_body reshapes Q from [B,S,H] and K/V from [B,S,H].
            # If K/V are 4-D [B,N,S,D], transpose to [B,S,N,D] first then the body reshape works.
            k_in, v_in = full_k, full_v
            # Heuristic: cross-attn K from encoder cache is [B, N, S, D].
            if is_cross:
                k_3 = f"{prefix}/k3"
                v_3 = f"{prefix}/v3"
                new_nodes += [
                    helper.make_node("Transpose", [full_k], [k_3], name=f"{prefix}/KToBSH", perm=[0, 2, 1, 3]),
                    helper.make_node("Transpose", [full_v], [v_3], name=f"{prefix}/VToBSH", perm=[0, 2, 1, 3]),
                ]
                k_in, v_in = k_3, v_3
            new_nodes += make_attention_body(
                q, k_in, v_in, out, prefix, num_heads, head_size, scale,
                causal=causal,
                past_seq_init_name=None,
                present_key=present_k,
                present_value=present_v,
            )
            replaced_mha += 1
            continue

        new_nodes.append(node)

    remaining = sorted({n.op_type for n in new_nodes if n.domain == "com.microsoft"})
    print(f"decomposed_attention={replaced_attention}")
    print(f"decomposed_multi_head_attention={replaced_mha}")
    print(f"remaining_com_microsoft_ops={','.join(remaining) if remaining else ''}")

    if args.dry_run:
        print("dry_run=1 (model not written)")
        return 0

    if replaced_attention == 0 and replaced_mha == 0:
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
