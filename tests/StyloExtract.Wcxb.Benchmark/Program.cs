using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Wcxb.Benchmark;

// Baseline numbers from WCXB paper (dev split)
file static class Baselines
{
    public static readonly FrozenDictionary<string, (double F1, double Precision, double Recall)> Overall =
        new Dictionary<string, (double, double, double)>
        {
            ["rs-trafilatura"] = (0.859, 0.863, 0.890),
            ["Trafilatura"]    = (0.791, 0.852, 0.793),
            ["Readability"]    = (0.675, 0.685, 0.713),
        }.ToFrozenDictionary();

    // page-type rows: article, documentation, service, forum, collection, listing, product
    public static readonly FrozenDictionary<string, FrozenDictionary<string, double>> ByPageType =
        new Dictionary<string, FrozenDictionary<string, double>>
        {
            ["rs-trafilatura"] = new Dictionary<string, double>
            {
                ["article"]       = 0.932,
                ["documentation"] = 0.932,
                ["service"]       = 0.844,
                ["forum"]         = 0.808,
                ["collection"]    = 0.716,
                ["listing"]       = 0.707,
                ["product"]       = 0.641,
            }.ToFrozenDictionary(),
            ["Trafilatura"] = new Dictionary<string, double>
            {
                ["article"]       = 0.926,
                ["documentation"] = 0.888,
                ["service"]       = 0.763,
                ["forum"]         = 0.585,
                ["collection"]    = 0.553,
                ["listing"]       = 0.589,
                ["product"]       = 0.567,
            }.ToFrozenDictionary(),
            ["Readability"] = new Dictionary<string, double>
            {
                ["article"]       = 0.825,
                ["documentation"] = 0.736,
                ["service"]       = 0.604,
                ["forum"]         = 0.466,
                ["collection"]    = 0.445,
                ["listing"]       = 0.496,
                ["product"]       = 0.407,
            }.ToFrozenDictionary(),
        }.ToFrozenDictionary();
}

// Partial ground-truth JSON model
file sealed class GroundTruth
{
    [JsonPropertyName("main_content")]
    public string? MainContent { get; set; }

    [JsonPropertyName("with")]
    public List<string>? With { get; set; }

    [JsonPropertyName("without")]
    public List<string>? Without { get; set; }
}

file sealed class PageType
{
    [JsonPropertyName("primary")]
    public string? Primary { get; set; }
}

file sealed class Internal
{
    [JsonPropertyName("page_type")]
    public PageType? PageType { get; set; }
}

file sealed class WcxbDoc
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("_internal")]
    public Internal? Internal { get; set; }

    [JsonPropertyName("ground_truth")]
    public GroundTruth? GroundTruth { get; set; }
}

sealed record PageResult(
    string FileId,
    string PageType,
    double F1,
    double Precision,
    double Recall,
    double WithHit,
    double WithoutReject,
    long LatencyMs);

static partial class WordF1
{
    // Can't use GeneratedRegex in a file-local class, use a compiled instance instead.
    private static readonly Regex WordPattern = new(@"\w+", RegexOptions.Compiled);

    public static (double F1, double Precision, double Recall) Compute(string predicted, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return string.IsNullOrWhiteSpace(predicted) ? (1.0, 1.0, 1.0) : (0.0, 0.0, 0.0);

        if (string.IsNullOrWhiteSpace(predicted))
            return (0.0, 0.0, 0.0);

        var predMatches = WordPattern.Matches(predicted.ToLowerInvariant());
        var refMatches  = WordPattern.Matches(reference.ToLowerInvariant());

        var predCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in predMatches)
            predCount[m.Value] = predCount.GetValueOrDefault(m.Value) + 1;

        var refCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in refMatches)
            refCount[m.Value] = refCount.GetValueOrDefault(m.Value) + 1;

        int predTotal = predCount.Values.Sum();
        int refTotal  = refCount.Values.Sum();

        if (predTotal == 0 || refTotal == 0)
            return (0.0, 0.0, 0.0);

        int overlap = 0;
        foreach (var (word, pc) in predCount)
        {
            if (refCount.TryGetValue(word, out var rc))
                overlap += Math.Min(pc, rc);
        }

        double p = (double)overlap / predTotal;
        double r = (double)overlap / refTotal;
        double f1 = (p + r) > 0 ? 2 * p * r / (p + r) : 0.0;
        return (f1, p, r);
    }
}

static class Program
{
    static async Task<int> Main(string[] args)
    {
        // Parse CLI args
        string datasetPath = "/tmp/wcxb";
        string split       = "dev";
        string profile     = "MainContentOnly";
        int    maxPages    = int.MaxValue;
        string outPath     = "docs/wcxb.md";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dataset-path" when i + 1 < args.Length: datasetPath = args[++i]; break;
                case "--split"        when i + 1 < args.Length: split       = args[++i]; break;
                case "--profile"      when i + 1 < args.Length: profile     = args[++i]; break;
                case "--max-pages"    when i + 1 < args.Length: maxPages    = int.Parse(args[++i]); break;
                case "--out"          when i + 1 < args.Length: outPath     = args[++i]; break;
            }
        }

        if (!Enum.TryParse<ExtractionProfile>(profile, out var extractionProfile))
        {
            Console.Error.WriteLine($"Unknown profile '{profile}'. Valid: MainContentOnly, RagFull, AgentNavigation, DebugFull");
            return 1;
        }

        var gtDir   = Path.Combine(datasetPath, split, "ground-truth");
        var htmlDir = Path.Combine(datasetPath, split, "html");

        if (!Directory.Exists(gtDir) || !Directory.Exists(htmlDir))
        {
            Console.Error.WriteLine($"Dataset not found at {datasetPath}/{split}. Check --dataset-path.");
            return 1;
        }

        // Build the StyloExtract stack with an isolated temp DB so the benchmark
        // does not contaminate any production template store.
        var tmpDb = Path.Combine(Path.GetTempPath(), $"wcxb-bench-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddStyloExtract(o =>
            {
                o.StorePath = tmpDb;
                o.DefaultProfile = extractionProfile;
            });

            await using var provider = services.BuildServiceProvider();
            var extractor = provider.GetRequiredService<ILayoutExtractor>();

            var options = new ExtractionOptions
            {
                Profile = extractionProfile,
                LearnNewTemplates = true,
            };

            var gtFiles = Directory.GetFiles(gtDir, "*.json")
                .OrderBy(f => f)
                .Take(maxPages)
                .ToArray();

            Console.WriteLine($"WCXB benchmark: {split} split, profile={profile}, pages={gtFiles.Length}");
            Console.WriteLine($"Dataset: {datasetPath}");
            Console.WriteLine();

            var results  = new List<PageResult>(gtFiles.Length);
            int errors   = 0;
            var wallClock = Stopwatch.StartNew();

            for (int i = 0; i < gtFiles.Length; i++)
            {
                var gtFile = gtFiles[i];
                var fileId = Path.GetFileNameWithoutExtension(gtFile); // e.g. "0001"

                var htmlGz = Path.Combine(htmlDir, $"{fileId}.html.gz");
                if (!File.Exists(htmlGz))
                {
                    Console.Error.WriteLine($"page{fileId}: ERROR html.gz not found at {htmlGz}");
                    errors++;
                    continue;
                }

                WcxbDoc? doc;
                try
                {
                    await using var fs = File.OpenRead(gtFile);
                    doc = await JsonSerializer.DeserializeAsync<WcxbDoc>(fs);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"page{fileId}: ERROR parsing ground truth: {ex.Message}");
                    errors++;
                    continue;
                }

                if (doc?.GroundTruth?.MainContent is null)
                {
                    Console.Error.WriteLine($"page{fileId}: ERROR missing main_content");
                    errors++;
                    continue;
                }

                string html;
                try
                {
                    await using var fs = File.OpenRead(htmlGz);
                    await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    using var sr = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    html = await sr.ReadToEndAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"page{fileId}: ERROR reading html.gz: {ex.Message}");
                    errors++;
                    continue;
                }

                Uri? uri = null;
                if (doc.Url is not null)
                    Uri.TryCreate(doc.Url, UriKind.Absolute, out uri);

                string markdown;
                long latencyMs;
                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = await extractor.ExtractAsync(html, uri, options);
                    sw.Stop();
                    latencyMs = sw.ElapsedMilliseconds;
                    markdown = result.Markdown;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"page{fileId}: ERROR in ExtractAsync: {ex.Message}");
                    errors++;
                    continue;
                }

                var (f1, prec, rec) = WordF1.Compute(markdown, doc.GroundTruth.MainContent);

                // with[] recall: % of must-appear snippets found (case-folded substring)
                double withHit = 1.0;
                if (doc.GroundTruth.With is { Count: > 0 } withs)
                {
                    var mdLower = markdown.ToLowerInvariant();
                    int hits = withs.Count(s => mdLower.Contains(s.ToLowerInvariant()));
                    withHit = (double)hits / withs.Count;
                }

                // without[] rejection: % of must-NOT-appear snippets absent
                double withoutReject = 1.0;
                if (doc.GroundTruth.Without is { Count: > 0 } withouts)
                {
                    var mdLower = markdown.ToLowerInvariant();
                    int absent = withouts.Count(s => !mdLower.Contains(s.ToLowerInvariant()));
                    withoutReject = (double)absent / withouts.Count;
                }

                var pageType = doc.Internal?.PageType?.Primary ?? "unknown";
                results.Add(new PageResult(fileId, pageType, f1, prec, rec, withHit, withoutReject, latencyMs));

                if ((i + 1) % 100 == 0)
                    Console.WriteLine($"  {i + 1}/{gtFiles.Length} pages done... (errors so far: {errors})");
            }

            wallClock.Stop();

            if (results.Count == 0)
            {
                Console.Error.WriteLine("No results collected — all pages errored.");
                return 1;
            }

            var report = BuildReport(results, errors, split, profile, wallClock.Elapsed);
            Console.WriteLine(report);

            // Ensure output directory exists relative to CWD
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            await File.WriteAllTextAsync(outPath, report);
            Console.WriteLine($"\nReport written to: {outPath}");

            return 0;
        }
        finally
        {
            // Clean up temp DB
            foreach (var ext in new[] { "", "-shm", "-wal" })
            {
                var f = tmpDb + ext;
                if (File.Exists(f)) try { File.Delete(f); } catch { /* best-effort */ }
            }
        }
    }

    static string BuildReport(List<PageResult> results, int errors, string split, string profile, TimeSpan wallClock)
    {
        double Avg(IEnumerable<double> xs) { var l = xs.ToList(); return l.Count == 0 ? 0 : l.Average(); }
        long Percentile(IEnumerable<long> xs, int p)
        {
            var s = xs.OrderBy(x => x).ToArray();
            if (s.Length == 0) return 0;
            int i = (int)Math.Ceiling(p / 100.0 * s.Length) - 1;
            return s[Math.Clamp(i, 0, s.Length - 1)];
        }

        double overallF1   = Avg(results.Select(r => r.F1));
        double overallPrec = Avg(results.Select(r => r.Precision));
        double overallRec  = Avg(results.Select(r => r.Recall));
        double overallWith = Avg(results.Select(r => r.WithHit));
        double overallWo   = Avg(results.Select(r => r.WithoutReject));
        long   p50Ms       = Percentile(results.Select(r => r.LatencyMs), 50);
        long   p99Ms       = Percentile(results.Select(r => r.LatencyMs), 99);

        var sb = new StringBuilder();
        sb.AppendLine($"## StyloExtract heuristic v1.3 vs WCXB baselines ({split} split, profile={profile})");
        sb.AppendLine();
        sb.AppendLine($"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC | pages={results.Count} | errors={errors} | wall-clock={wallClock:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("| System            |     F1 | Precision | Recall | p50 latency | p99 latency |");
        sb.AppendLine("|-------------------|-------:|----------:|-------:|------------:|------------:|");
        sb.AppendLine($"| StyloExtract v1.3 | {overallF1:F3}  | {overallPrec:F3}     | {overallRec:F3}  | {p50Ms} ms       | {p99Ms} ms       |");

        foreach (var (sys, (bf1, bp, br)) in Baselines.Overall)
            sb.AppendLine($"| {sys,-17} | {bf1:F3}  | {bp:F3}     | {br:F3}  | -           | -           |");

        sb.AppendLine();
        sb.AppendLine($"With-recall: {overallWith:P1} | Without-reject: {overallWo:P1}");
        sb.AppendLine();

        // Per-page-type breakdown
        var pageTypes = new[] { "article", "documentation", "service", "forum", "collection", "listing", "product" };
        var byType    = results.GroupBy(r => r.PageType).ToDictionary(g => g.Key, g => g.ToList());

        sb.AppendLine("### F1 by page type");
        sb.AppendLine();
        sb.AppendLine("| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |");
        sb.AppendLine("|---------------|-------------:|--------:|------------:|------------:|");

        foreach (var pt in pageTypes)
        {
            double seF1 = byType.TryGetValue(pt, out var ptResults) ? Avg(ptResults.Select(r => r.F1)) : double.NaN;
            double rsTr = Baselines.ByPageType["rs-trafilatura"].GetValueOrDefault(pt, double.NaN);
            double traf  = Baselines.ByPageType["Trafilatura"].GetValueOrDefault(pt, double.NaN);
            double read  = Baselines.ByPageType["Readability"].GetValueOrDefault(pt, double.NaN);

            string seStr  = double.IsNaN(seF1)  ? "n/a" : seF1.ToString("F3");
            string rsStr  = double.IsNaN(rsTr)  ? "n/a" : rsTr.ToString("F3");
            string trStr  = double.IsNaN(traf)  ? "n/a" : traf.ToString("F3");
            string rdStr  = double.IsNaN(read)  ? "n/a" : read.ToString("F3");
            int    count  = byType.TryGetValue(pt, out var ptResults2) ? ptResults2.Count : 0;

            sb.AppendLine($"| {char.ToUpperInvariant(pt[0]) + pt[1..] + $" (n={count})",-13} | {seStr,12} | {rsStr,7} | {trStr,11} | {rdStr,11} |");
        }

        // Any other page types not in the canonical list
        foreach (var (pt, ptResults) in byType.Where(kv => !pageTypes.Contains(kv.Key)))
        {
            double seF1 = Avg(ptResults.Select(r => r.F1));
            sb.AppendLine($"| {char.ToUpperInvariant(pt[0]) + pt[1..] + $" (n={ptResults.Count})",-13} | {seF1,12:F3} | n/a     | n/a         | n/a         |");
        }

        sb.AppendLine();

        // Latency detail
        sb.AppendLine("### Latency detail");
        sb.AppendLine();
        sb.AppendLine("| Percentile | Latency |");
        sb.AppendLine("|------------|--------:|");
        sb.AppendLine($"| p50        | {p50Ms} ms |");
        sb.AppendLine($"| p90        | {Percentile(results.Select(r => r.LatencyMs), 90)} ms |");
        sb.AppendLine($"| p99        | {p99Ms} ms |");
        sb.AppendLine($"| max        | {results.Max(r => r.LatencyMs)} ms |");
        sb.AppendLine();
        sb.AppendLine($"Total pages processed: {results.Count} | Errors: {errors} | Error rate: {(results.Count + errors > 0 ? (double)errors / (results.Count + errors) : 0):P1}");

        return sb.ToString();
    }
}
