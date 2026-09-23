"""Drop attention-probability graph outputs from whisper encoder/decoder ONNX exports.

Xenova medium/large-v3 exports were made with output_attentions=True, exposing
encoder_attentions.* / decoder_attentions.* / cross_attentions.* as graph outputs.
They are unused by WhisperOnnxAudioTranscriptionEngine and block TRT-RTX attention
fusion, so keep only last_hidden_state (encoder) and logits + present.* (decoder).

Usage: python prune_whisper_outputs.py <src.onnx> <dst.onnx> encoder|decoder
Exit code 3 means nothing to prune (dst not written).
"""

import os
import sys

import onnx


def keep_output(kind: str, name: str) -> bool:
    if kind == "encoder":
        return name == "last_hidden_state"
    return name == "logits" or name.startswith("present.")


def main() -> int:
    src, dst, kind = sys.argv[1], sys.argv[2], sys.argv[3]
    model = onnx.load(src, load_external_data=True)
    graph = model.graph

    kept = [o for o in graph.output if keep_output(kind, o.name)]
    if len(kept) == len(graph.output):
        return 3
    if not kept:
        raise SystemExit(f"No {kind} outputs to keep in {src}")

    del graph.output[:]
    graph.output.extend(kept)

    # Backward reachability from the kept outputs; drop nodes that only fed pruned outputs.
    live = {o.name for o in kept}
    reachable = []
    for node in reversed(graph.node):
        if any(out in live for out in node.output):
            reachable.append(node)
            live.update(i for i in node.input if i)
    reachable.reverse()
    removed = len(graph.node) - len(reachable)
    del graph.node[:]
    graph.node.extend(reachable)

    initializers = [i for i in graph.initializer if i.name in live]
    del graph.initializer[:]
    graph.initializer.extend(initializers)

    os.makedirs(os.path.dirname(dst), exist_ok=True)
    data_name = os.path.basename(dst) + ".data"
    data_path = os.path.join(os.path.dirname(dst), data_name)
    if os.path.exists(data_path):
        os.remove(data_path)
    onnx.save_model(model, dst, save_as_external_data=True, all_tensors_to_one_file=True, location=data_name)
    print(f"Pruned {kind}: kept {len(kept)} outputs, removed {removed} nodes -> {dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
