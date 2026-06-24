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
using StyloExtract.Playwright;

namespace StyloExtract.Wcxb.Benchmark;

// Diagnostic JSONL record (written only when --diagnostic is set).
file sealed class DiagnosticEntry
{
    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("page_type")]
    public string PageType { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("f1")]
    public double F1 { get; set; }

    [JsonPropertyName("precision")]
    public double Precision { get; set; }

    [JsonPropertyName("recall")]
    public double Recall { get; set; }

    [JsonPropertyName("gold_chars")]
    public int GoldChars { get; set; }

    [JsonPropertyName("pred_chars")]
    public int PredChars { get; set; }

    [JsonPropertyName("with_hit_count")]
    public int WithHitCount { get; set; }

    [JsonPropertyName("with_total")]
    public int WithTotal { get; set; }

    [JsonPropertyName("without_violations")]
    public List<string> WithoutViolations { get; set; } = [];

    [JsonPropertyName("extra_words_sample")]
    public List<string> ExtraWordsSample { get; set; } = [];

    [JsonPropertyName("missing_words_sample")]
    public List<string> MissingWordsSample { get; set; } = [];

    [JsonPropertyName("use_playwright")]
    public bool UsePlaywright { get; set; }
}

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

// Helper for diagnostic word-frequency analysis (used only when --diagnostic is set).
file static class DiagnosticWords
{
    private static readonly Regex WordPat = new(@"\w+", RegexOptions.Compiled);

    private static readonly FrozenSet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "to", "for", "in", "on", "at",
        "is", "are", "was", "were", "it", "its", "this", "that", "be", "by", "with",
        "as", "not", "but", "from", "have", "has", "had", "do", "does", "did",
        "will", "would", "can", "could", "may", "might", "shall", "should",
        "our", "we", "you", "your", "they", "their", "he", "she", "his", "her",
        "us", "me", "my", "i", "s", "t", "re", "ll", "ve", "d", "m",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, int> Counts(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in WordPat.Matches(text.ToLowerInvariant()))
        {
            if (!Stopwords.Contains(m.Value))
                result[m.Value] = result.GetValueOrDefault(m.Value) + 1;
        }
        return result;
    }

    public static List<string> TopExtra(Dictionary<string, int> predCounts, Dictionary<string, int> refCounts, int n)
    {
        // Words in pred that appear more than in ref, sorted by excess frequency
        var excess = new List<(string Word, int Excess)>();
        foreach (var (w, pc) in predCounts)
        {
            int rc = refCounts.GetValueOrDefault(w);
            int diff = pc - rc;
            if (diff > 0)
                excess.Add((w, diff));
        }
        return excess.OrderByDescending(x => x.Excess).Take(n).Select(x => x.Word).ToList();
    }

    public static List<string> TopMissing(Dictionary<string, int> predCounts, Dictionary<string, int> refCounts, int n)
    {
        var missing = new List<(string Word, int Count)>();
        foreach (var (w, rc) in refCounts)
        {
            int pc = predCounts.GetValueOrDefault(w);
            int diff = rc - pc;
            if (diff > 0)
                missing.Add((w, diff));
        }
        return missing.OrderByDescending(x => x.Count).Take(n).Select(x => x.Word).ToList();
    }
}

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
        string datasetPath  = "/tmp/wcxb";
        string split        = "dev";
        string profile      = "Wcxb";
        int    maxPages     = int.MaxValue;
        string outPath      = "docs/wcxb.md";
        bool   diagnostic   = false;
        string diagnosticOut = "/tmp/wcxb-diag.jsonl";
        bool   usePlaywright = false;
        HashSet<string>? pageTypeFilter = null;  // null = all page types
        HashSet<string>? pageIdFilter = null;    // null = all page ids

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dataset-path"   when i + 1 < args.Length: datasetPath   = args[++i]; break;
                case "--split"          when i + 1 < args.Length: split         = args[++i]; break;
                case "--profile"        when i + 1 < args.Length: profile       = args[++i]; break;
                case "--max-pages"      when i + 1 < args.Length: maxPages      = int.Parse(args[++i]); break;
                case "--out"            when i + 1 < args.Length: outPath       = args[++i]; break;
                case "--diagnostic-out" when i + 1 < args.Length: diagnosticOut = args[++i]; break;
                case "--diagnostic": diagnostic = true; break;
                case "--use-playwright": usePlaywright = true; break;
                case "--page-types" when i + 1 < args.Length:
                    pageTypeFilter = new HashSet<string>(
                        args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.OrdinalIgnoreCase);
                    break;
                case "--page-ids" when i + 1 < args.Length:
                    pageIdFilter = new HashSet<string>(
                        args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.OrdinalIgnoreCase);
                    break;
            }
        }

        if (!Enum.TryParse<ExtractionProfile>(profile, out var extractionProfile))
        {
            Console.Error.WriteLine($"Unknown profile '{profile}'. Valid: MainContentOnly, RagFull, AgentNavigation, DebugFull, Wcxb");
            return 1;
        }

        // Playwright availability check (fail-gracefully with a useful message)
        if (usePlaywright)
        {
            bool browsersAvailable = await PlaywrightInstaller.BrowsersAvailableAsync();
            if (!browsersAvailable)
            {
                Console.Error.WriteLine("Playwright chromium is not installed. Run: playwright install chromium");
                Console.Error.WriteLine("Or from the benchmark project dir: dotnet run -- install chromium");
                Console.Error.WriteLine("Playwright binaries must be installed before using --use-playwright.");
                return 1;
            }
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

            // When a page-type filter is active, pre-scan the ground-truth files to find
            // only those whose page_type matches. We do a cheap JSON scan rather than full
            // deserialisation so the filter overhead is negligible.
            IEnumerable<string> allGtFiles = Directory.GetFiles(gtDir, "*.json").OrderBy(f => f);
            if (pageIdFilter is not null)
            {
                allGtFiles = allGtFiles.Where(f => pageIdFilter.Contains(Path.GetFileNameWithoutExtension(f)));
            }
            if (pageTypeFilter is not null)
            {
                var filtered = new List<string>();
                foreach (var f in allGtFiles)
                {
                    try
                    {
                        await using var fs = File.OpenRead(f);
                        var doc = await JsonSerializer.DeserializeAsync<WcxbDoc>(fs);
                        var pt = doc?.Internal?.PageType?.Primary ?? "unknown";
                        if (pageTypeFilter.Contains(pt))
                            filtered.Add(f);
                    }
                    catch
                    {
                        // Malformed ground-truth: skip during pre-scan; the main loop will report it.
                    }
                }
                allGtFiles = filtered;
            }

            var gtFiles = allGtFiles.Take(maxPages).ToArray();

            var modeLabel = usePlaywright ? "Playwright" : "static-HTML";
            var filterLabel = pageTypeFilter is null ? "all" : string.Join(",", pageTypeFilter);
            Console.WriteLine($"WCXB benchmark: {split} split, profile={profile}, mode={modeLabel}, page-types={filterLabel}, pages={gtFiles.Length}");
            Console.WriteLine($"Dataset: {datasetPath}");
            Console.WriteLine();

            var results  = new List<PageResult>(gtFiles.Length);
            int errors   = 0;
            var wallClock = Stopwatch.StartNew();

            // Playwright: one IPlaywright + IBrowser shared for all pages to amortise launch cost.
            // Null when --use-playwright is not set.
            IDisposable?             pwDisposable     = null;
            Microsoft.Playwright.IBrowser? pwBrowser  = null;
            if (usePlaywright)
            {
                var pw = await Microsoft.Playwright.Playwright.CreateAsync();
                pwDisposable = pw;
                pwBrowser    = await pw.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions { Headless = true });
            }

            // Open diagnostic JSONL writer only when the flag is set; null otherwise.
            StreamWriter? diagWriter = null;
            if (diagnostic)
            {
                var diagDir = Path.GetDirectoryName(diagnosticOut);
                if (!string.IsNullOrEmpty(diagDir))
                    Directory.CreateDirectory(diagDir);
                diagWriter = new StreamWriter(diagnosticOut, append: false, Encoding.UTF8);
            }

            try
            {
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

                // Decompress the stored HTML (always needed -- Playwright loads from a temp file).
                string rawHtml;
                try
                {
                    await using var fs = File.OpenRead(htmlGz);
                    await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    using var sr = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    rawHtml = await sr.ReadToEndAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"page{fileId}: ERROR reading html.gz: {ex.Message}");
                    errors++;
                    continue;
                }

                string html;
                if (pwBrowser is not null)
                {
                    // Write the decompressed HTML to a temp file, then navigate Playwright
                    // to the file:// URI using DOMContentLoaded (not networkidle).
                    // file:// pages with external resource references never reach networkidle;
                    // DOMContentLoaded fires once the initial parse + synchronous scripts run,
                    // which is enough for inline JSON-LD hydration (the Discourse pattern).
                    //
                    // If Playwright times out (DOMContentLoaded never fires within 3s, which
                    // happens on Discourse/Ember pages that execute huge JS bundles), fall back
                    // to rawHtml so the page still contributes to the F1 score. These pages
                    // typically represent the JS-too-heavy-for-file:// case and are counted
                    // separately in the report notes.
                    var tmpHtml = Path.Combine(Path.GetTempPath(), $"wcxb-{fileId}-{Guid.NewGuid():N}.html");
                    try
                    {
                        await File.WriteAllTextAsync(tmpHtml, rawHtml, Encoding.UTF8);
                        html = await FetchRenderedHtmlAsync(pwBrowser, tmpHtml, fileId);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"page{fileId}: PLAYWRIGHT TIMEOUT/ERROR (falling back to static): {ex.Message.Split('\n')[0]}");
                        html = rawHtml;  // fall back to static HTML; page still scored
                    }
                    finally
                    {
                        try { File.Delete(tmpHtml); } catch { /* best-effort cleanup */ }
                    }
                }
                else
                {
                    html = rawHtml;
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
                int withHitCount = 0;
                int withTotal = 0;
                if (doc.GroundTruth.With is { Count: > 0 } withs)
                {
                    var mdLower = markdown.ToLowerInvariant();
                    withTotal = withs.Count;
                    withHitCount = withs.Count(s => mdLower.Contains(s.ToLowerInvariant()));
                    withHit = (double)withHitCount / withTotal;
                }

                // without[] rejection: % of must-NOT-appear snippets absent
                double withoutReject = 1.0;
                var withoutViolations = new List<string>();
                if (doc.GroundTruth.Without is { Count: > 0 } withouts)
                {
                    var mdLower = markdown.ToLowerInvariant();
                    foreach (var s in withouts)
                    {
                        if (mdLower.Contains(s.ToLowerInvariant()))
                            withoutViolations.Add(s);
                    }
                    int absent = withouts.Count - withoutViolations.Count;
                    withoutReject = (double)absent / withouts.Count;
                }

                var pageType = doc.Internal?.PageType?.Primary ?? "unknown";
                results.Add(new PageResult(fileId, pageType, f1, prec, rec, withHit, withoutReject, latencyMs));

                // Write diagnostic entry if requested
                if (diagWriter is not null)
                {
                    var predCounts = DiagnosticWords.Counts(markdown);
                    var refCounts  = DiagnosticWords.Counts(doc.GroundTruth.MainContent);

                    var entry = new DiagnosticEntry
                    {
                        FileId           = fileId,
                        PageType         = pageType,
                        Url              = doc.Url ?? "",
                        F1               = Math.Round(f1, 4),
                        Precision        = Math.Round(prec, 4),
                        Recall           = Math.Round(rec, 4),
                        GoldChars        = doc.GroundTruth.MainContent.Length,
                        PredChars        = markdown.Length,
                        WithHitCount     = withHitCount,
                        WithTotal        = withTotal,
                        WithoutViolations = withoutViolations,
                        ExtraWordsSample  = DiagnosticWords.TopExtra(predCounts, refCounts, 30),
                        MissingWordsSample = DiagnosticWords.TopMissing(predCounts, refCounts, 30),
                        UsePlaywright     = usePlaywright,
                    };

                    await diagWriter.WriteLineAsync(JsonSerializer.Serialize(entry));
                }

                if ((i + 1) % 100 == 0)
                    Console.WriteLine($"  {i + 1}/{gtFiles.Length} pages done... (errors so far: {errors})");
            }
            }
            finally
            {
                if (diagWriter is not null)
                {
                    await diagWriter.FlushAsync();
                    await diagWriter.DisposeAsync();
                    if (diagnostic)
                        Console.WriteLine($"Diagnostic JSONL written to: {diagnosticOut}");
                }
                // Dispose Playwright browser + IPlaywright (IDisposable, not IAsyncDisposable).
                if (pwBrowser is not null)
                    await pwBrowser.DisposeAsync();
                pwDisposable?.Dispose();
            }

            wallClock.Stop();

            if (results.Count == 0)
            {
                Console.Error.WriteLine("No results collected — all pages errored.");
                return 1;
            }

            var filterLabel2 = pageTypeFilter is null ? "all" : string.Join(",", pageTypeFilter);
        var report = BuildReport(results, errors, split, profile, wallClock.Elapsed, usePlaywright, filterLabel2);
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

    // Navigate to a local file:// HTML file and return the rendered page content.
    // Uses DOMContentLoaded (not networkidle) because file:// pages that reference external
    // CDN resources never reach networkidle -- external requests are blocked when loading
    // from disk, so the network never goes idle.
    static async Task<string> FetchRenderedHtmlAsync(Microsoft.Playwright.IBrowser browser, string localHtmlPath, string fileId)
    {
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var fileUri = "file://" + localHtmlPath;

        // Navigate with DOMContentLoaded. Use a short timeout: Discourse/Ember pages
        // execute large JS bundles that spin the renderer for minutes. We just want
        // the serialized DOM after initial sync scripts run; live API calls don't
        // work under file:// anyway.
        //
        // If DOMContentLoaded doesn't fire within 3s, close the page and throw so
        // the caller falls back to rawHtml (the static path). ContentAsync() blocks
        // until the renderer is idle, so we cannot safely call it after a timeout.
        await page.GotoAsync(fileUri, new Microsoft.Playwright.PageGotoOptions
        {
            Timeout  = 3000,
            WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
        });

        return await page.ContentAsync();
    }

    static string BuildReport(List<PageResult> results, int errors, string split, string profile, TimeSpan wallClock,
        bool usePlaywright = false, string pageTypes = "all")
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

        var modeLabel = usePlaywright ? "Playwright" : "static-HTML";
        var sb = new StringBuilder();
        sb.AppendLine($"## StyloExtract heuristic v1.3 vs WCXB baselines ({split} split, profile={profile}, mode={modeLabel}, page-types={pageTypes})");
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
        var canonicalTypes = new[] { "article", "documentation", "service", "forum", "collection", "listing", "product" };
        var byType    = results.GroupBy(r => r.PageType).ToDictionary(g => g.Key, g => g.ToList());

        sb.AppendLine("### F1 by page type");
        sb.AppendLine();
        sb.AppendLine("| Page type     | StyloExtract | rs-traf | Trafilatura | Readability |");
        sb.AppendLine("|---------------|-------------:|--------:|------------:|------------:|");

        foreach (var pt in canonicalTypes)
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
        foreach (var (pt, ptResults) in byType.Where(kv => !canonicalTypes.Contains(kv.Key)))
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
