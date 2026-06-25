# LLM model benchmark — template induction quality

Run: 2026-06-25 10:11 UTC  |  Backend: http://localhost:11434
Models: qwen3:4b, qwen3:1.7b, phi3.5:3.8b, qwen2.5-coder:3b, granite3.1-moe:3b, deepseek-r1:1.5b, qwen3.5:4b
Fixtures: 5 pages from WCXB dev split

## F1 by model × page

| Page (host) | qwen3:4b | qwen3:1.7b | phi3.5:3.8b | qwen2.5-coder:3b | granite3.1-moe:3b | deepseek-r1:1.5b | qwen3.5:4b |
|---|---:|---:|---:|---:|---:|---:|---:|
| 4690 (www.wsetglobal.com) | — | 0.254 | — | — | 0.138 | — | — |
| 4459 (www.fermyon.com) | — | 0.995 | — | — | 0.638 | — | 0.995 |
| 0660 (mybirdbuddy.eu) | — | 0.628 | — | 0.990 | 0.399 | — | 0.938 |
| 0203 (interiordesign.net) | — | 0.382 | — | 0.481 | 0.139 | — | 0.481 |
| 4349 (www.vims.edu) | — | 0.831 | — | 0.831 | 0.321 | — | — |

## Aggregate

| Model | Avg F1 | Avg train sec | Avg markdown chars |
|---|---:|---:|---:|
| qwen3:4b | 0.000 | 0.0 | 0 |
| qwen3:1.7b | 0.618 | 11.8 | 60600 |
| phi3.5:3.8b | 0.000 | 0.0 | 0 |
| qwen2.5-coder:3b | 0.767 | 20.9 | 17230 |
| granite3.1-moe:3b | 0.327 | 13.1 | 163695 |
| deepseek-r1:1.5b | 0.000 | 0.0 | 0 |
| qwen3.5:4b | 0.805 | 25.8 | 20814 |

## Train seconds by model × page

| Page | qwen3:4b | qwen3:1.7b | phi3.5:3.8b | qwen2.5-coder:3b | granite3.1-moe:3b | deepseek-r1:1.5b | qwen3.5:4b |
|---|---:|---:|---:|---:|---:|---:|---:|
| 4690 | 188.5 | 9.8 | 93.1 | 17.8 | 12.2 | 26.6 | 26.9 |
| 4459 | 240.0 | 16.0 | 76.6 | 26.6 | 18.9 | 140.5 | 29.8 |
| 0660 | 231.3 | 14.2 | 137.4 | 27.9 | 15.0 | 131.6 | 27.0 |
| 0203 | 205.7 | 9.7 | 63.7 | 18.3 | 9.3 | 26.4 | 20.6 |
| 4349 | 205.0 | 9.0 | 61.8 | 16.4 | 10.0 | 32.5 | 19.9 |
