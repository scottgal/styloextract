# StyloExtract.Llm.Ollama

Ollama-backed `ILlmTextProvider` for StyloExtract template induction.

## Use

```csharp
services.AddStyloExtract();
services.AddStyloExtractOperatorTemplates("config/templates");
services.AddStyloExtractLlmInducer(o =>
{
    o.OllamaUrl = "http://localhost:11434";
    o.Model = "gemma4:e4b-it-qat";
});
```

The default model is `gemma4:e4b-it-qat` per the
`docs/ml-classifier-v2-design.md` recommendation: Apache 2.0, 128K
context, 6.1 GB on disk, fits on CPU. Operators can swap to `12b-it-qat`
for stronger output or any other Ollama-supported model.

## What it does

Implements `ILlmTextProvider.CompleteAsync` over Ollama's `/api/chat`
HTTP endpoint with streaming disabled. Returns the model's full
response as a single string. The downstream
`LlmTemplateInducer` extracts the fenced YAML block and validates it.

AOT-clean (pure `System.Net.Http` + `System.Text.Json` source generator).
