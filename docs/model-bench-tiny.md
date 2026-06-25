# LLM model benchmark — template induction quality

Run: 2026-06-25 09:20 UTC  |  Backend: http://localhost:11434
Models: phi4-mini, llama3.2:3b, llama3.2:1b, smollm2:1.7b, qwen3.5:0.8b, qwen3.5:4b
Fixtures: 5 pages from WCXB dev split

## F1 by model × page

| Page (host) | phi4-mini | llama3.2:3b | llama3.2:1b | smollm2:1.7b | qwen3.5:0.8b | qwen3.5:4b |
|---|---:|---:|---:|---:|---:|---:|
| 4690 (www.wsetglobal.com) | 0.224 | 0.254 | — | 0.110 | 0.254 | 0.974 |
| 4459 (www.fermyon.com) | 0.995 | 0.982 | — | — | 0.995 | 0.995 |
| 0660 (mybirdbuddy.eu) | — | 0.628 | — | — | — | 0.938 |
| 0203 (interiordesign.net) | 0.382 | 0.481 | — | — | 0.481 | 0.481 |
| 4349 (www.vims.edu) | 0.831 | 0.831 | — | — | 0.383 | 0.524 |

## Aggregate

| Model | Avg F1 | Avg train sec | Avg markdown chars |
|---|---:|---:|---:|
| phi4-mini | 0.608 | 13.2 | 73892 |
| llama3.2:3b | 0.635 | 12.4 | 52572 |
| llama3.2:1b | 0.000 | 0.0 | 0 |
| smollm2:1.7b | 0.110 | 8.5 | 8574 |
| qwen3.5:0.8b | 0.528 | 7.5 | 64448 |
| qwen3.5:4b | 0.782 | 25.0 | 15673 |

## Train seconds by model × page

| Page | phi4-mini | llama3.2:3b | llama3.2:1b | smollm2:1.7b | qwen3.5:0.8b | qwen3.5:4b |
|---|---:|---:|---:|---:|---:|---:|
| 4690 | 14.1 | 9.9 | 6.5 | 8.5 | 13.9 | 23.8 |
| 4459 | 18.1 | 17.7 | 7.5 | 10.3 | 6.0 | 30.5 |
| 0660 | 16.0 | 15.8 | 6.8 | 11.2 | 71.6 | 27.7 |
| 0203 | 10.8 | 9.3 | 8.3 | 13.1 | 5.4 | 21.1 |
| 4349 | 9.7 | 9.5 | 4.5 | 10.4 | 4.7 | 21.8 |
