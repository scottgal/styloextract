using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.Core;

namespace StyloExtract.Cli.Shared.Commands;

public static class ExtractCommand
{
    public static Command Build()
    {
        var source = new Argument<string>("source") { Description = "Path to an HTML file or a https:// URL." };
        var jsonOpt = new Option<bool>("--json") { Description = "Output JSON instead of Markdown." };
        var profileOpt = new Option<ExtractionProfile>("--profile") { DefaultValueFactory = _ => ExtractionProfile.RagFull };
        var storeOpt = new Option<string>("--store") { DefaultValueFactory = _ => "styloextract-templates.db" };
        var keyOpt = new Option<string?>("--host-hash-key") { DefaultValueFactory = _ => null };
        var hostOpt = new Option<string?>("--host-override") { Description = "Synthetic host name for the template store when extracting a file (default: derived from the file path)." };

        var cmd = new Command("extract", "Extract a single page.");
        cmd.Add(source);
        cmd.Add(jsonOpt);
        cmd.Add(profileOpt);
        cmd.Add(storeOpt);
        cmd.Add(keyOpt);
        cmd.Add(hostOpt);
        cmd.SetAction(async (ParseResult pr) =>
        {
            var src = pr.GetValue(source)!;
            var useJson = pr.GetValue(jsonOpt);
            var profile = pr.GetValue(profileOpt);
            var store = pr.GetValue(storeOpt)!;
            var key = pr.GetValue(keyOpt);
            var hostOverride = pr.GetValue(hostOpt);

            var services = new ServiceCollection();
            services.AddStyloExtract(o =>
            {
                o.StorePath = store;
                o.HostHashKey = key;
                o.DefaultProfile = profile;
            });
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();

            var (html, uri) = await LoadHttpOrFileAsync(src);
            if (html is null)
            {
                return 1;
            }

            // For file inputs, sourceUri is null and the host hash defaults to "".
            // All file-based extractions would then share one host bucket, producing
            // cross-template false positives in slow-path cosine matching. Derive a
            // synthetic host from the file path (or the --host-override flag if provided).
            var resolvedHost = hostOverride
                ?? (uri is null ? "file:" + Path.GetFileNameWithoutExtension(src) : null);

            var result = await extractor.ExtractAsync(html, uri,
                new ExtractionOptions { Profile = profile, HostOverride = resolvedHost });

            if (useJson)
            {
                await Console.Out.WriteLineAsync(
                    JsonSerializer.Serialize(result, StyloExtractSerializerContextPretty.Default.ExtractionResult));
            }
            else
            {
                await Console.Out.WriteAsync(result.Markdown);
            }

            return 0;
        });
        return cmd;
    }

    // Internal helper used by both this command and the Playwright extension.
    public static async Task<(string? Html, Uri? Uri)> LoadHttpOrFileAsync(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            using var client = new HttpClient();
            return (await client.GetStringAsync(uri), uri);
        }

        return (await File.ReadAllTextAsync(source), null);
    }
}
