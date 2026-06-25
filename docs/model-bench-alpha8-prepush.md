# LLM model benchmark — template induction quality

Run: 2026-06-25 12:31 UTC  |  Backend: http://localhost:11434
Models: qwen3.5:0.8b, qwen3.5:4b, gemma4:e2b
Fixtures: 5 pages from WCXB dev split

## F1 by model × page

| Page (host) | qwen3.5:0.8b | qwen3.5:4b | gemma4:e2b |
|---|---:|---:|---:|
| 4690 (www.wsetglobal.com) | 0.149 | 0.974 | 0.254 |
| 4459 (www.fermyon.com) | 0.995 | 0.995 | 0.982 |
| 0660 (mybirdbuddy.eu) | 0.424 | 0.938 | 0.970 |
| 0203 (interiordesign.net) | 0.481 | 0.481 | 0.481 |
| 4349 (www.vims.edu) | — | 0.524 | 0.422 |

## Aggregate

| Model | Avg F1 | Avg train sec | Avg markdown chars |
|---|---:|---:|---:|
| qwen3.5:0.8b | 0.512 | 5.4 | 108678 |
| qwen3.5:4b | 0.782 | 17.9 | 15673 |
| gemma4:e2b | 0.622 | 14.4 | 56435 |

## Train seconds by model × page

| Page | qwen3.5:0.8b | qwen3.5:4b | gemma4:e2b |
|---|---:|---:|---:|
| 4690 | 5.7 | 17.9 | 13.6 |
| 4459 | 4.9 | 20.5 | 17.8 |
| 0660 | 6.8 | 20.2 | 17.8 |
| 0203 | 4.0 | 15.4 | 10.7 |
| 4349 | 6.2 | 15.7 | 12.0 |
