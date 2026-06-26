using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.Core;

namespace StyloExtract.Cli.Shared.Commands;

/// <summary>
/// <c>stylo-extract sitemap</c>: crawl one or more starting URLs, extract each page with
/// <see cref="ExtractionProfile.Sitemap"/>, follow internal nav links to a bounded depth,
/// and emit a markdown tree of titles + URLs to stdout (or <c>--out</c>).
///
/// <para>
/// End-to-end demo of the role-based extraction architecture for non-RAG use cases:
/// the crawler sees only Title + Heading + nav blocks (the body is filtered out by the
/// Sitemap profile), and traverses outbound links from those blocks. Safety caps live
/// in the CLI flags (<c>--max-depth</c>, <c>--max-pages</c>, <c>--delay-ms</c>) so even
/// an accidental run against a giant site is bounded.
/// </para>
/// </summary>
public static class SitemapCommand
{
    public static Command Build()
    {
        var urlsArg = new Argument<string[]>("urls")
        {
            Description = "One or more starting URLs.",
            Arity = ArgumentArity.OneOrMore,
        };
        var outOpt = new Option<string?>("--out")
        {
            DefaultValueFactory = _ => null,
            Description = "Write the markdown tree to a file instead of stdout.",
        };
        var maxDepthOpt = new Option<int>("--max-depth")
        {
            DefaultValueFactory = _ => 3,
            Description = "Maximum crawl depth from each starting URL (default 3).",
        };
        var maxPagesOpt = new Option<int>("--max-pages")
        {
            DefaultValueFactory = _ => 50,
            Description = "Maximum pages fetched across the whole crawl (default 50).",
        };
        var delayMsOpt = new Option<int>("--delay-ms")
        {
            DefaultValueFactory = _ => 1000,
            Description = "Politeness delay between requests in milliseconds (default 1000).",
        };
        var storeOpt = new Option<string>("--store")
        {
            DefaultValueFactory = _ => "styloextract-sitemap.db",
            Description = "Path to the SQLite template store.",
        };

        var cmd = new Command(
            "sitemap",
            "Crawl starting URLs and emit a markdown tree of page titles + internal nav links.");
        cmd.Add(urlsArg);
        cmd.Add(outOpt);
        cmd.Add(maxDepthOpt);
        cmd.Add(maxPagesOpt);
        cmd.Add(delayMsOpt);
        cmd.Add(storeOpt);

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var seeds = pr.GetValue(urlsArg) ?? Array.Empty<string>();
            var outPath = pr.GetValue(outOpt);
            var maxDepth = pr.GetValue(maxDepthOpt);
            var maxPages = pr.GetValue(maxPagesOpt);
            var delayMs = pr.GetValue(delayMsOpt);
            var store = pr.GetValue(storeOpt)!;

            if (seeds.Length == 0)
            {
                await Console.Error.WriteLineAsync("error: at least one starting URL is required");
                return 1;
            }

            var services = new ServiceCollection();
            services.AddStyloExtract(o =>
            {
                o.StorePath = store;
                o.DefaultProfile = ExtractionProfile.Sitemap;
            });
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("stylo-extract-sitemap/1.0 (+https://github.com/mostlylucid)");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            http.Timeout = TimeSpan.FromSeconds(30);

            var output = await CrawlAsync(extractor, http, seeds, maxDepth, maxPages, delayMs, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(outPath))
            {
                await File.WriteAllTextAsync(outPath, output, ct).ConfigureAwait(false);
            }
            else
            {
                await Console.Out.WriteAsync(output);
            }
            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Internal crawl: BFS from each seed, extracting nav-only Markdown per page,
    /// following internal links discovered from each page's nav blocks. Caps total
    /// fetched pages at <paramref name="maxPages"/>, respects <paramref name="maxDepth"/>,
    /// and sleeps <paramref name="delayMs"/> between requests for politeness. Internal
    /// to share with the test harness without spinning up a Process.
    /// </summary>
    public static async Task<string> CrawlAsync(
        ILayoutExtractor extractor,
        HttpClient http,
        IReadOnlyList<string> seeds,
        int maxDepth,
        int maxPages,
        int delayMs,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        bool first = true;
        int pagesFetched = 0;

        foreach (var seed in seeds)
        {
            if (pagesFetched >= maxPages || cancellationToken.IsCancellationRequested) break;
            if (!Uri.TryCreate(seed, UriKind.Absolute, out var seedUri))
            {
                await Console.Error.WriteLineAsync($"warning: skipping invalid URL {seed}");
                continue;
            }
            if (!first) output.AppendLine();
            first = false;
            output.AppendLine($"# {seedUri.Host}");
            output.AppendLine();

            var queue = new Queue<(Uri Url, int Depth)>();
            queue.Enqueue((seedUri, 0));
            while (queue.Count > 0 && pagesFetched < maxPages && !cancellationToken.IsCancellationRequested)
            {
                var (url, depth) = queue.Dequeue();
                var key = Canonicalize(url);
                if (!visited.Add(key)) continue;
                if (depth > maxDepth) continue;

                string html;
                try
                {
                    if (pagesFetched > 0 && delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    html = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    pagesFetched++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync($"warning: fetch failed for {url}: {ex.Message}");
                    continue;
                }

                var result = await extractor.ExtractAsync(
                    html, url,
                    new ExtractionOptions { Profile = ExtractionProfile.Sitemap },
                    cancellationToken).ConfigureAwait(false);

                var title = ExtractTitle(result);
                var indent = new string(' ', depth * 2);
                output.AppendLine($"{indent}- [{title}]({url.PathAndQuery})");

                if (depth < maxDepth)
                {
                    foreach (var link in CollectInternalLinks(result, seedUri))
                    {
                        if (!visited.Contains(Canonicalize(link)))
                        {
                            queue.Enqueue((link, depth + 1));
                        }
                    }
                }
            }
        }
        return output.ToString();
    }

    private static string ExtractTitle(ExtractionResult result)
    {
        var titleBlock = result.Blocks.FirstOrDefault(b => b.Role == BlockRole.Title);
        if (titleBlock is not null && !string.IsNullOrWhiteSpace(titleBlock.Text))
        {
            return Truncate(titleBlock.Text.Trim(), 100);
        }
        if (!string.IsNullOrWhiteSpace(result.Title)) return Truncate(result.Title!, 100);
        return "(untitled)";
    }

    private static IEnumerable<Uri> CollectInternalLinks(ExtractionResult result, Uri seed)
    {
        foreach (var block in result.Blocks)
        {
            // Only follow links that came from nav-ish blocks; the Sitemap profile
            // already filters body content out, but the block list is unfiltered.
            if (block.Role is not (BlockRole.PrimaryNavigation
                or BlockRole.SecondaryNavigation
                or BlockRole.Breadcrumb
                or BlockRole.Header))
            {
                continue;
            }

            foreach (var link in block.Links)
            {
                if (string.IsNullOrWhiteSpace(link.Href)) continue;
                if (link.Href.StartsWith('#')) continue;
                if (link.Href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (link.Href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(seed, link.Href, out var absolute)) continue;
                if (!IsSameHost(absolute, seed)) continue;
                yield return absolute;
            }
        }
    }

    private static bool IsSameHost(Uri a, Uri b)
        => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    // Drop fragment + lowercase path so /Foo and /foo#bar both map to one key.
    // Trailing slash kept distinct as some hosts redirect; preferable to over-merge.
    private static string Canonicalize(Uri url)
        => new UriBuilder(url) { Fragment = "" }.Uri.AbsoluteUri.ToLowerInvariant();

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
