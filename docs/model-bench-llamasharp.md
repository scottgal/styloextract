# LLM model benchmark — template induction quality

Run: 2026-06-25 11:43 UTC  |  Backend: http://localhost:11434
Models: llamasharp:Phi-4-mini-instruct-Q4_K_M, llamasharp:Qwen3.5-4B-Q4_K_M, llamasharp:Qwen2.5-Coder-3B-Instruct-Q4_K_M, qwen3.5:4b
Fixtures: 3 pages from WCXB dev split

## F1 by model × page

| Page (host) | llamasharp:Phi-4-mini-instruct-Q4_K_M | llamasharp:Qwen3.5-4B-Q4_K_M | llamasharp:Qwen2.5-Coder-3B-Instruct-Q4_K_M | qwen3.5:4b |
|---|---:|---:|---:|---:|
| 4690 (www.wsetglobal.com) | 0.224 | — | 0.136 | 0.974 |
| 4459 (www.fermyon.com) | — | — | — | 0.995 |
| 0660 (mybirdbuddy.eu) | 0.562 | — | 0.990 | 0.938 |

## Aggregate

| Model | Avg F1 | Avg train sec | Avg markdown chars |
|---|---:|---:|---:|
| llamasharp:Phi-4-mini-instruct-Q4_K_M | 0.393 | 141.1 | 101221 |
| llamasharp:Qwen3.5-4B-Q4_K_M | 0.000 | 0.0 | 0 |
| llamasharp:Qwen2.5-Coder-3B-Instruct-Q4_K_M | 0.563 | 93.7 | 187013 |
| qwen3.5:4b | 0.969 | 24.1 | 8650 |

## Train seconds by model × page

| Page | llamasharp:Phi-4-mini-instruct-Q4_K_M | llamasharp:Qwen3.5-4B-Q4_K_M | llamasharp:Qwen2.5-Coder-3B-Instruct-Q4_K_M | qwen3.5:4b |
|---|---:|---:|---:|---:|
| 4690 | 97.8 | 0.2 | 58.8 | 22.3 |
| 4459 | 185.8 | 0.2 | 123.9 | 26.3 |
| 0660 | 184.4 | 0.2 | 128.7 | 23.7 |
