#!/usr/bin/env python3
"""Export a trained LightGBM block classifier to ONNX for runtime inference.

The exported model is loaded by `StyloExtract.Ml.Inference.OnnxBlockClassifier`
(phase 3 of the design) at runtime. Schema is fixed:
  * Input  name: "features"
  * Input  shape: [batch_size, 45]
  * Input  type: float32
  * Output name: "probabilities"
  * Output shape: [batch_size, num_classes]

Metadata embedded for the runtime:
  * "feature_version": str(FeatureNames.Version) from the C# extractor.
  * "label_names":     comma-joined LABELS array.
  * "lgbm_num_trees":  string, the number of boosting iterations actually used.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
import lightgbm as lgb
from onnxmltools import convert_lightgbm
from onnxmltools.convert.common.data_types import FloatTensorType

from _labels import LABELS, DIM, FEATURE_VERSION


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--lgbm", required=True, help="LightGBM .txt model.")
    ap.add_argument("--out", required=True, help="Output .onnx path.")
    ap.add_argument(
        "--zipmap", action="store_true",
        help="Include the ZipMap output layer. Default is to strip it so the "
             "runtime gets a clean float[batch, num_classes] tensor.",
    )
    args = ap.parse_args()

    model = lgb.Booster(model_file=args.lgbm)
    print(
        f"loaded LightGBM model: {model.num_trees()} trees, "
        f"{model.num_feature()} features, "
        f"{len(model.dump_model()['tree_info'])} boost rounds",
        file=sys.stderr,
    )

    initial_types = [("features", FloatTensorType([None, DIM]))]
    onnx_model = convert_lightgbm(
        model,
        initial_types=initial_types,
        target_opset=18,
        zipmap=args.zipmap,
    )

    # Stamp metadata. The runtime asserts feature_version matches the
    # compiled-in C# FeatureNames.Version on load; mismatch refuses the model.
    meta = onnx_model.metadata_props
    def set_meta(k, v):
        for m in meta:
            if m.key == k:
                m.value = v
                return
        m = meta.add()
        m.key = k
        m.value = v
    set_meta("feature_version", str(FEATURE_VERSION))
    set_meta("label_names", ",".join(LABELS))
    set_meta("lgbm_num_trees", str(model.num_trees()))

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "wb") as f:
        f.write(onnx_model.SerializeToString())
    print(f"wrote {out_path}", file=sys.stderr)

    # Quick smoke: random batch through ONNX Runtime, check shape.
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])
        rng = np.random.default_rng(0)
        x = rng.standard_normal((3, DIM)).astype(np.float32)
        out_names = [o.name for o in sess.get_outputs()]
        ys = sess.run(out_names, {"features": x})
        for name, y in zip(out_names, ys):
            arr = np.asarray(y)
            print(f"  smoke: output {name} shape={arr.shape}", file=sys.stderr)
    except Exception as ex:
        print(f"  smoke check failed: {ex}", file=sys.stderr)


if __name__ == "__main__":
    main()
