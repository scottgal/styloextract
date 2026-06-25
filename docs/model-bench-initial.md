# LLM model benchmark — template induction quality

Run: 2026-06-25 09:04 UTC  |  Backend: http://localhost:11434
Models: qwen3.5:0.8b, qwen3.5:4b, gemma4:e2b
Fixtures: 5 pages from WCXB dev split

## F1 by model × page

| Page (host) | qwen3.5:0.8b | qwen3.5:4b | gemma4:e2b |
|---|---:|---:|---:|
| 4690 (www.wsetglobal.com) | 0.254 | 0.974 | 0.254 |
| 4459 (www.fermyon.com) | 0.995 | 0.995 | 0.982 |
| 0660 (mybirdbuddy.eu) | — | 0.938 | 0.970 |
| 0203 (interiordesign.net) | — | 0.481 | 0.481 |
| 4349 (www.vims.edu) | 0.831 | 0.524 | 0.422 |

## Aggregate

| Model | Avg F1 | Avg train sec | Avg markdown chars |
|---|---:|---:|---:|
| qwen3.5:0.8b | 0.693 | 5.2 | 68380 |
| qwen3.5:4b | 0.782 | 20.5 | 15673 |
| gemma4:e2b | 0.622 | 12.5 | 56435 |

## Train seconds by model × page

| Page | qwen3.5:0.8b | qwen3.5:4b | gemma4:e2b |
|---|---:|---:|---:|
| 4690 | 5.4 | 20.8 | 12.8 |
| 4459 | 6.2 | 24.7 | 15.8 |
| 0660 | 71.6 | 22.4 | 14.3 |
| 0203 | 76.9 | 17.3 | 9.5 |
| 4349 | 4.0 | 17.2 | 10.3 |
