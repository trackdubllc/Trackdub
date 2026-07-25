"""Decompose Whisper decoder cross-attention MHA for DirectML ModelLab builds."""

from __future__ import annotations

import argparse
import os
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper, shape_inference


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Replace Whisper cross-attention com.microsoft MultiHeadAttention nodes with primitive ONNX ops."
    )
    parser.add_argument("decoder", help="Path to decoder.onnx")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    decoder_path = Path(args.decoder).resolve()
    model_dir = decoder_path.parent

    model = onnx.load(str(decoder_path), load_external_data=True)
    try:
        inferred = shape_inference.infer_shapes(model)
    except Exception:
        inferred = model

    infos = {
        value.name: value
        for value in [*inferred.graph.value_info, *inferred.graph.input, *inferred.graph.output]
    }

    def tensor_elem_type(name: str) -> int:
        value_info = infos.get(name)
        if value_info is not None and value_info.type.HasField("tensor_type"):
            return value_info.type.tensor_type.elem_type
        return TensorProto.FLOAT

    graph = model.graph
    existing_names = {initializer.name for initializer in graph.initializer}

    def add_initializer(name: str, array: np.ndarray) -> str:
        candidate = name
        suffix = 0
        while candidate in existing_names:
            suffix += 1
            candidate = f"{name}_{suffix}"

        existing_names.add(candidate)
        graph.initializer.append(numpy_helper.from_array(array, candidate))
        return candidate

    new_nodes = []
    replaced = 0
    for node in graph.node:
        is_cross_attention_mha = (
            node.op_type == "MultiHeadAttention"
            and node.domain == "com.microsoft"
            and "cross_attn" in node.name
            and len(node.input) >= 3
            and len(node.output) >= 1
        )
        if not is_cross_attention_mha:
            new_nodes.append(node)
            continue

        attrs = {attr.name: helper.get_attribute_value(attr) for attr in node.attribute}
        num_heads = int(attrs.get("num_heads", 6))
        scale = float(attrs.get("scale", 1.0 / 8.0))
        head_size = int(round((1.0 / scale) ** 2)) if scale > 0 else 64
        hidden_size = num_heads * head_size
        scale_dtype = np.float16 if tensor_elem_type(node.input[0]) == TensorProto.FLOAT16 else np.float32

        prefix = f"/trackdub/decomposed_cross_mha_{replaced}"
        query, key, value = node.input[:3]
        output = node.output[0]

        query_shape = add_initializer(
            f"{prefix}/query_shape",
            np.array([0, 0, num_heads, head_size], dtype=np.int64),
        )
        output_shape = add_initializer(
            f"{prefix}/output_shape",
            np.array([0, 0, hidden_size], dtype=np.int64),
        )
        scale_name = add_initializer(f"{prefix}/scale", np.array(scale, dtype=scale_dtype))

        query_reshaped = f"{prefix}/query_reshaped"
        query_transposed = f"{prefix}/query_transposed"
        key_transposed = f"{prefix}/key_transposed"
        query_key = f"{prefix}/query_key"
        scaled = f"{prefix}/scaled"
        probabilities = f"{prefix}/probabilities"
        context = f"{prefix}/context"
        context_transposed = f"{prefix}/context_transposed"

        new_nodes.extend(
            [
                helper.make_node("Reshape", [query, query_shape], [query_reshaped], name=f"{prefix}/ReshapeQuery"),
                helper.make_node(
                    "Transpose",
                    [query_reshaped],
                    [query_transposed],
                    name=f"{prefix}/TransposeQuery",
                    perm=[0, 2, 1, 3],
                ),
                helper.make_node(
                    "Transpose",
                    [key],
                    [key_transposed],
                    name=f"{prefix}/TransposeKey",
                    perm=[0, 1, 3, 2],
                ),
                helper.make_node("MatMul", [query_transposed, key_transposed], [query_key], name=f"{prefix}/QueryKey"),
                helper.make_node("Mul", [query_key, scale_name], [scaled], name=f"{prefix}/Scale"),
                helper.make_node("Softmax", [scaled], [probabilities], name=f"{prefix}/Softmax", axis=-1),
                helper.make_node("MatMul", [probabilities, value], [context], name=f"{prefix}/Context"),
                helper.make_node(
                    "Transpose",
                    [context],
                    [context_transposed],
                    name=f"{prefix}/TransposeContext",
                    perm=[0, 2, 1, 3],
                ),
                helper.make_node(
                    "Reshape",
                    [context_transposed, output_shape],
                    [output],
                    name=f"{prefix}/ReshapeOutput",
                ),
            ]
        )
        replaced += 1

    graph.ClearField("node")
    graph.node.extend(new_nodes)
    onnx.checker.check_model(model)

    external_data_path = model_dir / f"{decoder_path.name}.data"
    if external_data_path.exists():
        os.remove(external_data_path)
        onnx.save_model(
            model,
            str(decoder_path),
            save_as_external_data=True,
            all_tensors_to_one_file=True,
            location=external_data_path.name,
            size_threshold=1024,
        )
    else:
        onnx.save_model(model, str(decoder_path))

    print(f"decomposed_cross_attention_nodes={replaced}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
