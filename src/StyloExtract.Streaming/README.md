# StyloExtract.Streaming

Hot-path streaming fence scanner. Skips page chrome and captures the content region as response bytes flow past, using MinHash-derived structural fences. Zero per-request GC-tracked allocations in steady state.

Built for the gateway position — drop into Stylobot's response pipeline; the scanner runs alongside the byte stream and emits the captured content region without buffering the full page.

Pairs with the existing `StyloExtract.Fingerprint` learn path and `ITemplateIndex` template store.
