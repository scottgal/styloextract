using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Cli.Shared.Commands;

public static class MonitorCommand
{
    public static Command Build()
    {
        var urlsOpt = new Option<string>("--urls") { Required = true, Description = "Path to a newline-delimited file of URLs to monitor." };
        var storeOpt = new Option<string>("--store") { Required = true, Description = "Path to the SQLite template store." };
        var intervalOpt = new Option<TimeSpan>("--interval") { DefaultValueFactory = _ => TimeSpan.FromMinutes(60), Description = "Poll interval (default 01:00:00). Press Ctrl-C to exit." };
        var webhookOpt = new Option<string?>("--webhook") { DefaultValueFactory = _ => null, Description = "Optional URL to POST each NDJSON event to." };
        var prettyOpt = new Option<bool>("--pretty") { DefaultValueFactory = _ => false, Description = "Write indented JSON (one event, multi-line) instead of compact NDJSON." };
        var keyOpt = new Option<string?>("--host-hash-key") { DefaultValueFactory = _ => null, Description = "HMAC key for host hashing. Use --host-hash-key for persistent matching across process restarts." };

        var cmd = new Command("monitor", "Watch a list of URLs and emit NDJSON template-version events to stdout. Press Ctrl-C to stop.");
        cmd.Add(urlsOpt);
        cmd.Add(storeOpt);
        cmd.Add(intervalOpt);
        cmd.Add(webhookOpt);
        cmd.Add(prettyOpt);
        cmd.Add(keyOpt);

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var urlsFile = pr.GetValue(urlsOpt)!;
            var store = pr.GetValue(storeOpt)!;
            var interval = pr.GetValue(intervalOpt);
            var webhook = pr.GetValue(webhookOpt);
            var pretty = pr.GetValue(prettyOpt);
            var key = pr.GetValue(keyOpt);

            var urlList = (await File.ReadAllLinesAsync(urlsFile, ct))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
                .ToList();

            var services = new ServiceCollection();
            using var sink = new MonitorEventSink(Console.Out, webhook, pretty);
            services.AddSingleton<ITemplateVersionEventSink>(sink);
            services.AddStyloExtract(o =>
            {
                o.StorePath = store;
                o.HostHashKey = key;
            });
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();

            using var http = new HttpClient();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var url in urlList)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            var html = await http.GetStringAsync(url, ct);
                            await extractor.ExtractAsync(html, new Uri(url));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await Console.Error.WriteLineAsync($"{url}: {ex.Message}");
                        }
                    }

                    await Task.Delay(interval, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl-C: exit cleanly.
            }

            return 0;
        });

        return cmd;
    }
}
