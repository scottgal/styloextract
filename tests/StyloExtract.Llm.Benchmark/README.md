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

Two bench runs covering 13 model variants across the current
small-LLM landscape (Llama 3.2, Phi-4-mini, SmolLM2, Qwen 3, Qwen 3.5,
Qwen 2.5 Coder, Phi 3.5, Granite MoE, DeepSeek R1, gemma4). 5 pages
per run.

### Recommendations

- **`qwen3.5:4b`** — production default. Best F1 (0.805).
- **`qwen2.5-coder:3b`** — **smaller and faster pick**. 2 GB, 21 s avg,
  F1 0.767 — only 0.04 behind the 4 B default. **Code-trained model
  matters for CSS-selector reasoning**: this model scored 0.990 on
  mybirdbuddy.eu where qwen3.5:4b scored 0.938.
- **`qwen3:1.7b`** — tiniest viable, 2 GB, 12 s avg, F1 0.618. 2× faster
  than the 4 B default at 77 % of quality.
- **DO NOT** use `qwen3:4b`, `phi3.5:3.8b`, `phi4-mini`, `deepseek-r1`,
  `llama3.2:1b`, `smollm2:*`, `granite3.1-moe:*`, or `gemma3:1b`. Each
  has a specific failure mode: thinking-mode tokens burn the output
  budget; reasoning models like R1 produce empty `content`; some
  small models lack the HTML / CSS exposure to pick valid selectors;
  granite over-extracts (163 KB markdown vs ~16 KB for good models).

### Aggregate (run 2, 7 models)

| Model | Size | Avg F1 | Train sec | Verdict |
|---|---:|---:|---:|---|
| **qwen3.5:4b** | 3 GB | **0.805** | 25.8 | Default. Best F1. |
| **qwen2.5-coder:3b** | 2 GB | **0.767** | 20.9 | **NEW smaller winner** — code-trained matters |
| qwen3:1.7b | 2 GB | 0.618 | **11.8** | Fastest viable; 2× faster than 4 B |
| granite3.1-moe:3b | 2 GB | 0.327 | 13.1 | Over-extracts massively (163 K chars avg) |
| qwen3:4b | 3 GB | **0.000** | 200+ | All trains hit the 200 s timeout (thinking-mode burn) |
| phi3.5:3.8b | 3 GB | **0.000** | 90+ | Every train returned null |
| deepseek-r1:1.5b | 1 GB | **0.000** | 130+ | R1 reasoning gymnastics burns all output tokens |

### Aggregate (run 1, 6 models)

| Model | Size | Avg F1 | Avg train sec | Notes |
|---|---:|---:|---:|---|
| qwen3.5:4b | 3 GB | 0.782 | 25.0 | Default |
| llama3.2:3b | 2 GB | 0.635 | 12.4 | Older but works |
| phi4-mini | 3.8 GB | 0.608 | 13.2 | Microsoft's sub-4 B reasoner |
| qwen3.5:0.8b | 1 GB | 0.528 | 7.5 | Practical tiny floor |
| smollm2:1.7b | 1 GB | 0.110 | 8.5 | Insufficient HTML / CSS training |
| llama3.2:1b | 1 GB | 0.000 | — | Below threshold |

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
