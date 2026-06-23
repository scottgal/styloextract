# StyloExtract.Ml

ML block classifier for StyloExtract. Augments the heuristic classifier on
novel layouts (custom CSS frameworks, e-commerce SPAs) where the framework-
content-class-hints catalog has no entry.

**Phase 1 (this version)**: pure-C# AOT-clean per-element feature extractor.
No model is loaded; no ONNX runtime; the package exposes
`ElementFeatureExtractor` so consumers can dump features for training or
test the extraction surface.

**Phase 2+**: ONNX-runtime inference, trained model embedded as a resource,
`MlBlockClassifier` `IBlockClassifier` implementation, DI helper
`AddStyloExtractMl()`. Tracked in `docs/ml-classifier-v2-design.md`.
