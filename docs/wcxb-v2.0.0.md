## StyloExtract heuristic v2.0.0 vs WCXB baselines (dev split, profile=Wcxb, mode=static-HTML, page-types=all)

Run: 2026-06-29 00:22 UTC | pages=1495 | errors=2 | wall-clock=00:39

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v2.0.0 | 0.759  | 0.782     | 0.824  | 15 ms       | 154 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 68.7% | Without-reject: 87.9%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=792) |        0.888 |   0.932 |       0.926 |       0.825 |
| Documentation (n=91) |        0.875 |   0.932 |       0.888 |       0.736 |
| Service (n=165) |        0.724 |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.501 |   0.808 |       0.585 |       0.466 |
| Collection (n=117) |        0.552 |   0.716 |       0.553 |       0.445 |
| Listing (n=99) |        0.536 |   0.707 |       0.589 |       0.496 |
| Product (n=119) |        0.486 |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 15 ms |
| p90        | 45 ms |
| p99        | 154 ms |
| max        | 921 ms |

Total pages processed: 1495 | Errors: 2 | Error rate: 0.1%
