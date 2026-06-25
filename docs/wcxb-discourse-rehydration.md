## StyloExtract heuristic v1.3 vs WCXB baselines (dev split, profile=Wcxb, mode=static-HTML, page-types=all)

Run: 2026-06-25 00:57 UTC | pages=1495 | errors=2 | wall-clock=00:25

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v1.3 | 0.755  | 0.752     | 0.843  | 8 ms       | 90 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 69.9% | Without-reject: 83.0%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=792) |        0.886 |   0.932 |       0.926 |       0.825 |
| Documentation (n=91) |        0.881 |   0.932 |       0.888 |       0.736 |
| Service (n=165) |        0.720 |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.535 |   0.808 |       0.585 |       0.466 |
| Collection (n=117) |        0.491 |   0.716 |       0.553 |       0.445 |
| Listing (n=99) |        0.519 |   0.707 |       0.589 |       0.496 |
| Product (n=119) |        0.500 |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 8 ms |
| p90        | 27 ms |
| p99        | 90 ms |
| max        | 805 ms |

Total pages processed: 1495 | Errors: 2 | Error rate: 0.1%
