#!/usr/bin/env python3
"""Train the LightGBM multi-class block classifier from a TSV of (features, label) rows.

Inputs:
  --features TSV produced by `wcxb_to_features.py`.
  --out      Path to write the trained model in LightGBM's text format.

Reports:
  * Per-class precision / recall / F1.
  * Overall accuracy + macro F1.
  * Feature importance (split count + gain).
  * Confusion matrix.

The text-format LightGBM model is the input to `features_to_onnx.py`.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.metrics import (
    accuracy_score,
    classification_report,
    confusion_matrix,
    f1_score,
)
from sklearn.model_selection import train_test_split

try:
    import lightgbm as lgb
except ImportError as ex:
    raise SystemExit(
        f"lightgbm import failed ({ex}); run `pip install -r requirements.txt`."
    )

from _labels import LABELS, DIM


def load_features(path: Path) -> tuple[np.ndarray, np.ndarray, list[str]]:
    """Read the TSV produced by wcxb_to_features.py. Returns (X, y, feature_names)."""
    # The TSV header is prefixed with '#'. pandas treats it as a comment by
    # default if we pass `comment="#"`; we then provide column names manually.
    feat_cols = [f"f{i}" for i in range(DIM)]
    cols = ["xpath", "label_id", "label"] + feat_cols
    df = pd.read_csv(path, sep="\t", comment="#", names=cols, header=None)
    if df.empty:
        raise SystemExit("input TSV is empty")
    X = df[feat_cols].to_numpy(dtype=np.float32)
    y = df["label_id"].to_numpy(dtype=np.int32)
    return X, y, feat_cols


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--features", required=True, help="TSV from wcxb_to_features.py.")
    ap.add_argument("--out", required=True, help="Output LightGBM .txt model path.")
    ap.add_argument(
        "--val-fraction", type=float, default=0.15,
        help="Held-out validation fraction. Default 0.15.",
    )
    ap.add_argument("--num-trees", type=int, default=500)
    ap.add_argument("--num-leaves", type=int, default=63)
    ap.add_argument("--learning-rate", type=float, default=0.05)
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args()

    X, y, feat_names = load_features(Path(args.features))
    print(f"loaded {len(X)} rows, {X.shape[1]} features", file=sys.stderr)

    X_train, X_val, y_train, y_val = train_test_split(
        X, y, test_size=args.val_fraction, random_state=args.seed, stratify=y
        if len(np.unique(y)) > 1 and min(np.bincount(y, minlength=len(LABELS))) >= 2
        else None,
    )

    params = {
        "objective": "multiclass",
        "num_class": len(LABELS),
        "metric": "multi_logloss",
        "num_leaves": args.num_leaves,
        "learning_rate": args.learning_rate,
        "feature_fraction": 0.9,
        "bagging_fraction": 0.8,
        "bagging_freq": 5,
        "min_data_in_leaf": 50,
        "verbose": -1,
    }
    train_set = lgb.Dataset(X_train, label=y_train, feature_name=feat_names)
    val_set = lgb.Dataset(X_val, label=y_val, feature_name=feat_names, reference=train_set)
    model = lgb.train(
        params,
        train_set,
        num_boost_round=args.num_trees,
        valid_sets=[train_set, val_set],
        valid_names=["train", "val"],
        callbacks=[lgb.early_stopping(50), lgb.log_evaluation(50)],
    )

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    model.save_model(str(out_path))
    print(f"wrote {out_path}", file=sys.stderr)

    # Predict on the held-out set; report per-class metrics.
    y_pred = model.predict(X_val).argmax(axis=1)
    print()
    print("=== validation metrics ===")
    print(f"accuracy: {accuracy_score(y_val, y_pred):.4f}")
    print(f"macro F1: {f1_score(y_val, y_pred, average='macro', zero_division=0):.4f}")
    print()
    print(classification_report(
        y_val, y_pred, target_names=LABELS, zero_division=0, digits=4
    ))

    print("=== feature importance (top 20 by gain) ===")
    importance = model.feature_importance(importance_type="gain")
    pairs = sorted(zip(feat_names, importance), key=lambda kv: kv[1], reverse=True)
    for name, gain in pairs[:20]:
        print(f"  {name:30s} {gain:.0f}")


if __name__ == "__main__":
    main()
