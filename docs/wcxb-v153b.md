## StyloExtract heuristic v1.3 vs WCXB baselines (dev split, profile=MainContentOnly)

Run: 2026-06-22 08:41 UTC | pages=1495 | errors=2 | wall-clock=00:23

| System            |     F1 | Precision | Recall | p50 latency | p99 latency |
|-------------------|-------:|----------:|-------:|------------:|------------:|
| StyloExtract v1.3 | 0.718  | 0.718     | 0.800  | 8 ms       | 83 ms       |
| rs-trafilatura    | 0.859  | 0.863     | 0.890  | -           | -           |
| Trafilatura       | 0.791  | 0.852     | 0.793  | -           | -           |
| Readability       | 0.675  | 0.685     | 0.713  | -           | -           |

With-recall: 66.6% | Without-reject: 83.9%

### F1 by page type

| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |
|---------------|-------------:|--------:|------------:|------------:|
| Article (n=792) |        0.852 |   0.932 |       0.926 |       0.825 |
| Documentation (n=91) |        0.860 |   0.932 |       0.888 |       0.736 |
| Service (n=165) |        0.693 |   0.844 |       0.763 |       0.604 |
| Forum (n=112) |        0.456 |   0.808 |       0.585 |       0.466 |
| Collection (n=117) |        0.418 |   0.716 |       0.553 |       0.445 |
| Listing (n=99) |        0.510 |   0.707 |       0.589 |       0.496 |
| Product (n=119) |        0.465 |   0.641 |       0.567 |       0.407 |

### Latency detail

| Percentile | Latency |
|------------|--------:|
| p50        | 8 ms |
| p90        | 27 ms |
| p99        | 83 ms |
| max        | 783 ms |

Total pages processed: 1495 | Errors: 2 | Error rate: 0.1%
