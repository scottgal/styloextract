#!/usr/bin/env python3
"""Project WCXB labeled pages into (features, label) rows for LightGBM.

For each WCXB page:
  1. Decompress the .html.gz.
  2. Call the C# `stylo-extract extract-features` CLI on the HTML.
     This returns one JSON record per element, each carrying the 45-dim
     feature vector AND a positional xpath. Doing extraction in C# means
     training-time features match inference-time features by construction;
     there is no parallel Python implementation to drift away.
  3. Parse the gold annotation JSON and project per-element labels.
  4. Write a TSV: `xpath\tlabel\tf0\tf1\t...\tf44` to the output file.

The label projection is intentionally simple: the gold `main_content` is a
text blob, so an element whose normalised text falls inside that blob is
labelled MainContent. Tag-based projection handles the chrome roles (nav,
header, footer, form, table). Everything else is Boilerplate. See
`docs/ml-classifier-v2-design.md` for the design.
"""
from __future__ import annotations

import argparse
import gzip
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

# Label set MUST match BlockRole in the C# enum. Mismatched ordinals would
# silently train a model whose class labels don't decode at inference time.
LABELS = [
    "Unknown",
    "MainContent",
    "Article",
    "Heading",
    "Summary",
    "PrimaryNavigation",
    "SecondaryNavigation",
    "Breadcrumb",
    "Sidebar",
    "RelatedLinks",
    "Footer",
    "Header",
    "Advertisement",
    "CookieBanner",
    "Form",
    "Table",
    "CodeBlock",
    "Boilerplate",
    "RepeatedItem",
]
LABEL_TO_ID = {label: i for i, label in enumerate(LABELS)}


@dataclass
class WcxbSample:
    file_id: str
    html: str
    main_content_text: str


def iter_wcxb(dataset: Path, split: str) -> Iterable[WcxbSample]:
    """Yield WCXB samples for the given split (dev / train / test)."""
    gt_dir = dataset / split / "groundtruth"
    html_dir = dataset / split / "html"
    if not gt_dir.is_dir() or not html_dir.is_dir():
        raise SystemExit(
            f"WCXB layout missing under {dataset}/{split}; "
            "expected groundtruth/ and html/ subdirectories."
        )
    for gt_path in sorted(gt_dir.glob("*.json")):
        file_id = gt_path.stem
        html_gz = html_dir / f"{file_id}.html.gz"
        if not html_gz.exists():
            continue
        with gzip.open(html_gz, "rt", encoding="utf-8", errors="replace") as f:
            html = f.read()
        with open(gt_path, "r", encoding="utf-8") as f:
            gt = json.load(f)
        main = gt.get("ground_truth", {}).get("main_content") or ""
        yield WcxbSample(file_id=file_id, html=html, main_content_text=main)


_WS = re.compile(r"\s+")


def normalise(text: str) -> str:
    """Whitespace-normalise text for substring containment checks."""
    return _WS.sub(" ", text).strip().lower()


def project_label(
    xpath: str, tag: str, element_text: str, gold_main: str
) -> str:
    """Project a per-element label using the gold main_content text plus tag rules."""
    norm_el = normalise(element_text)
    if norm_el and len(norm_el) >= 20 and norm_el in gold_main:
        return "MainContent"
    # Tag-based chrome projection. Order matters: footer-inside-nav still
    # gets PrimaryNavigation because the outer-most non-content role wins
    # by virtue of being the first ancestor visited at projection time.
    # Here we only look at the element's own tag; the model picks up
    # ancestor signals from its feature vector.
    if tag == "nav":
        return "PrimaryNavigation"
    if tag == "header":
        return "Header"
    if tag == "footer":
        return "Footer"
    if tag == "form":
        return "Form"
    if tag == "table":
        return "Table"
    if tag in ("h1", "h2", "h3", "h4", "h5", "h6"):
        return "Heading"
    if tag == "pre":
        return "CodeBlock"
    return "Boilerplate"


def extract_features(cli: str, html: str, min_text: int) -> list[dict]:
    """Call the C# extract-features CLI on stdin and parse JSONL output."""
    proc = subprocess.run(
        [cli, "extract-features", "-", "--min-text", str(min_text)],
        input=html.encode("utf-8"),
        capture_output=True,
        check=True,
    )
    out = []
    for line in proc.stdout.decode("utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        out.append(json.loads(line))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--dataset", required=True, help="WCXB dataset root.")
    ap.add_argument("--split", default="dev", choices=["dev", "train", "test"])
    ap.add_argument(
        "--cli",
        default=os.environ.get("STYLO_EXTRACT_CLI"),
        help="Path to the stylo-extract CLI. Defaults to $STYLO_EXTRACT_CLI.",
    )
    ap.add_argument(
        "--min-text",
        type=int,
        default=10,
        help="Skip elements with TextContent shorter than this. Default 10.",
    )
    ap.add_argument("--out", required=True, help="Output TSV path.")
    ap.add_argument(
        "--max-pages",
        type=int,
        default=None,
        help="Stop after N pages. Useful for smoke runs.",
    )
    args = ap.parse_args()

    if not args.cli:
        print("--cli or $STYLO_EXTRACT_CLI must be set", file=sys.stderr)
        sys.exit(2)
    cli = args.cli

    dataset = Path(args.dataset)
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    rows_written = 0
    label_counts: dict[str, int] = {}
    with open(out_path, "w", encoding="utf-8") as out:
        # TSV header line so downstream tooling has column names. Comment-
        # prefix the line with '#' so pandas can skip it on read if needed.
        cols = ["xpath", "label_id", "label"] + [f"f{i}" for i in range(45)]
        out.write("#" + "\t".join(cols) + "\n")

        for page_idx, sample in enumerate(iter_wcxb(dataset, args.split)):
            if args.max_pages and page_idx >= args.max_pages:
                break
            gold = normalise(sample.main_content_text)
            try:
                records = extract_features(cli, sample.html, args.min_text)
            except subprocess.CalledProcessError as ex:
                print(
                    f"[{sample.file_id}] extract-features failed: {ex.stderr.decode(errors='replace')[:200]}",
                    file=sys.stderr,
                )
                continue
            for rec in records:
                xpath = rec["xpath"]
                tag = rec["tag"]
                text_len = rec["text_len"]
                # The CLI emits a whitespace-normalised, lowercased excerpt of
                # the element's text (up to 4 KB). Substring containment in
                # the gold main_content blob is the MainContent signal.
                excerpt = rec.get("text", "")
                label = project_label(xpath, tag, excerpt, gold)
                features = rec["features"]
                if len(features) != 45:
                    continue
                cols = [xpath, str(LABEL_TO_ID[label]), label] + [f"{v:.6f}" for v in features]
                out.write("\t".join(cols) + "\n")
                rows_written += 1
                label_counts[label] = label_counts.get(label, 0) + 1

            if page_idx % 50 == 0 and page_idx > 0:
                print(f"  {page_idx} pages, {rows_written} rows", file=sys.stderr)

    print(f"wrote {rows_written} rows to {out_path}", file=sys.stderr)
    print("label counts:", file=sys.stderr)
    for label in LABELS:
        c = label_counts.get(label, 0)
        if c:
            print(f"  {label}: {c}", file=sys.stderr)


if __name__ == "__main__":
    main()
