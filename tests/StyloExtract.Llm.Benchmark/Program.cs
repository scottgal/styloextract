using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Html;
using StyloExtract.Llm.LlamaSharp;
using StyloExtract.Llm.Ollama;

namespace StyloExtract.Llm.Benchmark;

/// <summary>
/// Compare template induction quality across LLM models on a fixed page set.
///
/// <para>
/// For each (model, page) pair the harness: trains a template via
/// LlmTemplateInducer (Ollama backend), saves the YAML to a per-model
/// operator-template root, then runs a fresh extractor configured with
/// AddStyloExtractOperatorTemplates pointing at that root. The resulting
/// markdown is word-F1'd against the WCXB gold's main_content. Both F1
/// and train-time-per-page are reported.
/// </para>
///
/// <para>
/// Usage:
/// <code>
/// dotnet run --project tests/StyloExtract.Llm.Benchmark -c Release -- \
///   --ollama-url http://localhost:11434 \
///   --models qwen3.5:0.8b,qwen3.5:4b,gemma4:e2b \
///   --wcxb-path /tmp/wcxb \
///   --page-ids 4690,4459,0660,0203,4349 \
///   --out docs/model-bench.md
/// </code>
/// </para>
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string ollamaUrl = "http://localhost:11434";
        string models = "qwen3.5:0.8b,qwen3.5:4b,gemma4:e2b";
        string wcxbPath = "/tmp/wcxb";
        string split = "dev";
        string pageIds = "";
        string outPath = "docs/model-bench.md";
        int timeoutSec = 300;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ollama-url" when i + 1 < args.Length: ollamaUrl = args[++i]; break;
                case "--models" when i + 1 < args.Length: models = args[++i]; break;
                case "--wcxb-path" when i + 1 < args.Length: wcxbPath = args[++i]; break;
                case "--split" when i + 1 < args.Length: split = args[++i]; break;
                case "--page-ids" when i + 1 < args.Length: pageIds = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--timeout-sec" when i + 1 < args.Length: timeoutSec = int.Parse(args[++i]); break;
            }
        }

        if (string.IsNullOrWhiteSpace(pageIds))
        {
            Console.Error.WriteLine("--page-ids required (comma-separated WCXB file ids, e.g. 4690,4459,0660)");
            return 2;
        }

        var modelList = models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        var modelDisplayNames = new string[modelList.Length];
        var ids = pageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();

        var gtDir = Path.Combine(wcxbPath, split, "ground-truth");
        var htmlDir = Path.Combine(wcxbPath, split, "html");
        if (!Directory.Exists(gtDir) || !Directory.Exists(htmlDir))
        {
            Console.Error.WriteLine($"WCXB dataset not found at {wcxbPath}/{split}.");
            return 1;
        }

        // Load fixtures: (id, host, html, gold, pageType) per page.
        var fixtures = new List<Fixture>();
        foreach (var id in ids)
        {
            var gtFile = Path.Combine(gtDir, $"{id}.json");
            var htmlGz = Path.Combine(htmlDir, $"{id}.html.gz");
            if (!File.Exists(gtFile) || !File.Exists(htmlGz))
            {
                Console.Error.WriteLine($"page {id}: missing ground-truth or html.gz");
                continue;
            }
            await using var fs = File.OpenRead(gtFile);
            var doc = await JsonSerializer.DeserializeAsync<WcxbDoc>(fs);
            if (doc?.GroundTruth?.MainContent is null || doc.Url is null) continue;
            await using var fsHtml = File.OpenRead(htmlGz);
            await using var gz = new GZipStream(fsHtml, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            var html = await sr.ReadToEndAsync();
            fixtures.Add(new Fixture(
                Id: id,
                Url: doc.Url,
                Host: new Uri(doc.Url).Host,
                Html: html,
                Gold: doc.GroundTruth.MainContent,
                PageType: doc.Internal?.PageType?.Primary ?? "unknown"));
        }
        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine("no usable fixtures loaded");
            return 1;
        }

        // Run the matrix. Results[modelIdx, fixtureIdx] = (F1, TrainSeconds, MarkdownLen).
        var results = new Result[modelList.Length, fixtures.Count];
        Console.WriteLine($"Model bench: {modelList.Length} models × {fixtures.Count} fixtures = {modelList.Length * fixtures.Count} runs");
        Console.WriteLine();

        for (int mi = 0; mi < modelList.Length; mi++)
        {
            var model = modelList[mi];
            Console.WriteLine($"=== model: {model} ===");
            var templateRoot = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}");
            Directory.CreateDirectory(templateRoot);

            // Model spec routing: `llamasharp:/path/to/file.gguf` runs
            // in-process via LLamaSharp; anything else hits Ollama. Lets the
            // same bench compare both backends side-by-side.
            var llmServices = new ServiceCollection();
            string displayName;
            if (model.StartsWith("llamasharp:", StringComparison.OrdinalIgnoreCase))
            {
                var ggufPath = model["llamasharp:".Length..];
                displayName = "llamasharp:" + Path.GetFileNameWithoutExtension(ggufPath);
                llmServices.AddStyloExtractLlamaSharp(o =>
                {
                    o.ModelPath = ggufPath;
                    o.ContextSize = 8192;
                    o.Timeout = TimeSpan.FromSeconds(timeoutSec);
                });
            }
            else
            {
                displayName = model;
                llmServices.AddOptions<OllamaTextProviderOptions>().Configure(o =>
                {
                    o.OllamaUrl = ollamaUrl;
                    o.Model = model;
                    o.Timeout = TimeSpan.FromSeconds(timeoutSec);
                });
                llmServices.AddOllamaTextProvider();
            }
            using var llmSp = llmServices.BuildServiceProvider();
            var llm = llmSp.GetRequiredService<ILlmTextProvider>();
            var inducer = new LlmTemplateInducer(llm);
            modelDisplayNames[mi] = displayName;

            // Phase 1: train one template per fixture.
            for (int fi = 0; fi < fixtures.Count; fi++)
            {
                var f = fixtures[fi];
                Console.Write($"  train {f.Id} ({f.Host}) … ");
                var doc = LoadAndCleanDocument(f.Html);
                var skeleton = new DomSkeletonRenderer().Render(doc);
                var catalog = new DocumentSelectorCatalog().Render(doc);

                var sw = Stopwatch.StartNew();
                OperatorTemplate? template;
                try
                {
                    template = await inducer.InduceFromSkeletonAsync(
                        skeleton, f.Host, availableSelectors: catalog, document: doc);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Console.WriteLine($"FAIL ({ex.GetType().Name}: {ex.Message})");
                    results[mi, fi] = new Result(F1: 0, TrainSeconds: sw.Elapsed.TotalSeconds, MarkdownLen: 0, Notes: "train threw");
                    continue;
                }
                sw.Stop();
                if (template is null)
                {
                    Console.WriteLine($"FAIL (train returned null after {sw.Elapsed.TotalSeconds:F1}s)");
                    results[mi, fi] = new Result(F1: 0, TrainSeconds: sw.Elapsed.TotalSeconds, MarkdownLen: 0, Notes: "train returned null");
                    continue;
                }
                var yaml = OperatorTemplateYamlEmitter.Emit(template);
                await File.WriteAllTextAsync(Path.Combine(templateRoot, f.Host + ".yaml"), yaml);
                Console.WriteLine($"ok in {sw.Elapsed.TotalSeconds:F1}s ({template.Rules.Count} rule(s))");
                results[mi, fi] = new Result(F1: 0, TrainSeconds: sw.Elapsed.TotalSeconds, MarkdownLen: 0, Notes: "");
            }

            // Phase 2: extract every fixture once with this model's template root.
            var tmpDb = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.db");
            var extServices = new ServiceCollection();
            extServices.AddStyloExtract(o =>
            {
                o.StorePath = tmpDb;
                o.DefaultProfile = ExtractionProfile.Wcxb;
            });
            extServices.AddStyloExtractOperatorTemplates(templateRoot);
            await using var extSp = extServices.BuildServiceProvider();
            var extractor = extSp.GetRequiredService<ILayoutExtractor>();

            for (int fi = 0; fi < fixtures.Count; fi++)
            {
                var f = fixtures[fi];
                if (results[mi, fi].Notes != "") continue; // train failed; skip extract
                try
                {
                    var r = await extractor.ExtractAsync(f.Html, new Uri(f.Url),
                        new ExtractionOptions { Profile = ExtractionProfile.Wcxb });
                    var (f1, _, _) = WordF1.Compute(r.Markdown, f.Gold);
                    results[mi, fi] = results[mi, fi] with { F1 = f1, MarkdownLen = r.Markdown.Length };
                }
                catch (Exception ex)
                {
                    results[mi, fi] = results[mi, fi] with { Notes = "extract threw: " + ex.GetType().Name };
                }
            }

            // Cleanup
            try { Directory.Delete(templateRoot, recursive: true); } catch { }
            foreach (var ext in new[] { "", "-shm", "-wal" })
            {
                var f = tmpDb + ext;
                if (File.Exists(f)) try { File.Delete(f); } catch { }
            }
            Console.WriteLine();
        }

        // Print + write report.
        var report = BuildReport(modelDisplayNames, fixtures, results, ollamaUrl);
        Console.WriteLine(report);
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outPath, report);
        Console.WriteLine($"Report written to {outPath}");
        return 0;
    }

    private static AngleSharp.Dom.IDocument LoadAndCleanDocument(string html)
    {
        var doc = new AngleSharpHtmlDomParser().Parse(html);
        new DomCleaner().Clean(doc);
        return doc;
    }

    private static string BuildReport(string[] models, List<Fixture> fixtures, Result[,] results, string ollamaUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# LLM model benchmark — template induction quality");
        sb.AppendLine();
        sb.AppendLine($"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC  |  Backend: {ollamaUrl}");
        sb.AppendLine($"Models: {string.Join(", ", models)}");
        sb.AppendLine($"Fixtures: {fixtures.Count} pages from WCXB dev split");
        sb.AppendLine();

        // F1 matrix
        sb.AppendLine("## F1 by model × page");
        sb.AppendLine();
        sb.Append("| Page (host) |");
        foreach (var m in models) sb.Append($" {m} |");
        sb.AppendLine();
        sb.Append("|---|");
        for (int i = 0; i < models.Length; i++) sb.Append("---:|");
        sb.AppendLine();
        for (int fi = 0; fi < fixtures.Count; fi++)
        {
            var f = fixtures[fi];
            sb.Append($"| {f.Id} ({f.Host}) |");
            for (int mi = 0; mi < models.Length; mi++)
            {
                var r = results[mi, fi];
                if (r.Notes != "")
                    sb.Append($" — |");
                else
                    sb.Append($" {r.F1:F3} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        // Average F1 per model
        sb.AppendLine("## Aggregate");
        sb.AppendLine();
        sb.AppendLine("| Model | Avg F1 | Avg train sec | Avg markdown chars |");
        sb.AppendLine("|---|---:|---:|---:|");
        for (int mi = 0; mi < models.Length; mi++)
        {
            double f1sum = 0, tsum = 0;
            long lensum = 0;
            int count = 0;
            for (int fi = 0; fi < fixtures.Count; fi++)
            {
                var r = results[mi, fi];
                if (r.Notes != "") continue;
                f1sum += r.F1;
                tsum += r.TrainSeconds;
                lensum += r.MarkdownLen;
                count++;
            }
            var avgF1 = count > 0 ? f1sum / count : 0;
            var avgT = count > 0 ? tsum / count : 0;
            var avgLen = count > 0 ? lensum / count : 0;
            sb.AppendLine($"| {models[mi]} | {avgF1:F3} | {avgT:F1} | {avgLen} |");
        }
        sb.AppendLine();

        // Train time matrix
        sb.AppendLine("## Train seconds by model × page");
        sb.AppendLine();
        sb.Append("| Page |");
        foreach (var m in models) sb.Append($" {m} |");
        sb.AppendLine();
        sb.Append("|---|");
        for (int i = 0; i < models.Length; i++) sb.Append("---:|");
        sb.AppendLine();
        for (int fi = 0; fi < fixtures.Count; fi++)
        {
            sb.Append($"| {fixtures[fi].Id} |");
            for (int mi = 0; mi < models.Length; mi++)
                sb.Append($" {results[mi, fi].TrainSeconds:F1} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private sealed record Fixture(string Id, string Url, string Host, string Html, string Gold, string PageType);
    private sealed record Result(double F1, double TrainSeconds, int MarkdownLen, string Notes)
    {
        public Result() : this(0, 0, 0, "") { }
    }

    // WCXB ground-truth JSON shape (subset).
    private sealed class WcxbDoc
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("_internal")] public Internal? Internal { get; set; }
        [JsonPropertyName("ground_truth")] public GroundTruth? GroundTruth { get; set; }
    }
    private sealed class Internal
    {
        [JsonPropertyName("page_type")] public PageType? PageType { get; set; }
    }
    private sealed class PageType
    {
        [JsonPropertyName("primary")] public string? Primary { get; set; }
    }
    private sealed class GroundTruth
    {
        [JsonPropertyName("main_content")] public string? MainContent { get; set; }
    }
}

internal static class WordF1
{
    private static readonly Regex WordPattern = new(@"\w+", RegexOptions.Compiled);

    public static (double F1, double Precision, double Recall) Compute(string predicted, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return string.IsNullOrWhiteSpace(predicted) ? (1.0, 1.0, 1.0) : (0.0, 0.0, 0.0);
        if (string.IsNullOrWhiteSpace(predicted))
            return (0.0, 0.0, 0.0);

        var predCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in WordPattern.Matches(predicted.ToLowerInvariant()))
            predCount[m.Value] = predCount.GetValueOrDefault(m.Value) + 1;

        var refCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in WordPattern.Matches(reference.ToLowerInvariant()))
            refCount[m.Value] = refCount.GetValueOrDefault(m.Value) + 1;

        int predTotal = predCount.Values.Sum();
        int refTotal = refCount.Values.Sum();
        if (predTotal == 0 || refTotal == 0) return (0.0, 0.0, 0.0);

        int overlap = 0;
        foreach (var (w, pc) in predCount)
            if (refCount.TryGetValue(w, out var rc))
                overlap += Math.Min(pc, rc);
        double p = (double)overlap / predTotal;
        double r = (double)overlap / refTotal;
        double f1 = (p + r) > 0 ? 2 * p * r / (p + r) : 0.0;
        return (f1, p, r);
    }
}
