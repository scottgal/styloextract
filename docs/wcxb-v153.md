## StyloExtract heuristic v1.3 vs WCXB baselines (dev split, profile=MainContentOnly)

Run: 2026-06-22 08:38 UTC | pages=1495 | errors=2 | wall-clock=00:22

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v1.3 | 0.716  | 0.718     | 0.797  | 8 ms       | 76 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 66.3% | Without-reject: 84.1%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=792) |        0.854 |   0.932 |       0.926 |       0.825 |
| Documentation (n=91) |        0.860 |   0.932 |       0.888 |       0.736 |
| Service (n=165) |        0.699 |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.456 |   0.808 |       0.585 |       0.466 |
| Collection (n=117) |        0.408 |   0.716 |       0.553 |       0.445 |
| Listing (n=99) |        0.504 |   0.707 |       0.589 |       0.496 |
| Product (n=119) |        0.436 |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 8 ms |
| p90        | 26 ms |
| p99        | 76 ms |
| max        | 760 ms |

Total pages processed: 1495 | Errors: 2 | Error rate: 0.1%
