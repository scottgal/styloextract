# WCXB training evidence — Gemma 4 e4b on 192.168.0.15

YAML templates produced by `stylo-extract template train` against catastrophic
WCXB pages where the pure-heuristic extraction emitted ≤ 5 characters of
MainContent. Each was induced from the page's slim DOM skeleton + a closed
catalog of real CSS selectors (no hallucination), validated against the live
AngleSharp DOM (selectors that matched zero elements were dropped), and
written here in canonical form.

| Page | Host | Before | After |
|---|---|---:|---:|
| 0203 | interiordesign.net | 0.000 | 0.481 |
| 0205 | wral.com | 0.000 | 0.671 |
| 0259 | hospitalitynet.org | 0.000 | 0.401 |
| 0660 | mybirdbuddy.eu (collection) | 0.000 | 0.806 |
| 2936 | thomann.de | 0.000 | 0.738 |
| 4022 | liteaf.com | 0.000 | 0.756 |
| 4065 | thehumansolution.com | 0.000 | 0.145 |
| 4112 | whiteflowerfarm.com | 0.000 | 0.604 |
| 4163 | kingarthurbaking.com | 0.000 | 0.869 |
| 4459 | fermyon.com | 0.000 | **0.995** |
| 4206 | activeendeavors.com | 0.000 | 0.560 |
| 4349 | vims.edu | 0.000 | 0.219 |
| 4657 | capitalgroup.com | 0.000 | 0.719 |
| 4690 | wsetglobal.com | 0.000 | 0.068 (over-extracts) |
| 0601 | mybirdbuddy.eu (product, via repair) | 0.336 | 0.431 |

Aggregate WCXB Wcxb-mode: F1 0.751 → 0.755, catastrophic (pred_chars ≤ 5)
43 → 32.

The templates here are CONTENT-DERIVED PROOF, not shipped configuration.
Production deploys train against their own content via the same CLI.
