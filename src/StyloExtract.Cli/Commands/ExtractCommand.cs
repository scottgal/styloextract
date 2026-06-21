using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.Playwright;

namespace StyloExtract.Cli.Commands;

public static class ExtractCommand
{
    public static Command Build()
    {
        var source = new Argument<string>("source") { Description = "Path to an HTML file or a https:// URL." };
        var jsonOpt = new Option<bool>("--json") { Description = "Output JSON instead of Markdown." };
        var profileOpt = new Option<ExtractionProfile>("--profile") { DefaultValueFactory = _ => ExtractionProfile.RagFull };
        var storeOpt = new Option<string>("--store") { DefaultValueFactory = _ => "styloextract-templates.db" };
        var keyOpt = new Option<string?>("--host-hash-key") { DefaultValueFactory = _ => null };
        var renderedOpt = new Option<bool>("--rendered", "-r") { Description = "Use Playwright to fetch client-side-rendered HTML (auto-installs browsers on first use)." };

        var cmd = new Command("extract", "Extract a single page.");
        cmd.Add(source);
        cmd.Add(jsonOpt);
        cmd.Add(profileOpt);
        cmd.Add(storeOpt);
        cmd.Add(keyOpt);
        cmd.Add(renderedOpt);
        cmd.SetAction(async (ParseResult pr) =>
        {
            var src = pr.GetValue(source)!;
            var json = pr.GetValue(jsonOpt);
            var profile = pr.GetValue(profileOpt);
            var store = pr.GetValue(storeOpt)!;
            var key = pr.GetValue(keyOpt);
            var rendered = pr.GetValue(renderedOpt);

            var services = new ServiceCollection();
            services.AddStyloExtract(o =>
            {
                o.StorePath = store;
                o.HostHashKey = key;
                o.DefaultProfile = profile;
            });
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();

            var (html, uri) = await LoadAsync(src, rendered);
            if (html is null)
            {
                // LoadAsync already printed an error message.
                return 1;
            }

            var result = await extractor.ExtractAsync(html, uri, new ExtractionOptions { Profile = profile });

            if (json)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                await Console.Out.WriteAsync(result.Markdown);
            }

            return 0;
        });
        return cmd;
    }

    private static async Task<(string? Html, Uri? Uri)> LoadAsync(string source, bool rendered)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            if (rendered)
            {
                if (!PlaywrightInstaller.BrowsersAvailable())
                {
                    Console.Error.WriteLine("Browsers not installed. Installing chromium...");
                    var exit = PlaywrightInstaller.EnsureBrowsersInstalled("chromium");
                    if (exit != 0)
                    {
                        Console.Error.WriteLine($"Browser install failed with exit code {exit}. Run 'install-browsers' manually and retry.");
                        return (null, null);
                    }
                }

                await using var fetcher = new PlaywrightHtmlFetcher();
                var result = await fetcher.FetchAsync(uri, new RenderOptions());
                return (result.Html, result.FinalUri);
            }

            using var client = new HttpClient();
            return (await client.GetStringAsync(uri), uri);
        }

        if (rendered)
        {
            Console.Error.WriteLine("--rendered ignored for local files.");
        }

        return (await File.ReadAllTextAsync(source), null);
    }
}
