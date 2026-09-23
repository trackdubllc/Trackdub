"""Drop attention-probability graph outputs from an ONNX export before Olive optimizes it.

Exports made with output_attentions=True (e.g. Xenova whisper-medium/large-v3) expose
encoder_attentions.N / decoder_attentions.N / cross_attentions.N as graph outputs. No
Trackdub engine reads them, they cost memory on every run, and they block TRT-RTX
attention fusion.

Usage: python prune_attention_outputs.py <src.onnx> <dst.onnx>
dst is written only when something was pruned; callers check for its existence.

Used by tools/olive/Validate-*.ps1 and embedded in Trackdub.Infrastructure for
OliveModelOptimizationService recipe runs.
"""

import os
import re
import sys

import onnx

ATTENTION_OUTPUT = re.compile(r"^(encoder_attentions|decoder_attentions|cross_attentions)\.\d+$")


def main() -> int:
    src, dst = sys.argv[1], sys.argv[2]
    model = onnx.load(src, load_external_data=False)
    if not any(ATTENTION_OUTPUT.match(o.name) for o in model.graph.output):
        print(f"No attention outputs to prune in {src}")
        return 0

    model = onnx.load(src, load_external_data=True)
    graph = model.graph
    kept = [o for o in graph.output if not ATTENTION_OUTPUT.match(o.name)]
    if not kept:
        raise SystemExit(f"Pruning would remove every output of {src}")
    pruned = len(graph.output) - len(kept)
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

    os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
    data_name = os.path.basename(dst) + ".data"
    data_path = os.path.join(os.path.dirname(os.path.abspath(dst)), data_name)
    if os.path.exists(data_path):
        os.remove(data_path)
    onnx.save_model(model, dst, save_as_external_data=True, all_tensors_to_one_file=True, location=data_name)
    print(f"Pruned {pruned} attention outputs and {removed} nodes -> {dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
