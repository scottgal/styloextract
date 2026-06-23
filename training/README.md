# StyloExtract ML training pipeline

Trains the per-element block classifier described in
[`../docs/ml-classifier-v2-design.md`](../docs/ml-classifier-v2-design.md).

**Out-of-band.** The training subtree is NOT in the .NET solution. Run it on
demand, commit the resulting ONNX model to `../src/StyloExtract.Ml/Models/`.

## Why C# extracts features, Python trains

The 45-feature vector for each element is computed by the C# CLI's
`stylo-extract extract-features` command. The Python training pipeline reads
that output, projects labels from WCXB gold annotations, trains a LightGBM
multi-class classifier, and exports it to ONNX.

**This split eliminates feature drift between training and inference.** If
Python rolled its own feature extractor and a single off-by-one slipped in
between the two implementations, the trained model would be silently mis-
calibrated at runtime. By making the C# extractor the single source of truth,
training-time and inference-time feature vectors are guaranteed identical.

## One-time setup

```bash
cd training
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt

# Build the AOT CLI once so we can call it for feature extraction.
cd ..
dotnet build src/StyloExtract.Cli.Aot -c Release -nologo
export STYLO_EXTRACT_CLI=$(pwd)/src/StyloExtract.Cli.Aot/bin/Release/net10.0/stylo-extract
```

## Workflow

```bash
# 1) Convert WCXB pages to (features, label) rows. WCXB at /tmp/wcxb.
python wcxb_to_features.py --dataset /tmp/wcxb --split dev --out features.tsv

# 2) Train a LightGBM multi-class classifier on the rows.
python pipeline.py --features features.tsv --out lgbm.txt

# 3) Export the trained model to ONNX.
python features_to_onnx.py --lgbm lgbm.txt --out ../src/StyloExtract.Ml/Models/block-classifier-v2.onnx

# 4) Commit the ONNX artefact.
git add ../src/StyloExtract.Ml/Models/block-classifier-v2.onnx
git commit -m "model(v2): retrain on WCXB dev split, F1=<...>"
```

## Feature schema lock

If `FeatureNames.Version` in the C# source ever bumps, the model retrains
from scratch and the version is recorded in the ONNX metadata. The runtime
asserts the model's feature version matches its compiled-in version on
load; a mismatch refuses to use the model and falls back to heuristic-only.

## Label projection

WCXB gold annotations provide a `main_content` text blob per page. The
projection (`wcxb_to_features.py`) assigns labels:

* Any element whose normalised text appears in `main_content` → `MainContent`
* `<nav>` and descendants → `PrimaryNavigation` / `SecondaryNavigation`
* `<header>` and descendants → `Header`
* `<footer>` and descendants → `Footer`
* `<form>` with meaningful inputs → `Form`
* `<table>` → `Table`
* Everything else → `Boilerplate`

The projection is intentionally lossy. WCXB only labels the body; we project
chrome roles by HTML5 semantics. The result is good enough for cold start;
the warm-start retraining loop (phase 2.1, deferred) refines per-host labels
from operator-template observations.

## Files

  * `wcxb_to_features.py` — WCXB → (features, label) TSV.
  * `pipeline.py` — LightGBM train + F1/ROC-AUC report + feature importance.
  * `features_to_onnx.py` — ONNX export.
  * `requirements.txt` — Python deps.
