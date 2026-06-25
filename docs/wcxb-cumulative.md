## StyloExtract heuristic v1.3 vs WCXB baselines (dev split, profile=Wcxb, mode=static-HTML, page-types=all)

Run: 2026-06-25 01:00 UTC | pages=1495 | errors=2 | wall-clock=00:25

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v1.3 | 0.760  | 0.756     | 0.849  | 9 ms       | 88 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 70.5% | Without-reject: 82.9%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=792) |        0.889 |   0.932 |       0.926 |       0.825 |
| Documentation (n=91) |        0.881 |   0.932 |       0.888 |       0.736 |
| Service (n=165) |        0.724 |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.535 |   0.808 |       0.585 |       0.466 |
| Collection (n=117) |        0.500 |   0.716 |       0.553 |       0.445 |
| Listing (n=99) |        0.548 |   0.707 |       0.589 |       0.496 |
| Product (n=119) |        0.501 |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 9 ms |
| p90        | 28 ms |
| p99        | 88 ms |
| max        | 760 ms |

Total pages processed: 1495 | Errors: 2 | Error rate: 0.1%
