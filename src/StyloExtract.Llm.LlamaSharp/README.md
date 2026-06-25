# Mostlylucid.StyloExtract.Llm.LlamaSharp

In-process CPU LLM backend for StyloExtract template induction / repair,
via [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (the .NET binding
for `llama.cpp`). No Ollama server required — the model loads from a
GGUF file on disk and runs in the host process.

## When to use this vs Ollama

| Concern | Ollama backend | LlamaSharp backend |
|---|---|---|
| Deployment shape | Separate `ollama serve` process + HTTP | Embedded in your .NET host |
| Model storage | Ollama-managed (`~/.ollama/models`) | Plain GGUF file on disk |
| Cold start | Ollama daemon already warm | Pay model load (~1–5 s) on first call |
| Concurrent generations | Multi-context | Single context — calls serialise |
| Target | Long-running services with shared LLM | Embedded / single-binary distribution |

LlamaSharp is the target backend for the "CPU + tiny model embedded
in the host" deployment.

## Setup

```bash
dotnet add package Mostlylucid.StyloExtract.Llm.LlamaSharp
```

Download a GGUF model (e.g. from HuggingFace) once at deploy time. For
template induction the empirically-strong picks are:

- `qwen3.5:4b` (q4_k_m): ~3 GB on disk, F1 0.78 on the WCXB bench.
- `llama3.2:3b` (q4_k_m): ~2 GB, F1 0.64, 2× faster — best smaller option.
- `qwen3.5:0.8b` (q4_k_m): ~1 GB, F1 0.53, fastest viable.

See `tests/StyloExtract.Llm.Benchmark/README.md` for the cross-model
quality matrix.

## DI wiring

```csharp
services.AddStyloExtract(...);
services.AddStyloExtractLlamaSharp(o =>
{
    o.ModelPath = "/var/models/qwen3.5-4b-q4_k_m.gguf";
    o.ContextSize = 8192;
    o.Threads = 8;              // 0 = let llama.cpp pick (physical cores)
    o.GpuLayerCount = 0;        // > 0 if you want partial GPU offload
});
services.AddStyloExtractLlmInducer("config/templates");
```

`AddStyloExtractLlmInducer` works unchanged — the inducer resolves
whichever `ILlmTextProvider` is registered, Ollama or LlamaSharp.

## Operational notes

- Model is loaded LAZILY on the first `CompleteAsync` call. Call
  `LlamaSharpTextProvider.EnsureLoaded()` at startup to warm it.
- Single context per provider; concurrent template inductions queue.
  For high-concurrency template-training workloads, prefer Ollama.
- Native binaries (libllama.dll/so/dylib) ship via
  `LLamaSharp.Backend.Cpu`. The package is NOT AOT-compatible.
