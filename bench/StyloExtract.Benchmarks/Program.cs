using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

// Spec §13 latency targets (p99, ms):
// Fast-path match:     15ms  (FastPathHitTargetMs=1ms applies to pure cache probe, not full ExtractAsync)
// Slow-path match:     30ms
// Novel registration:  50ms
const double FastPathMatchTargetMs = 15.0;
const double SlowPathMatchTargetMs = 30.0;
const double NovelTargetMs = 50.0;

if (args.Length > 0 && args[0] == "--regression")
{
    // Regression mode: run all benches with a short warmup + few iterations and
    // fail with non-zero exit code if any p99 exceeds spec §13 targets.
    return await RunRegressionAsync();
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;

static async Task<int> RunRegressionAsync()
{
    Console.WriteLine("=== StyloExtract Benchmark Regression Mode (spec §13) ===");
    Console.WriteLine();

    var html = File.ReadAllText("article.html");
    var errors = new List<string>();
    const int Iterations = 20;

    // --- Fast-path cache hit ---
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var uri = new Uri("https://bench-regression.example.com/cache-hit");
        await extractor.ExtractAsync(html, uri); // warm

        var times = new List<double>(Iterations);
        for (int i = 0; i < Iterations; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await extractor.ExtractAsync(html, uri);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var p99 = Percentile(times, 0.99);
        var status = p99 <= FastPathMatchTargetMs ? "PASS" : "FAIL";
        Console.WriteLine($"FastPath match (warm):  p99={p99:F2}ms  target<={FastPathMatchTargetMs}ms  [{status}]");
        if (status == "FAIL")
            errors.Add($"FastPath match p99={p99:F2}ms exceeds target {FastPathMatchTargetMs}ms");
    }

    // --- Novel registration ---
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        var extractor = sp.GetRequiredService<ILayoutExtractor>();

        var times = new List<double>(Iterations);
        for (int i = 0; i < Iterations; i++)
        {
            var uri = new Uri($"https://novel-regression-{i}.example.com/page");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await extractor.ExtractAsync(html, uri);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var p99 = Percentile(times, 0.99);
        var status = p99 <= NovelTargetMs ? "PASS" : "FAIL";
        Console.WriteLine($"Novel registration:     p99={p99:F2}ms  target<={NovelTargetMs}ms  [{status}]");
        if (status == "FAIL")
            errors.Add($"Novel registration p99={p99:F2}ms exceeds target {NovelTargetMs}ms");
    }

    // --- Slow-path match ---
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var uri = new Uri("https://bench-regression-slow.example.com/page");
        await extractor.ExtractAsync(html, uri); // register base

        var perturbed = html.Replace(
            "<footer>",
            "<section><p>" + string.Concat(Enumerable.Repeat("slow path perturb test content ", 50)) + "</p></section>\n<footer>");

        var times = new List<double>(Iterations);
        for (int i = 0; i < Iterations; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await extractor.ExtractAsync(perturbed, uri);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var p99 = Percentile(times, 0.99);
        var status = p99 <= SlowPathMatchTargetMs ? "PASS" : "FAIL";
        Console.WriteLine($"Slow-path match:        p99={p99:F2}ms  target<={SlowPathMatchTargetMs}ms  [{status}]");
        if (status == "FAIL")
            errors.Add($"Slow-path match p99={p99:F2}ms exceeds target {SlowPathMatchTargetMs}ms");
    }

    Console.WriteLine();
    if (errors.Count > 0)
    {
        Console.WriteLine("REGRESSION FAILURES:");
        foreach (var e in errors) Console.WriteLine($"  - {e}");
        return 1;
    }

    Console.WriteLine("All spec §13 targets met.");
    return 0;
}

static double Percentile(List<double> sorted, double p)
{
    var s = sorted.OrderBy(x => x).ToList();
    var idx = (int)Math.Ceiling(p * s.Count) - 1;
    return s[Math.Max(0, Math.Min(idx, s.Count - 1))];
}
