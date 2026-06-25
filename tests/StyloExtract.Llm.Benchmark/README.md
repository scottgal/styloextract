# LLM model benchmark — template induction quality

Runs the cross-product of (models × pages) for template induction and
reports F1 / train-time / markdown-size matrices. Lets you pick the
right model for a deployment empirically rather than by spot-check.

## Usage

```bash
dotnet run --project tests/StyloExtract.Llm.Benchmark -c Release -- \
    --ollama-url http://localhost:11434 \
    --models qwen3.5:4b,llama3.2:3b,phi4-mini \
    --wcxb-path /tmp/wcxb \
    --page-ids 4690,4459,0660,0203,4349 \
    --out docs/model-bench.md
```

Pages are referred to by their WCXB dev-split file ID. The harness
loads each page's HTML.gz + ground-truth JSON, trains a template
per model, applies the template via `AddStyloExtractOperatorTemplates`,
and word-F1's the resulting markdown against the gold `main_content`.

## Findings (2026-06-25)

Current default is `qwen3.5:4b` (~3 GB, ~25 s per induce on CPU).
Bench against current small-model leaders on a 5-page sample:

| Model | Size | Avg F1 | Avg train sec | Notes |
|---|---:|---:|---:|---|
| **qwen3.5:4b** | 3 GB | **0.782** | 25.0 | Quality king. Default. |
| llama3.2:3b | 2 GB | 0.635 | 12.4 | 2× faster, 80% quality. Best smaller-than-default. |
| phi4-mini | 3.8 GB | 0.608 | 13.2 | Microsoft's sub-4B reasoner. Comparable to llama3.2:3b. |
| qwen3.5:0.8b | 1 GB | 0.528 | 7.5 | Practical tiny floor. Sometimes wins on simple pages. |
| smollm2:1.7b | 1 GB | 0.110 | 8.5 | Insufficient HTML/CSS training. |
| llama3.2:1b | 1 GB | 0.000 | — | Every train returned null. Below threshold for CSS-selector reasoning. |

### Recommendations

- **`qwen3.5:4b`** — production default. Best F1.
- **`llama3.2:3b`** — pick for the smaller-and-faster axis. Half wall-clock at 80% of qwen3.5:4b's quality.
- **`qwen3.5:0.8b`** — pick for the tiniest viable footprint. Occasionally beats 4× larger models on simple pages.
- **DO NOT** use `llama3.2:1b`, `smollm2:1.7b`, or smaller for template induction. Below ~3B params today, CSS-selector reasoning fails or struggles.

### Interesting per-page inversions

| Page | qwen3.5:4b | phi4-mini | llama3.2:3b |
|---|---:|---:|---:|
| 4690 (wsetglobal) | **0.974** | 0.224 | 0.254 |
| 4349 (vims.edu) | 0.524 | **0.831** | **0.831** |

Smaller models sometimes pick better selectors on simpler pages
because they don't overthink. On complex pages with multiple nested
content containers (like wsetglobal where qwen3.5:4b nailed
`div#uBlogsy_main section.uBlogsy_post_body`), the larger model wins
decisively. This is why the bench exists — picking a single default
is suboptimal; picking per-deployment via the bench is better.

## Future backends

The current bench uses Ollama via `OllamaTextProvider`. Adding more
`ILlmTextProvider` implementations would let the bench compare:

- **LlamaSharp** (`llama.cpp` binding, GGUF models, pure-CPU inference) —
  in-process, no Ollama server, embedded with the extraction pipeline.
- **ONNX Runtime GenAI** (Microsoft.ML.OnnxRuntime is already a package
  reference) — for models with pre-quantized ONNX builds on HuggingFace
  (Phi-4-mini, Qwen 3, Llama 3.2 all have published ONNX variants).

Both would let StyloExtract ship the LLM training loop without a
separate server dependency. Target deployment: LlamaSharp + CPU + a
~1-3 GB model embedded in the host process.
