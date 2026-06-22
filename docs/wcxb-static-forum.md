## StyloExtract heuristic v1.3 vs WCXB baselines (dev split, profile=MainContentOnly, mode=static-HTML, page-types=forum)

Run: 2026-06-22 09:04 UTC | pages=112 | errors=1 | wall-clock=00:06

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v1.3 | 0.456  | 0.481     | 0.587  | 43 ms       | 219 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 50.3% | Without-reject: 80.1%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=0) |          n/a |   0.932 |       0.926 |       0.825 |
| Documentation (n=0) |          n/a |   0.932 |       0.888 |       0.736 |
| Service (n=0) |          n/a |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.456 |   0.808 |       0.585 |       0.466 |
| Collection (n=0) |          n/a |   0.716 |       0.553 |       0.445 |
| Listing (n=0) |          n/a |   0.707 |       0.589 |       0.496 |
| Product (n=0) |          n/a |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 43 ms |
| p90        | 133 ms |
| p99        | 219 ms |
| max        | 222 ms |

Total pages processed: 112 | Errors: 1 | Error rate: 0.9%
